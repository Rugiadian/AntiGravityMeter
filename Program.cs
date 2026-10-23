using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AntiGravityMeter
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        static async Task Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--test")
                {
                    AttachConsole(-1);
                    var collector = new AgyCollector();
                    var m = await collector.CollectMetricsAsync(true);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("=== AntiGravity Quota Results ===");
                    sb.AppendLine($"Plan: {m.UserPlan}");
                    sb.AppendLine($"Status: {m.Status}");
                    foreach (var g in m.Groups)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"[{g.Name}]{(g.IsSynthesized ? " (from per-model quotaInfo)" : "")}");
                        foreach (var b in g.Buckets)
                            sb.AppendLine($"  {b.Title}: {b.RemainingPercent}% ({b.RefreshTimeText})");
                    }
                    sb.AppendLine();
                    sb.AppendLine("[Per-Model Usage]");
                    foreach (var mu in m.Models)
                    {
                        string reset = mu.ResetTime == DateTime.MinValue ? "-" : mu.ResetTime.ToString("yyyy-MM-dd HH:mm");
                        sb.AppendLine($"  {mu.DisplayName,-28} {mu.Provider,-10} {mu.RemainingPercent,3}%  reset {reset}");
                    }
                    string testOutput = sb.ToString();
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_results.txt"), testOutput);
                    Console.WriteLine(testOutput);
                    return;
                }

                ApplicationConfiguration.Initialize();
                Application.Run(new WidgetForm());
            }
            catch (Exception ex)
            {
                try
                {
                    string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                    File.WriteAllText(logFile, ex.ToString());
                }
                catch { }
            }
        }
    }
}
