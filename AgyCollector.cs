using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
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

        // P/Invoke for TCP table lookup
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int pdwSize,
            bool bOrder,
            int ulAf,
            int tableClass,
            uint reserved = 0);

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public byte localPort1;
            public byte localPort2;
            public byte localPort3;
            public byte localPort4;
            public uint remoteAddr;
            public uint remotePort;
            public int owningPid;
            public ushort LocalPort => (ushort)((localPort1 << 8) | localPort2);
        }

        private readonly string _appDataDir;
        private readonly string _summariesDbPath;
        private readonly string _conversationsDirPath;
        private readonly string _historyJsonlPath;
        private readonly string _cliLogPath;
        private readonly string _logsDirPath;

        private readonly HttpClient _cloudHttpClient;
        private readonly HttpClient _lsHttpClient;
        private DateTime _lastQuotaFetchTime = DateTime.MinValue;
        private AgyMetrics _cachedMetrics = new();

        // Cached Language Server Info
        private int _cachedLsPort = 0;
        private string? _cachedLsCsrfToken = null;
        private int _cachedLsPid = 0;

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

            _cloudHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Local language_server uses self-signed HTTPS certificates
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            _lsHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
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

            // 6. Live Quota:
            // Priority 1: Direct Language Server API (Exact match with Antigravity App UI)
            // Priority 2: Fallback to Google CloudCode fetchAvailableModels API
            bool needFetch = forceRefreshQuota || 
                             (DateTime.UtcNow - _lastQuotaFetchTime).TotalSeconds > 5 ||
                             _cachedMetrics.WeeklyLimit.RemainingPercent == 0;

            if (needFetch)
            {
                bool success = await FetchLanguageServerQuotaAsync(metrics);
                if (!success)
                {
                    // Fallback to cloud API
                    await FetchCloudLiveQuotaAsync(metrics);
                }

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

        /// <summary>
        /// Fetches the exact real-time quota summary directly from the Antigravity local Language Server.
        /// This matches 100% with the Antigravity App's "Models & Usage" screen (Weekly % & 5-Hour %).
        /// </summary>
        private async Task<bool> FetchLanguageServerQuotaAsync(AgyMetrics metrics)
        {
            try
            {
                // Ensure we have Language Server PID and CSRF Token
                if (!EnsureLanguageServerInfo(out int pid, out string csrfToken))
                {
                    return false;
                }

                // Find the listening port
                List<int> candidatePorts = GetProcessListeningPorts(pid);
                // Also add standard fallback ports
                if (!candidatePorts.Contains(9120)) candidatePorts.Insert(0, 9120);
                if (_cachedLsPort > 0 && !candidatePorts.Contains(_cachedLsPort)) candidatePorts.Insert(0, _cachedLsPort);

                foreach (int port in candidatePorts)
                {
                    try
                    {
                        string url = $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
                        using var req = new HttpRequestMessage(HttpMethod.Post, url);
                        req.Headers.Add("x-codeium-csrf-token", csrfToken);
                        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                        using var resp = await _lsHttpClient.SendAsync(req);
                        if (!resp.IsSuccessStatusCode) continue;

                        string json = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);

                        if (!doc.RootElement.TryGetProperty("response", out var respProp) ||
                            !respProp.TryGetProperty("groups", out var groupsProp))
                        {
                            continue;
                        }

                        _cachedLsPort = port;

                        // Parse groups for "Gemini Models"
                        foreach (var group in groupsProp.EnumerateArray())
                        {
                            string dispName = group.TryGetProperty("displayName", out var dp) ? dp.GetString() ?? "" : "";
                            if (!dispName.Contains("Gemini", StringComparison.OrdinalIgnoreCase)) continue;

                            if (group.TryGetProperty("buckets", out var bucketsProp))
                            {
                                foreach (var bucket in bucketsProp.EnumerateArray())
                                {
                                    string window = bucket.TryGetProperty("window", out var winProp) ? winProp.GetString() ?? "" : "";
                                    string bucketId = bucket.TryGetProperty("bucketId", out var bIdProp) ? bIdProp.GetString() ?? "" : "";
                                    double remFraction = bucket.TryGetProperty("remainingFraction", out var rfProp) ? rfProp.GetDouble() : 1.0;
                                    string desc = bucket.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
                                    string resetTimeStr = bucket.TryGetProperty("resetTime", out var rtProp) ? rtProp.GetString() ?? "" : "";

                                    DateTime resetTimeUtc = DateTime.MinValue;
                                    if (DateTime.TryParse(resetTimeStr, out var parsedDt))
                                    {
                                        resetTimeUtc = parsedDt.ToUniversalTime();
                                    }

                                    int pct = Math.Clamp((int)Math.Round(remFraction * 100.0), 0, 100);

                                    // Format clean refresh time text
                                    string refreshText = FormatRefreshText(desc, resetTimeUtc);

                                    if (window == "weekly" || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase))
                                    {
                                        metrics.WeeklyLimit.RemainingPercent = pct;
                                        metrics.WeeklyLimit.ResetTime = resetTimeUtc.ToLocalTime();
                                        metrics.WeeklyLimit.RefreshTimeText = refreshText;
                                        metrics.WeeklyLimit.Description = desc;
                                    }
                                    else if (window == "5h" || bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase))
                                    {
                                        metrics.FiveHourLimit.RemainingPercent = pct;
                                        metrics.FiveHourLimit.ResetTime = resetTimeUtc.ToLocalTime();
                                        metrics.FiveHourLimit.RefreshTimeText = refreshText;
                                        metrics.FiveHourLimit.Description = desc;
                                    }
                                }
                            }

                            return true; // Successfully updated Gemini Models quota
                        }
                    }
                    catch
                    {
                        // Try next port
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static string FormatRefreshText(string fullDescription, DateTime resetTimeUtc)
        {
            if (string.IsNullOrWhiteSpace(fullDescription))
            {
                if (resetTimeUtc != DateTime.MinValue)
                {
                    var span = resetTimeUtc - DateTime.UtcNow;
                    if (span.TotalDays >= 1)
                        return $"it will fully refresh in {(int)span.TotalDays} days, {span.Hours} hours.";
                    if (span.TotalHours >= 1)
                        return $"it will fully refresh in {(int)span.TotalHours} hours, {span.Minutes} minutes.";
                    if (span.TotalMinutes > 0)
                        return $"it will fully refresh in {span.Minutes} minutes.";
                }
                return "it will fully refresh shortly.";
            }

            // Extract the "it will fully refresh in..." part if present
            int idx = fullDescription.IndexOf("it will fully refresh in", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                return fullDescription.Substring(idx).TrimEnd('.');
            }

            return fullDescription;
        }

        /// <summary>
        /// Locates running language_server.exe and extracts the CSRF token from its command line.
        /// </summary>
        private bool EnsureLanguageServerInfo(out int pid, out string csrfToken)
        {
            pid = 0;
            csrfToken = "";

            // Check if cached PID is still alive
            if (_cachedLsPid > 0 && !string.IsNullOrEmpty(_cachedLsCsrfToken))
            {
                try
                {
                    var p = Process.GetProcessById(_cachedLsPid);
                    if (!p.HasExited && p.ProcessName.Contains("language_server", StringComparison.OrdinalIgnoreCase))
                    {
                        pid = _cachedLsPid;
                        csrfToken = _cachedLsCsrfToken;
                        return true;
                    }
                }
                catch
                {
                    _cachedLsPid = 0;
                    _cachedLsCsrfToken = null;
                }
            }

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'language_server.exe'");

                foreach (ManagementObject mo in searcher.Get())
                {
                    string? cmdLine = mo["CommandLine"]?.ToString();
                    if (string.IsNullOrEmpty(cmdLine)) continue;

                    var match = Regex.Match(cmdLine, @"--csrf_token\s+([a-zA-Z0-9\-]+)");
                    if (match.Success)
                    {
                        pid = Convert.ToInt32(mo["ProcessId"]);
                        csrfToken = match.Groups[1].Value;

                        _cachedLsPid = pid;
                        _cachedLsCsrfToken = csrfToken;
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>
        /// Retrieves all TCP listening ports owned by a specific process ID using Win32 API.
        /// </summary>
        private static List<int> GetProcessListeningPorts(int pid)
        {
            var ports = new List<int>();
            if (pid <= 0) return ports;

            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER);

            IntPtr pTcpTable = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(pTcpTable, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER) == 0)
                {
                    int rowCount = Marshal.ReadInt32(pTcpTable);
                    IntPtr rowPtr = IntPtr.Add(pTcpTable, sizeof(int));
                    int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                    for (int i = 0; i < rowCount; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                        if (row.owningPid == pid)
                        {
                            ports.Add(row.LocalPort);
                        }
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pTcpTable);
            }

            return ports;
        }

        private async Task FetchCloudLiveQuotaAsync(AgyMetrics metrics)
        {
            string? token = GetKeyringToken();
            if (string.IsNullOrEmpty(token)) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("User-Agent", "antigravity-cli");
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                using var resp = await _cloudHttpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("models", out var modelsProp))
                    {
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

                                metrics.FiveHourLimit.RemainingPercent = percent;
                                metrics.FiveHourLimit.ResetTime = resetTimeUtc.ToLocalTime();
                                metrics.FiveHourLimit.RefreshTimeText = FormatRefreshText("", resetTimeUtc);
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void CollectProcessInfo(AgyMetrics metrics)
        {
            try
            {
                var procs = Process.GetProcessesByName("antigravity");
                if (procs.Length == 0)
                {
                    procs = Process.GetProcessesByName("node");
                }

                Process? targetProc = null;
                double maxMem = 0;

                foreach (var p in procs)
                {
                    try
                    {
                        if (p.WorkingSet64 > maxMem)
                        {
                            maxMem = p.WorkingSet64;
                            targetProc = p;
                        }
                    }
                    catch { }
                }

                if (targetProc != null)
                {
                    metrics.ProcessId = targetProc.Id;
                    metrics.MemoryMb = targetProc.WorkingSet64 / (1024.0 * 1024.0);

                    var now = DateTime.UtcNow;
                    var totalTime = targetProc.TotalProcessorTime;

                    if (_lastCpuCheckTime != DateTime.MinValue && now > _lastCpuCheckTime)
                    {
                        double elapsedMs = (now - _lastCpuCheckTime).TotalMilliseconds;
                        double cpuMs = (totalTime - _lastCpuTotalTime).TotalMilliseconds;
                        int cpuCount = Environment.ProcessorCount;
                        if (elapsedMs > 0 && cpuCount > 0)
                        {
                            metrics.CpuPercent = Math.Clamp((cpuMs / (elapsedMs * cpuCount)) * 100.0, 0, 100);
                        }
                    }

                    _lastCpuCheckTime = now;
                    _lastCpuTotalTime = totalTime;
                    metrics.Status = AgyStatus.Idle;
                }
                else
                {
                    metrics.Status = AgyStatus.Offline;
                }
            }
            catch
            {
                metrics.Status = AgyStatus.Offline;
            }
        }

        private void CollectDbSummaries(AgyMetrics metrics)
        {
            if (!File.Exists(_summariesDbPath)) return;

            try
            {
                using var conn = new SqliteConnection($"Data Source={_summariesDbPath};Mode=ReadOnly;");
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM summaries";
                    metrics.TotalConversations = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT conversation_id, title, preview, step_count, last_modified
                        FROM summaries
                        ORDER BY last_modified DESC
                        LIMIT 5";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var item = new ConvSummaryItem
                        {
                            Id = reader.IsDBNull(0) ? "" : reader.GetString(0),
                            Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Preview = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            StepCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                        };

                        if (!reader.IsDBNull(4))
                        {
                            long unixEpoch = reader.GetInt64(4);
                            if (unixEpoch > 0)
                            {
                                item.LastModified = DateTimeOffset.FromUnixTimeSeconds(unixEpoch).LocalDateTime;
                            }
                        }

                        metrics.RecentConversations.Add(item);
                    }
                }
            }
            catch
            {
            }
        }

        private void CollectActiveConversationDetails(AgyMetrics metrics)
        {
            if (!Directory.Exists(_conversationsDirPath)) return;

            try
            {
                var dirInfo = new DirectoryInfo(_conversationsDirPath);
                FileInfo? latestFile = null;

                foreach (var file in dirInfo.GetFiles("*.jsonl"))
                {
                    if (latestFile == null || file.LastWriteTimeUtc > latestFile.LastWriteTimeUtc)
                    {
                        latestFile = file;
                    }
                }

                if (latestFile != null)
                {
                    metrics.ActiveConvId = Path.GetFileNameWithoutExtension(latestFile.Name);
                    metrics.LastActivityTime = latestFile.LastWriteTime;

                    if (metrics.RecentConversations.Count > 0 && metrics.RecentConversations[0].Id == metrics.ActiveConvId)
                    {
                        metrics.ActiveConvTitle = metrics.RecentConversations[0].Title;
                    }

                    int stepCount = 0;
                    string lastModel = "";

                    using (var fs = new FileStream(latestFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            stepCount++;

                            if (line.Contains("\"model\""))
                            {
                                var match = Regex.Match(line, @"""model""\s*:\s*""([^""]+)""");
                                if (match.Success)
                                {
                                    lastModel = match.Groups[1].Value;
                                }
                            }
                        }
                    }

                    metrics.ActiveConvSteps = stepCount;
                    if (!string.IsNullOrEmpty(lastModel))
                    {
                        metrics.ActiveModel = lastModel;
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
                int total = 0;
                int today = 0;
                var todayDate = DateTime.Today;

                using (var fs = new FileStream(_historyJsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        total++;

                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("timestamp", out var tsProp))
                            {
                                if (DateTime.TryParse(tsProp.GetString(), out var dt))
                                {
                                    if (dt.ToLocalTime().Date == todayDate)
                                    {
                                        today++;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                metrics.TotalPrompts = total;
                metrics.TodayPrompts = today;
            }
            catch
            {
            }
        }

        private void CollectApiCallStats(AgyMetrics metrics)
        {
            try
            {
                int totalCalls = 0;
                int todayCalls = 0;
                var todayDate = DateTime.Today;

                if (File.Exists(_cliLogPath))
                {
                    var fileInfo = new FileInfo(_cliLogPath);
                    if (fileInfo.Length < 100 * 1024 * 1024)
                    {
                        using var fs = new FileStream(_cliLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Contains("Sending request to") || line.Contains("StreamGenerateContent") || line.Contains("googleapis.com"))
                            {
                                totalCalls++;
                                if (line.Length > 20 && DateTime.TryParse(line.Substring(0, 19), out var dt))
                                {
                                    if (dt.ToLocalTime().Date == todayDate) todayCalls++;
                                }
                            }
                        }
                    }
                }

                metrics.TotalApiCalls = totalCalls;
                metrics.TodayApiCalls = todayCalls;
            }
            catch
            {
            }
        }
    }
}
