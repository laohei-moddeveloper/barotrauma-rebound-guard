using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;
using MoonSharp.Interpreter;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class NativeModHotPathTests
    {
        private static int checks;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        private static void Native(string method, params object[] args) => AccessTools.Method(typeof(NativeModHotPaths), method).Invoke(null, args);
        private static Character MakeCharacter(string species, bool humanAI)
        {
            var character = (Character)RuntimeHelpers.GetUninitializedObject(typeof(Character));
            var parameters = (CharacterParams)RuntimeHelpers.GetUninitializedObject(typeof(CharacterParams));
            AccessTools.PropertySetter(typeof(CharacterParams), "SpeciesName").Invoke(parameters, new object[] { species.ToIdentifier() });
            AccessTools.PropertySetter(typeof(CharacterParams), "UseHumanAI").Invoke(parameters, new object[] { humanAI });
            AccessTools.Field(typeof(Character), "Params").SetValue(character, parameters);
            return character;
        }
        private static (double ms, long bytes) Measure(Action run, int count)
        {
            for (int i=0;i<500;i++) run();
            var samples = new List<(double ms,long bytes)>();
            for(int repeat=0;repeat<7;repeat++)
            {
                long allocated=GC.GetAllocatedBytesForCurrentThread(), start=Stopwatch.GetTimestamp();
                for(int i=0;i<count;i++) run();
                samples.Add(((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency, GC.GetAllocatedBytesForCurrentThread()-allocated));
            }
            return samples.OrderBy(sample=>sample.ms).ElementAt(3);
        }
        private static void ForeignPrefix() { }
        private delegate bool Prefix(Character character, ref float result);
        public static object Run()
        {
            var fixtures = new[] { MakeCharacter("human",false),MakeCharacter("human",true),MakeCharacter("mudraptor_player",true),MakeCharacter("crawler",false) };
            var getter=AccessTools.PropertyGetter(typeof(Character),"IsHuman");
            var nativeGetter=(Func<Character,bool>)getter.CreateDelegate(typeof(Func<Character,bool>));
            var foreign=new Harmony("reboundguard.native.hotpath.foreign");
            bool enabled=Settings.NativeKnownModHotPaths; int restorations=0;
            try
            {
                Native("Start"); Settings.NativeKnownModHotPaths=false;
                Check(!NativeModHotPaths.InstallMudraptorIsHuman(()=>restorations++),"disabled bridge refuses ownership; Lua can fall back");
                Settings.NativeKnownModHotPaths=true;
                foreign.Patch(getter,prefix:new HarmonyMethod(typeof(NativeModHotPathTests),nameof(ForeignPrefix)));
                Check(!NativeModHotPaths.InstallMudraptorIsHuman(()=>restorations++),"unknown getter hook retains original Lua instead of overriding it");
                foreign.UnpatchSelf();
                var raw = fixtures.Select(character=>nativeGetter(character)).ToArray();
                Check(NativeModHotPaths.InstallMudraptorIsHuman(()=>restorations++),"native getter postfix installs");
                for(int i=0;i<fixtures.Length;i++) Check(nativeGetter(fixtures[i]) == (raw[i] || fixtures[i].Params.UseHumanAI),"native original-result OR live AI parameter");
                AccessTools.PropertySetter(typeof(CharacterParams),"UseHumanAI").Invoke(fixtures[3].Params,new object[]{true});
                Check(nativeGetter(fixtures[3]),"runtime AI parameter change is visible immediately; no classification cache");
                AccessTools.PropertySetter(typeof(CharacterParams),"UseHumanAI").Invoke(fixtures[3].Params,new object[]{false});
                Check(!nativeGetter(fixtures[3]),"runtime AI parameter removal is visible immediately");
                Check(NativeModHotPaths.InstallTSMBuffHooks(()=>restorations++),"native TSM prefixes emit against actual methods");
                var hasBuff=(Func<Character,bool>)AccessTools.Method(typeof(NativeModHotPaths),"HasTSMBuff").CreateDelegate(typeof(Func<Character,bool>));
                Check(!hasBuff(null) && !hasBuff(fixtures[0]),"missing character/health do not invent buffs");
                var health=(CharacterHealth)RuntimeHelpers.GetUninitializedObject(typeof(CharacterHealth));
                FieldInfo afflictionsField=AccessTools.Field(typeof(CharacterHealth),"afflictions");
                var afflictions=(IDictionary)Activator.CreateInstance(afflictionsField.FieldType);
                afflictionsField.SetValue(health,afflictions);
                AccessTools.PropertySetter(typeof(Character),"CharacterHealth").Invoke(fixtures[3],new object[]{health});
                var movement=(Prefix)AccessTools.Method(typeof(NativeModHotPaths),"BeforeMovement").CreateDelegate(typeof(Prefix));
                foreach(string id in new[]{"tsm_straight_grip_buff","tsm_buff_dsdj_exo_bag","tsm_analgesic_buff","unrelated"})
                {
                    var prefab=(AfflictionPrefab)RuntimeHelpers.GetUninitializedObject(typeof(AfflictionPrefab));
                    FieldInfo identifier=AccessTools.Field(typeof(AfflictionPrefab),"<Identifier>k__BackingField") ?? AccessTools.Field(typeof(AfflictionPrefab),"Identifier");
                    identifier.SetValue(prefab,id.ToIdentifier());
                    var affliction=(Affliction)RuntimeHelpers.GetUninitializedObject(typeof(Affliction));
                    AccessTools.Field(typeof(Affliction),"Prefab").SetValue(affliction,prefab);
                    afflictions.Clear();afflictions.Add(affliction,null);
                    foreach(float strength in new[]{0f,.999f,1f,1.001f,float.NaN})
                    {
                        AccessTools.Field(typeof(Affliction),"_strength").SetValue(affliction,strength);
                        bool expected=id!="unrelated" && strength>=1;
                        Check(hasBuff(fixtures[3])==expected,"live affliction name and threshold agree with Lua: "+id);
                        float result=7f; bool runOriginal=movement(fixtures[3],ref result);
                        Check(runOriginal==!expected && result==(expected?0f:7f),"native movement skip exactly matches existing buff rule");
                    }
                    afflictions.Clear();Check(!hasBuff(fixtures[3]),"buff removal visible immediately");
                }

                UserData.RegisterType(typeof(Character),InteropAccessMode.Reflection);
                UserData.RegisterType(typeof(CharacterParams),InteropAccessMode.Reflection);
                var script=new Script();
                DynValue callback=script.DoString("return function(instance, ptable) return ptable.OriginalReturnValue or instance.Params.UseHumanAI end");
                Character selected=fixtures[2]; bool observed=false;
                Action lua=()=>{
                    var table=new Table(script); table.Set("OriginalReturnValue",DynValue.NewBoolean(false));
                    observed=script.Call(callback,selected,table).Boolean;
                };
                Action native=()=>{observed=nativeGetter(selected);};
                lua();Check(observed,"original Lua rule evaluates true on the same actual Character object");
                native();Check(observed,"native wrapper evaluates true on the same actual Character object");
                var slow=Measure(lua,10000); var fast=Measure(native,10000);
                Check(fast.bytes == 0,"patched real IsHuman getter allocates no per-call managed objects");
                Native("Stop",false);
                Check(!nativeGetter(selected),"native uninstall restores the vanilla classification");
                Check(Harmony.GetPatchInfo(getter)?.Owners.Contains("barotrauma.reboundguard.nativecompat.v040") != true,"native owner removed on stop");
                CheckLuaBridge("Mudraptor-IsHuman.lua",true,1);
                CheckLuaBridge("TSM-accessoriesbuff.lua",true,2);
                CheckLuaBridge("Mudraptor-IsHuman.lua",false,1);
                CheckLuaBridge("TSM-accessoriesbuff.lua",false,2);
                Type exit=typeof(Plugin).Assembly.GetType("ReboundGuard.ExitLifetimePatch");
                if(exit!=null)
                {
                    Check((bool)AccessTools.Method(exit,"SupportedCodeMatches").Invoke(null,null),"actual client OnExiting fingerprint matches audited lifetime defect");
                    var originalExit=AccessTools.Method(typeof(GameMain),"OnExiting");
                    var patched=((IEnumerable<CodeInstruction>)AccessTools.Method(exit,"Transpiler").Invoke(null,new object[]{PatchProcessor.GetOriginalInstructions(originalExit)})).ToList();
                    int defer=patched.FindIndex(i=>i.operand is MethodInfo method && method.Name=="DeferDispose");
                    int quit=patched.FindIndex(i=>i.operand is MethodInfo method && method.Name=="Quit");
                    int flush=patched.FindIndex(i=>i.operand is MethodInfo method && method.Name=="FlushDeferred");
                    int steam=patched.FindIndex(i=>i.operand is MethodInfo method && method.Name=="ShutDown");
                    Check(defer>=0 && defer<quit && quit<flush && flush<steam,"Quit executes before deferred disposal, before Steam shutdown");
                    Check(patched.Count-PatchProcessor.GetOriginalInstructions(originalExit).Count==1,"only one flush instruction added; original Quit stays intact");
                    var harmony=new Harmony("barotrauma.reboundguard.experimental.v040");
                    try { AccessTools.Method(exit,"Install").Invoke(null,new object[]{harmony}); Check(Harmony.GetPatchInfo(originalExit)?.Owners.Contains(harmony.Id)==true,"real OnExiting transpiler and exception finalizer emit"); }
                    finally { harmony.UnpatchSelf(); }
                }
                return new { checks, calls_per_batch=10000, lua_rule_ms=slow.ms, native_getter_ms=fast.ms,
                    lua_rule_bytes=slow.bytes,native_getter_bytes=fast.bytes,speedup=slow.ms/fast.ms,
                    note="Original Lua rule via real MoonSharp and actual Character objects versus actual Harmony-patched getter. Lua fixture omits the complete LuaCs generated prefix/postfix wrapper overhead. Not whole-game FPS or a new live combat benchmark." };
            }
            finally { foreign.UnpatchSelf(); Native("Stop",false); Settings.NativeKnownModHotPaths=enabled; }
        }
        private static void CheckLuaBridge(string file,bool useNative,int fallbackHooks)
        {
            Native("Start");Settings.NativeKnownModHotPaths=useNative;
            UserData.RegisterType(typeof(NativeModHotPaths),InteropAccessMode.Reflection);
            var script=new Script();int patches=0;string bridgeError="";
            script.Options.DebugPrint=text=>bridgeError+=text;
            var luaUserData=new Table(script);
            luaUserData.Set("RegisterType",DynValue.NewCallback((context,args)=>DynValue.Nil));
            luaUserData.Set("CreateStatic",DynValue.NewCallback((context,args)=>UserData.CreateStatic(typeof(NativeModHotPaths))));
            script.Globals.Set("LuaUserData",DynValue.NewTable(luaUserData));
            var hook=new Table(script);var kinds=new Table(script);kinds.Set("After",DynValue.NewNumber(1));kinds.Set("Before",DynValue.NewNumber(0));
            hook.Set("HookMethodType",DynValue.NewTable(kinds));hook.Set("Patch",DynValue.NewCallback((context,args)=>{patches++;return DynValue.Nil;}));
            script.Globals.Set("Hook",DynValue.NewTable(hook));
            script.DoString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"compat",file)));
            Check(patches==(useNative?0:fallbackHooks),"Lua source selects native path or registers original fallback: "+file+"; "+bridgeError);
            if(useNative)
            {
                var callbacks=(IDictionary)AccessTools.Field(typeof(NativeModHotPaths),"Fallbacks").GetValue(null);
                Check(callbacks.Count==1,"Lua function marshals into one retained restoration Action");
                var callback=(Action)callbacks.Values.Cast<object>().Single();
                Native("Stop",false);callback();
                Check(patches==fallbackHooks,"retained Lua Action restores original hooks: "+file);
            }
            Native("Stop",false);
        }
    }
}
