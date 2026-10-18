using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Barotrauma;

namespace ReboundGuard
{
    internal static class DiagnosticLog
    {
        private static BlockingCollection<string> pending;
        private static Thread writer;
        private static string failure;
        internal static string CurrentPath { get; private set; }
        internal static string Failure => Volatile.Read(ref failure);
        internal static void Start()
        {
            if (!Settings.SaveDiagnostics || pending != null) return;
            CurrentPath = null; failure = null;
            try
            {
                string role = typeof(Item).Assembly.GetName().Name == "Barotrauma" ? "client" : "server";
                string directory = Path.Combine(AppContext.BaseDirectory, "ReboundGuardReports");
                Directory.CreateDirectory(directory);
                CurrentPath = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + role + "_" + Environment.ProcessId + ".log");
                failure = null;
                var queue = new BlockingCollection<string>(256);
                pending = queue;
                string path = CurrentPath;
                writer = new Thread(() => Write(queue, path)) { IsBackground = true, Name = "ReboundGuard diagnostics" };
                writer.Start();
            }
            catch (Exception exception) { pending = null; writer = null; failure = exception.GetType().Name + ": " + exception.Message; }
        }
        internal static void Append(string message)
        {
            var queue = pending;
            if (queue == null || queue.IsAddingCompleted) return;
            try { queue.TryAdd(DateTime.UtcNow.ToString("o") + " " + message); }
            catch (InvalidOperationException) { }
        }
        private static void Write(BlockingCollection<string> queue, string path)
        {
            try
            {
                using (var file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
                {
                    foreach (string line in queue.GetConsumingEnumerable())
                    { file.WriteLine(line); if (queue.Count == 0) file.Flush(); }
                }
            }
            catch (Exception exception) { Volatile.Write(ref failure, exception.GetType().Name + ": " + exception.Message); }
        }
        internal static void Stop()
        {
            var queue = pending; pending = null;
            if (queue == null) return;
            queue.CompleteAdding();
            writer?.Join(500);
            writer = null;
        }
    }
}
