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
                    string testOutput = $"=== AntiGravity Quota Results ===\n" +
                        $"Plan: {m.UserPlan}\n" +
                        $"Status: {m.Status}\n" +
                        $"Weekly Limit Remaining: {m.WeeklyLimit.RemainingPercent}% ({m.WeeklyLimit.RefreshTimeText})\n" +
                        $"Five Hour Limit Remaining: {m.FiveHourLimit.RemainingPercent}% ({m.FiveHourLimit.RefreshTimeText})\n";
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
