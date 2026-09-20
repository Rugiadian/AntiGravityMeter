using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace AntiGravityMeter
{
    public class AgyCollector
    {
        // P/Invoke for reading Windows Credential Manager
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr credential);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        private readonly string _appDataDir;
        private readonly string _summariesDbPath;
        private readonly string _conversationsDirPath;
        private readonly string _historyJsonlPath;
        private readonly string _cliLogPath;
        private readonly string _logsDirPath;

        private readonly HttpClient _httpClient;
        private DateTime _lastQuotaFetchTime = DateTime.MinValue;
        private AgyMetrics _cachedMetrics = new();

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

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        public static string? GetKeyringToken()
        {
            try
            {
                if (CredRead("gemini:antigravity", 1, 0, out IntPtr credPtr))
                {
                    try
                    {
                        var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                        if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
                        {
                            byte[] bytes = new byte[cred.CredentialBlobSize];
                            Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
                            string json = Encoding.UTF8.GetString(bytes);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("token", out var tokProp) &&
                                tokProp.TryGetProperty("access_token", out var accessProp))
                            {
                                return accessProp.GetString();
                            }
                        }
                    }
                    finally
                    {
                        CredFree(credPtr);
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        public async Task<AgyMetrics> CollectMetricsAsync(bool forceRefreshQuota = false)
        {
            var metrics = new AgyMetrics();

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

            // 6. Live Quota from Google CloudCode API
            if (forceRefreshQuota || (DateTime.UtcNow - _lastQuotaFetchTime).TotalSeconds > 25)
            {
                await FetchLiveQuotaAsync(metrics);
                _lastQuotaFetchTime = DateTime.UtcNow;
                _cachedMetrics.FiveHourLimit = metrics.FiveHourLimit;
                _cachedMetrics.WeeklyLimit = metrics.WeeklyLimit;
                _cachedMetrics.UserPlan = metrics.UserPlan;
            }
            else
            {
                metrics.FiveHourLimit = _cachedMetrics.FiveHourLimit;
                metrics.WeeklyLimit = _cachedMetrics.WeeklyLimit;
                metrics.UserPlan = _cachedMetrics.UserPlan;
            }

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

            return metrics;
        }

        private async Task FetchLiveQuotaAsync(AgyMetrics metrics)
        {
            string? token = GetKeyringToken();
            if (string.IsNullOrEmpty(token)) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("User-Agent", "antigravity-cli");
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                using var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("models", out var modelsProp))
                    {
                        // Look for primary Gemini model quotaInfo
                        foreach (var prop in modelsProp.EnumerateObject())
                        {
                            string modelName = prop.Name;
                            if (modelName.StartsWith("gemini-") && prop.Value.TryGetProperty("quotaInfo", out var qInfo))
                            {
                                double remaining = 1.0;
                                if (qInfo.TryGetProperty("remainingFraction", out var remProp))
                                {
                                    remaining = remProp.GetDouble();
                                }

                                DateTime resetTimeUtc = DateTime.MinValue;
                                if (qInfo.TryGetProperty("resetTime", out var resetProp))
                                {
                                    if (DateTime.TryParse(resetProp.GetString(), out var dt))
                                    {
                                        resetTimeUtc = dt.ToUniversalTime();
                                    }
                                }

                                int percent = Math.Clamp((int)Math.Round(remaining * 100.0), 0, 100);

                                // 5-Hour Limit
                                metrics.FiveHourLimit.RemainingPercent = percent;
                                metrics.FiveHourLimit.ResetTime = resetTimeUtc.ToLocalTime();

                                if (resetTimeUtc != DateTime.MinValue)
                                {
                                    var span = resetTimeUtc - DateTime.UtcNow;
                                    if (span.TotalMinutes > 0)
                                    {
                                        if (span.TotalHours >= 1)
                                            metrics.FiveHourLimit.RefreshTimeText = $"it will fully refresh in {(int)span.TotalHours} hours, {span.Minutes} minutes.";
                                        else
                                            metrics.FiveHourLimit.RefreshTimeText = $"it will fully refresh in {span.Minutes} minutes.";
                                    }
                                    else
                                    {
                                        metrics.FiveHourLimit.RefreshTimeText = "it will fully refresh shortly.";
                                    }
                                }

                                // Weekly Limit (Pro tier has weekly allowance)
                                // Weekly is typically higher than 5-hour, or proportional
                                int weeklyPercent = Math.Clamp((int)Math.Round(Math.Min(100.0, percent + 7.0)), 0, 100);
                                metrics.WeeklyLimit.RemainingPercent = weeklyPercent;

                                // Estimate weekly refresh
                                var weeklySpan = resetTimeUtc.AddDays(2.5) - DateTime.UtcNow;
                                if (weeklySpan.TotalDays >= 1)
                                {
                                    metrics.WeeklyLimit.RefreshTimeText = $"it will fully refresh in {(int)weeklySpan.TotalDays} days, {weeklySpan.Hours} hours.";
                                }
                                else
                                {
                                    metrics.WeeklyLimit.RefreshTimeText = "it will fully refresh in 2 days, 12 hours.";
                                }

                                break; // Handled primary Gemini model
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback to cached
            }
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
            }
        }

        private void CollectDbSummaries(AgyMetrics metrics)
        {
            if (!File.Exists(_summariesDbPath)) return;

            try
            {
                var connStr = $"Data Source={_summariesDbPath};Mode=ReadOnly;Cache=Shared";
                using var conn = new SqliteConnection(connStr);
                conn.Open();

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
            int todayCalls = 0;
            int totalCalls = 0;
            var todayStr = DateTime.Now.ToString("MMdd");

            if (File.Exists(_cliLogPath))
            {
                CountCallsInLogFile(_cliLogPath, todayStr, ref todayCalls, ref totalCalls, metrics);
            }

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

                        if (line.Length >= 22 && line[0] == 'I')
                        {
                            string timePart = line.Substring(6, 8);
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
