using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace AntiGravityMeter
{
    public class AgyCollector
    {
        private readonly string _appDataDir;
        private readonly string _summariesDbPath;
        private readonly string _conversationsDirPath;
        private readonly string _historyJsonlPath;
        private readonly string _cliLogPath;
        private readonly string _logsDirPath;

        // CPU calculation helper
        private DateTime _lastCpuCheckTime = DateTime.UtcNow;
        private TimeSpan _lastCpuTotalTime = TimeSpan.Zero;

        public AgyCollector()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _appDataDir = Path.Combine(userProfile, ".gemini", "antigravity-cli");
            _summariesDbPath = Path.Combine(_appDataDir, "conversation_summaries.db");
            _conversationsDirPath = Path.Combine(_appDataDir, "conversations");
            _historyJsonlPath = Path.Combine(_appDataDir, "history.jsonl");
            _cliLogPath = Path.Combine(_appDataDir, "cli.log");
            _logsDirPath = Path.Combine(_appDataDir, "log");
        }

        public async Task<AgyMetrics> CollectMetricsAsync()
        {
            var metrics = new AgyMetrics();

            await Task.Run(() =>
            {
                // 1. Process Metrics
                CollectProcessInfo(metrics);

                // 2. Summary Database Metrics
                CollectDbSummaries(metrics);

                // 3. Active Conversation Details (Model, Steps)
                CollectActiveConversationDetails(metrics);

                // 4. Prompt History from history.jsonl
                CollectPromptHistory(metrics);

                // 5. API Call statistics from cli.log & log/*.log
                CollectApiCallStats(metrics);

                // Determine precise status
                if (metrics.Status != AgyStatus.Offline)
                {
                    TimeSpan sinceLastActive = DateTime.Now - metrics.LastActivityTime;
                    if (metrics.CpuPercent > 5.0 || sinceLastActive.TotalSeconds < 8)
                    {
                        metrics.Status = AgyStatus.Busy;
                    }
                    else
                    {
                        metrics.Status = AgyStatus.Idle;
                    }
                }
            });

            return metrics;
        }

        private void CollectProcessInfo(AgyMetrics metrics)
        {
            try
            {
                var procs = Process.GetProcessesByName("agy");
                if (procs.Length == 0)
                {
                    procs = Process.GetProcessesByName("Antigravity");
                }

                if (procs.Length > 0)
                {
                    var mainProc = procs[0];
                    metrics.Status = AgyStatus.Idle;
                    metrics.ProcessId = mainProc.Id;

                    mainProc.Refresh();
                    metrics.MemoryMb = Math.Round(mainProc.WorkingSet64 / (1024.0 * 1024.0), 1);

                    // CPU Calculation
                    var now = DateTime.UtcNow;
                    var cpuTime = mainProc.TotalProcessorTime;
                    var timeSpan = now - _lastCpuCheckTime;

                    if (timeSpan.TotalMilliseconds > 500 && _lastCpuTotalTime != TimeSpan.Zero)
                    {
                        double cpuUsed = (cpuTime - _lastCpuTotalTime).TotalMilliseconds;
                        double totalAvailable = timeSpan.TotalMilliseconds * Environment.ProcessorCount;
                        metrics.CpuPercent = Math.Clamp(Math.Round((cpuUsed / totalAvailable) * 100.0, 1), 0.0, 100.0);
                    }

                    _lastCpuCheckTime = now;
                    _lastCpuTotalTime = cpuTime;
                }
                else
                {
                    metrics.Status = AgyStatus.Offline;
                    metrics.MemoryMb = 0;
                    metrics.CpuPercent = 0;
                }
            }
            catch
            {
                // Process could exit or be restricted
            }
        }

        private void CollectDbSummaries(AgyMetrics metrics)
        {
            if (!File.Exists(_summariesDbPath)) return;

            try
            {
                // Use ReadOnly mode with pooled connection
                var connStr = $"Data Source={_summariesDbPath};Mode=ReadOnly;Cache=Shared";
                using var conn = new SqliteConnection(connStr);
                conn.Open();

                // Totals
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(step_count), 0) FROM conversation_summaries;";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        metrics.TotalConversations = reader.GetInt32(0);
                        metrics.TotalSteps = reader.GetInt32(1);
                    }
                }

                // Recent Conversations
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT conversation_id, title, preview, step_count, last_modified_time 
                        FROM conversation_summaries 
                        ORDER BY last_modified_time DESC 
                        LIMIT 6;";
                    using var reader = cmd.ExecuteReader();
                    metrics.RecentConversations.Clear();

                    bool first = true;
                    while (reader.Read())
                    {
                        string id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        string title = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        string preview = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        int steps = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                        DateTime modTime = DateTime.MinValue;

                        if (!reader.IsDBNull(4))
                        {
                            if (DateTime.TryParse(reader.GetString(4), out var dt))
                            {
                                modTime = dt.ToLocalTime();
                            }
                        }

                        if (string.IsNullOrWhiteSpace(title))
                        {
                            title = string.IsNullOrWhiteSpace(preview) ? id : preview;
                        }

                        if (first)
                        {
                            metrics.ActiveConvId = id;
                            metrics.ActiveConvTitle = title;
                            metrics.ActiveConvSteps = steps;
                            if (modTime > metrics.LastActivityTime)
                            {
                                metrics.LastActivityTime = modTime;
                            }
                            first = false;
                        }

                        metrics.RecentConversations.Add(new ConvSummaryItem
                        {
                            Id = id,
                            Title = title,
                            Preview = preview,
                            StepCount = steps,
                            LastModified = modTime
                        });
                    }
                }
            }
            catch
            {
                // Safely ignore locking during live SQLite writes
            }
        }

        private void CollectActiveConversationDetails(AgyMetrics metrics)
        {
            if (string.IsNullOrEmpty(metrics.ActiveConvId)) return;

            string convDb = Path.Combine(_conversationsDirPath, $"{metrics.ActiveConvId}.db");
            if (!File.Exists(convDb)) return;

            try
            {
                var connStr = $"Data Source={convDb};Mode=ReadOnly;Cache=Shared";
                using var conn = new SqliteConnection(connStr);
                conn.Open();

                // Extract latest model used from gen_metadata
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT data FROM gen_metadata ORDER BY idx DESC LIMIT 1;";
                var blob = cmd.ExecuteScalar() as byte[];
                if (blob != null && blob.Length > 0)
                {
                    string text = Encoding.ASCII.GetString(blob);
                    var match = Regex.Match(text, @"(gemini-[0-9\.]+[a-zA-Z0-9_\-]*|claude-[0-9\.]+[a-zA-Z0-9_\-]*)");
                    if (match.Success)
                    {
                        metrics.ActiveModel = match.Value;
                    }
                }
            }
            catch
            {
                // Fallback to existing
            }
        }

        private void CollectPromptHistory(AgyMetrics metrics)
        {
            if (!File.Exists(_historyJsonlPath)) return;

            try
            {
                using var fs = new FileStream(_historyJsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);

                string? line;
                int totalPrompts = 0;
                int todayPrompts = 0;
                var today = DateTime.Today;
                var recentList = new List<PromptHistoryItem>();

                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    totalPrompts++;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        long ts = 0;
                        if (root.TryGetProperty("timestamp", out var tsProp))
                        {
                            ts = tsProp.GetInt64();
                        }

                        DateTime itemTime = ts > 0 
                            ? DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime 
                            : DateTime.MinValue;

                        if (itemTime >= today)
                        {
                            todayPrompts++;
                        }

                        string text = root.TryGetProperty("display", out var dispProp) ? dispProp.GetString() ?? "" : "";
                        string convId = root.TryGetProperty("conversationId", out var cProp) ? cProp.GetString() ?? "" : "";

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            recentList.Add(new PromptHistoryItem
                            {
                                Text = text,
                                Timestamp = itemTime,
                                ConversationId = convId
                            });

                            if (itemTime > metrics.LastActivityTime)
                            {
                                metrics.LastActivityTime = itemTime;
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                metrics.TotalPrompts = totalPrompts;
                metrics.TodayPrompts = todayPrompts;

                // Keep last 5 recent prompts (newest first)
                recentList.Reverse();
                if (recentList.Count > 5) recentList.RemoveRange(5, recentList.Count - 5);
                metrics.RecentPrompts = recentList;
            }
            catch
            {
            }
        }

        private void CollectApiCallStats(AgyMetrics metrics)
        {
            // Collect from cli.log first
            int todayCalls = 0;
            int totalCalls = 0;
            var todayStr = DateTime.Now.ToString("MMdd"); // Format in log: I0921

            if (File.Exists(_cliLogPath))
            {
                CountCallsInLogFile(_cliLogPath, todayStr, ref todayCalls, ref totalCalls, metrics);
            }

            // Also scan recent log files in log directory
            if (Directory.Exists(_logsDirPath))
            {
                try
                {
                    var dirInfo = new DirectoryInfo(_logsDirPath);
                    var logFiles = dirInfo.GetFiles("cli-*.log");
                    foreach (var f in logFiles)
                    {
                        if (f.FullName == _cliLogPath) continue;
                        CountCallsInLogFile(f.FullName, todayStr, ref todayCalls, ref totalCalls, metrics);
                    }
                }
                catch
                {
                }
            }

            metrics.TodayApiCalls = todayCalls;
            metrics.TotalApiCalls = totalCalls;
        }

        private void CountCallsInLogFile(string path, string todayStr, ref int todayCount, ref int totalCount, AgyMetrics metrics)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Contains("streamGenerateContent"))
                    {
                        totalCount++;
                        if (line.Length > 5 && line.Substring(1, 4) == todayStr)
                        {
                            todayCount++;
                        }

                        // Try to parse log timestamp: e.g. I0921 03:30:01.846161
                        if (line.Length >= 22 && line[0] == 'I')
                        {
                            string timePart = line.Substring(6, 8); // 03:30:01
                            if (TimeSpan.TryParse(timePart, out var ts))
                            {
                                var logTime = DateTime.Today.Add(ts);
                                if (logTime > metrics.LastActivityTime)
                                {
                                    metrics.LastActivityTime = logTime;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
