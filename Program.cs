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
                    var m = await collector.CollectMetricsAsync();
                    Console.WriteLine("=== AgyCollector Test Results ===");
                    Console.WriteLine($"Status: {m.Status}");
                    Console.WriteLine($"ProcessId: {m.ProcessId}, RAM: {m.MemoryMb} MB, CPU: {m.CpuPercent}%");
                    Console.WriteLine($"Total Conversations: {m.TotalConversations}");
                    Console.WriteLine($"Total Steps: {m.TotalSteps}");
                    Console.WriteLine($"Today Prompts: {m.TodayPrompts} / Total: {m.TotalPrompts}");
                    Console.WriteLine($"Today API Calls: {m.TodayApiCalls} / Total: {m.TotalApiCalls}");
                    Console.WriteLine($"Active Conv: {m.ActiveConvTitle} ({m.ActiveConvSteps} steps)");
                    Console.WriteLine($"Active Model: {m.ActiveModel}");
                    Console.WriteLine($"5-Hour Limit: {m.FiveHourLimit.RemainingPercent}% ({m.FiveHourLimit.RefreshTimeText})");
                    Console.WriteLine($"Weekly Limit: {m.WeeklyLimit.RemainingPercent}% ({m.WeeklyLimit.RefreshTimeText})");
                    Console.WriteLine($"Last Activity: {m.LastActivityTime:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine("\nRecent Conversations:");
                    foreach (var c in m.RecentConversations)
                    {
                        Console.WriteLine($"  [{c.StepCount} steps] {c.Title} ({c.LastModified:MM-dd HH:mm})");
                    }
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
