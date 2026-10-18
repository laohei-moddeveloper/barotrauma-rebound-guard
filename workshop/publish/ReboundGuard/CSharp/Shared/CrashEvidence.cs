using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ReboundGuard
{
    internal static class CrashEvidence
    {
        private static string path;
        private static int written;
        internal static void Start()
        {
            written = 0;
            path = DiagnosticLog.CurrentPath == null ? null : DiagnosticLog.CurrentPath + ".exceptions.txt";
            AppDomain.CurrentDomain.UnhandledException += Unhandled;
        }
        internal static void Stop() { AppDomain.CurrentDomain.UnhandledException -= Unhandled; path = null; }
        private static void Unhandled(object sender, UnhandledExceptionEventArgs args) => Record("UnhandledException; terminating=" + args.IsTerminating, args.ExceptionObject as Exception, true);
        internal static void Record(string source, Exception error, bool unhandled = false)
        {
            string target = path;
            if (target == null || error == null) return;
            if (Interlocked.Increment(ref written) > 4 && !unhandled) return;
            // Fatal path only: do not depend on the asynchronous log queue surviving.
            // No exception is marked handled and no gameplay method is skipped.
            try { File.AppendAllText(target, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + source + Environment.NewLine + error + Environment.NewLine); }
            catch { }
        }
        internal static string MemoryReport()
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            using (Process process = Process.GetCurrentProcess())
                return string.Format(CultureInfo.InvariantCulture, "memory: managed_live_mb={0:F1}; last_gc_heap_mb={1:F1}; fragmented_mb={2:F1}; private_mb={3:F1}; working_set_mb={4:F1}; gc_available_mb={5:F1}; gc_memory_load_mb={6:F1}",
                    GC.GetTotalMemory(false) / 1048576.0, info.HeapSizeBytes / 1048576.0, info.FragmentedBytes / 1048576.0,
                    process.PrivateMemorySize64 / 1048576.0, process.WorkingSet64 / 1048576.0, info.TotalAvailableMemoryBytes / 1048576.0, info.MemoryLoadBytes / 1048576.0);
        }
    }
}
