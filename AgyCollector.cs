using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const uint MIB_TCP_STATE_LISTEN = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public int owningPid;
            public ushort LocalPort => (ushort)(((localPort & 0xFF) << 8) | ((localPort >> 8) & 0xFF));
        }

        private readonly HttpClient _cloudHttpClient;
        private readonly HttpClient _lsHttpClient;
        private DateTime _lastQuotaFetchTime = DateTime.MinValue;
        private AgyMetrics _cachedMetrics = new();

        // Cached Language Server Info
        private int _cachedLsPort = 0;
        private string? _cachedLsCsrfToken = null;
        private int _cachedLsPid = 0;

        public AgyCollector()
        {
            _cloudHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

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

            // 1. Process Status (Checks agy CLI, Antigravity IDE, language_server)
            CollectProcessStatus(metrics);

            // 2. Exact Quota Collection
            // Method 1: Google CloudCode retrieveUserQuotaSummary endpoint (Works for CLI & IDE directly)
            // Method 2: Local Antigravity Language Server RPC
            // Plus: fetchAvailableModels for per-model usage (and groups missing from the summary, e.g. Gemini)
            bool needFetch = forceRefreshQuota ||
                             (DateTime.UtcNow - _lastQuotaFetchTime).TotalSeconds > 4 ||
                             _cachedMetrics.Groups.Count == 0;

            if (needFetch)
            {
                // 1순위: 로컬 Language Server (Gemini 주간/5h, Claude 주간/5h, Argon 등 모든 그룹 완벽 제공)
                // 2순위: Google CloudCode 직접 호출 (IDE 미실행 시 fallback)
                var modelsTask = FetchCloudAvailableModelsAsync();
                bool success = await FetchLanguageServerQuotaAsync(metrics);
                if (!success)
                {
                    success = await FetchCloudQuotaSummaryAsync(metrics);
                }
                await modelsTask;

                // Fall back to last known model list if this round's request failed
                var models = modelsTask.Result ?? _cachedMetrics.Models;
                metrics.Models = models;

                if (success || models.Count > 0)
                {
                    AddSynthesizedGroups(metrics, models, summaryAvailable: success);
                }
                else
                {
                    // Keep the last known values if every source failed this round
                    metrics.Groups = _cachedMetrics.Groups;
                }

                SyncLegacyGeminiLimits(metrics);

                _lastQuotaFetchTime = DateTime.UtcNow;
                _cachedMetrics.WeeklyLimit = metrics.WeeklyLimit;
                _cachedMetrics.FiveHourLimit = metrics.FiveHourLimit;
                _cachedMetrics.UserPlan = metrics.UserPlan;
                _cachedMetrics.Groups = metrics.Groups;
                _cachedMetrics.Models = metrics.Models;
            }
            else
            {
                metrics.WeeklyLimit = _cachedMetrics.WeeklyLimit;
                metrics.FiveHourLimit = _cachedMetrics.FiveHourLimit;
                metrics.UserPlan = _cachedMetrics.UserPlan;
                metrics.Groups = _cachedMetrics.Groups;
                metrics.Models = _cachedMetrics.Models;
            }

            return metrics;
        }

        private static readonly string[] QuotaSummaryEndpoints = new[]
        {
            "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
            "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"
        };

        private static readonly string[] AvailableModelsEndpoints = new[]
        {
            "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
            "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels"
        };

        /// <summary>
        /// Calls the official CloudCode retrieveUserQuotaSummary endpoint using the user's stored OAuth token.
        /// This is the exact same API called by the Antigravity backend, providing exact Weekly & 5-Hour limits.
        /// </summary>
        private async Task<bool> FetchCloudQuotaSummaryAsync(AgyMetrics metrics)
        {
            string? token = GetKeyringToken();
            if (string.IsNullOrEmpty(token)) return false;

            foreach (var url in QuotaSummaryEndpoints)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.Add("User-Agent", "antigravity-cli");
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    using var resp = await _cloudHttpClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) continue;

                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (ParseQuotaSummaryJson(doc.RootElement, metrics))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        /// <summary>
        private async Task<bool> FetchLanguageServerQuotaAsync(AgyMetrics metrics)
        {
            try
            {
                if (!EnsureLanguageServerInfo(out int pid, out string csrfToken))
                {
                    return false;
                }

                List<int> candidatePorts = GetProcessListeningPorts(pid);
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

                        if (ParseQuotaSummaryJson(doc.RootElement, metrics))
                        {
                            _cachedLsPort = port;
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// Fetches every available model with its quotaInfo via fetchAvailableModels.
        /// Effort variants ("(Low)", "(Medium)", "(High)") are merged into one entry.
        /// Returns null if the request failed.
        /// </summary>
        private async Task<List<ModelUsage>?> FetchCloudAvailableModelsAsync()
        {
            string? token = GetKeyringToken();
            if (string.IsNullOrEmpty(token)) return null;

            foreach (var url in AvailableModelsEndpoints)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.Add("User-Agent", "antigravity-cli");
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    using var resp = await _cloudHttpClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) continue;

                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    return ParseAvailableModelsJson(doc.RootElement);
                }
                catch
                {
                }
            }

            return null;
        }

        private static readonly Regex EffortSuffixRegex = new(
            @"\s*\((Extra Low|Low|Medium|High|Minimal|Thinking)\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<ModelUsage> ParseAvailableModelsJson(JsonElement root)
        {
            var result = new Dictionary<string, ModelUsage>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("models", out var modelsProp) || modelsProp.ValueKind != JsonValueKind.Object)
            {
                return new List<ModelUsage>();
            }

            // Order used by Antigravity's own model picker
            var sortIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("agentModelSorts", out var sortsProp) && sortsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var sort in sortsProp.EnumerateArray())
                {
                    if (!sort.TryGetProperty("groups", out var sg) || sg.ValueKind != JsonValueKind.Array) continue;
                    foreach (var g in sg.EnumerateArray())
                    {
                        if (!g.TryGetProperty("modelIds", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
                        foreach (var id in ids.EnumerateArray())
                        {
                            string? s = id.GetString();
                            if (!string.IsNullOrEmpty(s) && !sortIndex.ContainsKey(s)) sortIndex[s] = sortIndex.Count;
                        }
                    }
                }
            }

            foreach (var prop in modelsProp.EnumerateObject())
            {
                var m = prop.Value;
                string displayName = m.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                string apiProvider = m.TryGetProperty("apiProvider", out var ap) ? ap.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(displayName)) continue;            // internal / tab-completion models
                if (apiProvider.Contains("INTERNAL", StringComparison.OrdinalIgnoreCase)) continue;
                if (!m.TryGetProperty("quotaInfo", out var qInfo)) continue;

                double remaining = qInfo.TryGetProperty("remainingFraction", out var remProp) &&
                                   remProp.ValueKind == JsonValueKind.Number ? remProp.GetDouble() : 1.0;
                DateTime resetLocal = qInfo.TryGetProperty("resetTime", out var rtProp)
                    ? ParseUtc(rtProp.GetString()) is DateTime u && u != DateTime.MinValue ? u.ToLocalTime() : DateTime.MinValue
                    : DateTime.MinValue;

                string provider = m.TryGetProperty("modelProvider", out var mp) ? FormatProvider(mp.GetString()) : "";
                string baseName = EffortSuffixRegex.Replace(displayName, "").Trim();
                int pct = Math.Clamp((int)Math.Round(remaining * 100.0), 0, 100);
                int order = sortIndex.TryGetValue(prop.Name, out int idx) ? idx : int.MaxValue;

                if (!result.TryGetValue(baseName, out var usage))
                {
                    usage = new ModelUsage
                    {
                        DisplayName = baseName,
                        Provider = provider,
                        RemainingPercent = pct,
                        ResetTime = resetLocal,
                        SortOrder = order
                    };
                    result[baseName] = usage;
                }
                else
                {
                    // Variants normally share a pool; be conservative and keep the lowest value
                    usage.RemainingPercent = Math.Min(usage.RemainingPercent, pct);
                    if (resetLocal != DateTime.MinValue &&
                        (usage.ResetTime == DateTime.MinValue || resetLocal < usage.ResetTime))
                    {
                        usage.ResetTime = resetLocal;
                    }
                    usage.SortOrder = Math.Min(usage.SortOrder, order);
                }
                usage.ModelIds.Add(prop.Name);
            }

            return result.Values
                .OrderBy(u => ProviderRank(u.Provider))
                .ThenBy(u => u.SortOrder)
                .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int ProviderRank(string provider) => provider switch
        {
            "Google" => 0,
            "Anthropic" => 1,
            "OpenAI" => 2,
            _ => 3
        };

        private static string FormatProvider(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Replace("MODEL_PROVIDER_", "", StringComparison.OrdinalIgnoreCase);
            return s.ToUpperInvariant() switch
            {
                "GOOGLE" => "Google",
                "ANTHROPIC" => "Anthropic",
                "OPENAI" => "OpenAI",
                _ => s.Length > 1 ? char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant() : s
            };
        }

        private static DateTime ParseUtc(string? s)
        {
            if (!string.IsNullOrEmpty(s) &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }
            return DateTime.MinValue;
        }

        /// <summary>
        /// The quota summary currently only reports some pools (e.g. "Claude and GPT models").
        /// Pools that are missing are synthesized from per-model quotaInfo.
        /// </summary>
        private static void AddSynthesizedGroups(AgyMetrics metrics, List<ModelUsage> models, bool summaryAvailable)
        {
            if (!summaryAvailable) metrics.Groups = new List<QuotaGroup>();

            bool hasGemini = metrics.Groups.Exists(g => g.Name.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
            if (!hasGemini)
            {
                var gemini = models.FindAll(m => m.Provider == "Google");
                var group = SynthesizeGroup("Gemini models", gemini);
                if (group != null) metrics.Groups.Insert(0, group);
            }

            if (!summaryAvailable)
            {
                var others = models.FindAll(m => m.Provider != "Google");
                var group = SynthesizeGroup("Claude and GPT models", others);
                if (group != null) metrics.Groups.Add(group);
            }
        }

        private static QuotaGroup? SynthesizeGroup(string name, List<ModelUsage> models)
        {
            if (models.Count == 0) return null;

            int minPct = 100;
            DateTime earliestReset = DateTime.MinValue;
            foreach (var m in models)
            {
                minPct = Math.Min(minPct, m.RemainingPercent);
                if (m.ResetTime != DateTime.MinValue && (earliestReset == DateTime.MinValue || m.ResetTime < earliestReset))
                {
                    earliestReset = m.ResetTime;
                }
            }

            DateTime resetUtc = earliestReset == DateTime.MinValue ? DateTime.MinValue : earliestReset.ToUniversalTime();
            return new QuotaGroup
            {
                Name = name,
                IsSynthesized = true,
                Description = "Models within this group: " + string.Join(", ", models.ConvertAll(m => m.DisplayName)),
                Buckets =
                {
                    new ModelQuotaItem
                    {
                        Title = "Quota Remaining",
                        RemainingPercent = minPct,
                        ResetTime = earliestReset,
                        RefreshTimeText = FormatRefreshText("", resetUtc)
                    }
                }
            };
        }

        /// <summary>
        /// Keeps the legacy WeeklyLimit / FiveHourLimit properties pointing at the Gemini pool.
        /// </summary>
        private static void SyncLegacyGeminiLimits(AgyMetrics metrics)
        {
            var gemini = metrics.Groups.Find(g => g.Name.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
            if (gemini == null) return;

            foreach (var b in gemini.Buckets)
            {
                if (b.Window == "weekly") metrics.WeeklyLimit = b;
                else metrics.FiveHourLimit = b;
            }
        }

        private static bool ParseQuotaSummaryJson(JsonElement root, AgyMetrics metrics)
        {
            JsonElement target = root;
            if (root.TryGetProperty("response", out var respEl))
            {
                target = respEl;
            }

            if (!target.TryGetProperty("groups", out var groupsProp) || groupsProp.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var groups = new List<QuotaGroup>();
            foreach (var group in groupsProp.EnumerateArray())
            {
                var qg = new QuotaGroup
                {
                    Name = group.TryGetProperty("displayName", out var dp) ? dp.GetString() ?? "" : "",
                    Description = group.TryGetProperty("description", out var gd) ? gd.GetString() ?? "" : ""
                };

                if (group.TryGetProperty("buckets", out var bucketsProp) && bucketsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bucket in bucketsProp.EnumerateArray())
                    {
                        string window = bucket.TryGetProperty("window", out var winProp) ? winProp.GetString() ?? "" : "";
                        string bucketId = bucket.TryGetProperty("bucketId", out var bIdProp) ? bIdProp.GetString() ?? "" : "";
                        string title = bucket.TryGetProperty("displayName", out var bdn) ? bdn.GetString() ?? "" : "";
                        double remFraction = bucket.TryGetProperty("remainingFraction", out var rfProp) &&
                                             rfProp.ValueKind == JsonValueKind.Number ? rfProp.GetDouble() : 1.0;
                        string desc = bucket.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
                        DateTime resetTimeUtc = ParseUtc(bucket.TryGetProperty("resetTime", out var rtProp) ? rtProp.GetString() : null);

                        if (window == "" && bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase)) window = "weekly";
                        if (window == "" && bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase)) window = "5h";
                        if (string.IsNullOrEmpty(title))
                        {
                            title = window == "weekly" ? "Weekly Limit Remaining"
                                  : window == "5h" ? "Five Hour Limit Remaining"
                                  : "Limit Remaining";
                        }

                        qg.Buckets.Add(new ModelQuotaItem
                        {
                            Title = title,
                            Window = window,
                            Description = desc,
                            RemainingPercent = Math.Clamp((int)Math.Round(remFraction * 100.0), 0, 100),
                            ResetTime = resetTimeUtc == DateTime.MinValue ? DateTime.MinValue : resetTimeUtc.ToLocalTime(),
                            RefreshTimeText = FormatRefreshText(desc, resetTimeUtc)
                        });
                    }
                }

                // Weekly first, then 5-hour
                qg.Buckets.Sort((a, b) => (a.Window == "weekly" ? 0 : 1).CompareTo(b.Window == "weekly" ? 0 : 1));
                if (qg.Buckets.Count > 0) groups.Add(qg);
            }

            if (groups.Count == 0) return false;
            groups.Sort((a, b) => GroupRank(a.Name).CompareTo(GroupRank(b.Name)));
            metrics.Groups = groups;
            return true;
        }

        private static int GroupRank(string name)
        {
            if (name.Contains("Gemini", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.Contains("Claude", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
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

            int idx = fullDescription.IndexOf("it will fully refresh in", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                return fullDescription.Substring(idx).TrimEnd('.');
            }

            return fullDescription;
        }

        private bool EnsureLanguageServerInfo(out int pid, out string csrfToken)
        {
            pid = 0;
            csrfToken = "";

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

        private static List<int> GetProcessListeningPorts(int pid)
        {
            var ports = new List<int>();
            if (pid <= 0) return ports;

            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

            IntPtr pTcpTable = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(pTcpTable, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int rowCount = Marshal.ReadInt32(pTcpTable);
                    IntPtr rowPtr = IntPtr.Add(pTcpTable, 4);
                    int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                    for (int i = 0; i < rowCount; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                        if (row.owningPid == pid && (row.state == MIB_TCP_STATE_LISTEN || row.state == 5))
                        {
                            if (!ports.Contains(row.LocalPort))
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

        private void CollectProcessStatus(AgyMetrics metrics)
        {
            try
            {
                var agyProcs = Process.GetProcessesByName("agy");
                if (agyProcs.Length > 0)
                {
                    metrics.Status = AgyStatus.Busy;
                    metrics.ProcessId = agyProcs[0].Id;
                    return;
                }

                var antigravityProcs = Process.GetProcessesByName("antigravity");
                if (antigravityProcs.Length > 0)
                {
                    metrics.Status = AgyStatus.Idle;
                    metrics.ProcessId = antigravityProcs[0].Id;
                    return;
                }

                metrics.Status = AgyStatus.Idle;
            }
            catch
            {
                metrics.Status = AgyStatus.Offline;
            }
        }
    }
}
