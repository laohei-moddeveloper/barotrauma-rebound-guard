using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;

public static class TraceAgent
{
    public static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        try { return Execute(args).GetAwaiter().GetResult(); }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task<int> Execute(string[] args)
    {
        int pid = int.Parse(args[0]), seconds = int.Parse(args[1]); string path = args[2];
        Console.WriteLine("Collecting runtime GC/allocation samples from PID " + pid + " for " + seconds + " seconds.");
        var providers = new[] { new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x40000001),
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational, 0) };
        if(pid > 0) using(var session = new DiagnosticsClient(pid).StartEventPipeSession(providers, true, 64))
        {
            using(var stream = File.Create(path))
            {
                Task copy = session.EventStream.CopyToAsync(stream);
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                session.Stop(); await copy;
            }
        }
        Console.WriteLine("Captured " + new FileInfo(path).Length + " bytes; parsing allocation samples.");
        string etlx = TraceLog.CreateFromEventPipeDataFile(path);
        using(var log = new TraceLog(etlx))
        {
            var types = new Dictionary<string, long>(); var sites = new Dictionary<string, long>();
            var stackSamples = new Dictionary<string, long>(); var cpuStacks = new Dictionary<string, int>();
            var categories = new Dictionary<string, long>();
            int samples = 0, cpuSamples = 0; long bytes = 0;
            foreach(var item in log.Events)
            {
                if(item.EventName == "GC/AllocationTick")
                {
                    string type = item.PayloadByName("TypeName")?.ToString() ?? "unknown";
                    object amount = item.PayloadByName("AllocationAmount64") ?? item.PayloadByName("AllocationAmount");
                    long size = Convert.ToInt64(amount); samples++; bytes += size;
                    types[type] = types.GetValueOrDefault(type) + size;
                    var frames = new List<string>();
                    for(var stack = item.CallStack(); stack != null && frames.Count < 60; stack = stack.Caller)
                        frames.Add(stack.CodeAddress.FullMethodName ?? stack.CodeAddress.ToString());
                    string site = frames.FirstOrDefault(frame => !frame.StartsWith("System.") && !frame.StartsWith("Microsoft.")) ?? "unknown";
                    sites[site] = sites.GetValueOrDefault(site) + size;
                    string key = type + "\n" + string.Join("\n", frames);
                    stackSamples[key] = stackSamples.GetValueOrDefault(key) + size;
                    string category = frames.Any(frame => frame.Contains("get_IsHuman")) ? "IsHuman" :
                        frames.Any(frame => frame.Contains("tsm_remove_movement_penalty")) ? "TSM movement buff" :
                        frames.Any(frame => frame.Contains("tsm_remove_aim_wobble")) ? "TSM aim buff" :
                        frames.Any(frame => frame.Contains("CallFunctionSafe")) ? "other Lua callbacks" : "other";
                    categories[category] = categories.GetValueOrDefault(category) + size;
                }
                else if(item.ProviderName == "Microsoft-DotNETCore-SampleProfiler")
                {
                    cpuSamples++;
                    var frames = new List<string>();
                    for(var stack = item.CallStack(); stack != null && frames.Count < 60; stack = stack.Caller)
                        frames.Add(stack.CodeAddress.FullMethodName ?? stack.CodeAddress.ToString());
                    string key = string.Join("\n", frames); cpuStacks[key] = cpuStacks.GetValueOrDefault(key) + 1;
                }
            }
            var result = new { pid, seconds, samples, sampled_bytes = bytes, cpu_samples = cpuSamples,
                types = types.OrderByDescending(pair => pair.Value).Take(40).ToArray(),
                sites = sites.OrderByDescending(pair => pair.Value).Take(40).ToArray(),
                allocation_categories = categories.OrderByDescending(pair=>pair.Value).ToArray(),
                allocation_stacks = stackSamples.OrderByDescending(pair => pair.Value).Take(50).ToArray(),
                cpu_stacks = cpuStacks.OrderByDescending(pair => pair.Value).Take(30).ToArray(),
                note = "EventPipe sampled allocation weights and managed CPU stacks; collection can add overhead. Snapshot is not a live combat benchmark or crash proof." };
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { samples, bytes, cpuSamples, top_types = result.types.Take(5) }));
        }
        return 0;
    }
}
