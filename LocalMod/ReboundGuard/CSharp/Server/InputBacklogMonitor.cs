using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;

namespace ReboundGuard
{
    internal static class InputBacklogMonitor
    {
        private static int trimEvents,discardedInputs,maxQueued;
        internal static void Install(Harmony harmony)
        {
            if(typeof(Character).Assembly.GetName().Version!=new Version(1,13,4,0))return;
            harmony.Patch(AccessTools.Method(typeof(Character),"ServerReadInput"),
                transpiler:new HarmonyMethod(typeof(InputBacklogMonitor),nameof(Transpiler)),
                postfix:new HarmonyMethod(typeof(InputBacklogMonitor),nameof(AfterRead)));
        }
        private static void AfterRead(Character __instance)
        {
            maxQueued=Math.Max(maxQueued,__instance.memInput.Count);
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code=instructions.ToList();
            MethodInfo remove=typeof(List<Character.NetInputMem>).GetMethod("RemoveRange");
            if(code.Count(i=>i.Calls(remove))!=1)throw new InvalidOperationException("Unknown input queue trim IL");
            foreach(var instruction in code)
            {
                if(!instruction.Calls(remove))continue;
                instruction.opcode=OpCodes.Call;
                instruction.operand=AccessTools.Method(typeof(InputBacklogMonitor),nameof(ObserveTrim));
            }
            return code;
        }
        private static void ObserveTrim(List<Character.NetInputMem> queue,int start,int count)
        {
            maxQueued=Math.Max(maxQueued,queue.Count);
            trimEvents++;discardedInputs+=count;
            // Observe the original operation exactly; do not change drop policy.
            queue.RemoveRange(start,count);
        }
        internal static string Report()
        {
            string result="server input backlog: max_queue="+maxQueued+"; vanilla_trim_events="+trimEvents+"; vanilla_discarded_inputs="+discardedInputs;
            maxQueued=trimEvents=discardedInputs=0;
            return result;
        }
    }
}
