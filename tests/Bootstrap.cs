using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
public static class Bootstrap
{
    public static int Main(string[] arguments)
    {
        string folder=arguments[0];
        AssemblyLoadContext.Default.Resolving += (context,name)=> {
            string local=Path.Combine(AppContext.BaseDirectory,name.Name+".dll");
            if(File.Exists(local))return context.LoadFromAssemblyPath(local);
            string path=Path.Combine(folder,name.Name+".dll");
            return File.Exists(path)?context.LoadFromAssemblyPath(path):null;
        };
        try
        {
            if(arguments.Length>2 && arguments[2]!="All")
            {
                string suite = arguments[2] switch {
                    "Bridge" => "ReboundGuardTests.LuaBridgeTests",
                    "Loading" => "ReboundGuardTests.LoadingLifecycleTests",
                    "Native" => "ReboundGuardTests.NativeModHotPathTests",
                    "Queue" => "ReboundGuardTests.QueueChurnTests",
                    "Compatibility" => "ReboundGuardTests.CompatibilityTests",
                    "Dispatch" => "ReboundGuardTests.LuaDispatchSemanticsTests",
                    "Scaffold" => "ReboundGuardTests.LuaScaffoldBenchmarkTests",
                    "StatusTags" => "ReboundGuardTests.LiveStatusTagTests",
                    "Nearby" => "ReboundGuardTests.NearbyItemIndexTests",
                    "Lifecycle" => "ReboundGuardTests.PluginLifecycleTests",
                    "Stress" => "ReboundGuardTests.LuaDispatchStressTests",
                    _ => throw new ArgumentException("Unknown suite: " + arguments[2])
                };
                object report=Assembly.GetExecutingAssembly().GetType(suite).GetMethod("Run").Invoke(null,null);
                File.WriteAllText(arguments[1],System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine("PASS: "+suite+"; "+arguments[1]);return 0;
            }
            string result=(string)Assembly.GetExecutingAssembly().GetType("ReboundGuardTests.DeepRegression").GetMethod("Run").Invoke(null,new object[]{arguments[1]});
            Console.WriteLine(result);
            return 0;
        }
        catch(Exception exception) { Console.Error.WriteLine(exception.GetBaseException()); return 1; }
    }
}
