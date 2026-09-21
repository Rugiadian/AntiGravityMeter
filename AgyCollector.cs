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
            // Method 3: Fallback to fetchAvailableModels
            bool needFetch = forceRefreshQuota ||
                             (DateTime.UtcNow - _lastQuotaFetchTime).TotalSeconds > 4 ||
                             _cachedMetrics.WeeklyLimit.RemainingPercent == 100 && _cachedMetrics.WeeklyLimit.RefreshTimeText == "";

            if (needFetch)
            {
                bool success = await FetchCloudQuotaSummaryAsync(metrics);
                if (!success)
                {
                    success = await FetchLanguageServerQuotaAsync(metrics);
                }
                if (!success)
                {
                    await FetchCloudAvailableModelsFallbackAsync(metrics);
                }

                _lastQuotaFetchTime = DateTime.UtcNow;
                _cachedMetrics.WeeklyLimit = metrics.WeeklyLimit;
                _cachedMetrics.FiveHourLimit = metrics.FiveHourLimit;
                _cachedMetrics.UserPlan = metrics.UserPlan;
            }
            else
            {
                metrics.WeeklyLimit = _cachedMetrics.WeeklyLimit;
                metrics.FiveHourLimit = _cachedMetrics.FiveHourLimit;
                metrics.UserPlan = _cachedMetrics.UserPlan;
            }

            return metrics;
        }

        /// <summary>
        /// Calls the official CloudCode retrieveUserQuotaSummary endpoint using the user's stored OAuth token.
        /// This is the exact same API called by the Antigravity backend, providing exact Weekly & 5-Hour limits.
        /// </summary>
        private async Task<bool> FetchCloudQuotaSummaryAsync(AgyMetrics metrics)
        {
            string? token = GetKeyringToken();
            if (string.IsNullOrEmpty(token)) return false;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("User-Agent", "antigravity-cli");
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                using var resp = await _cloudHttpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return false;

                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return ParseQuotaSummaryJson(doc.RootElement, metrics);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Secondary method: queries local language_server if Antigravity Desktop App is active.
        /// </summary>
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
        /// Fallback method using fetchAvailableModels if retrieveUserQuotaSummary is unreachable.
        /// </summary>
        private async Task FetchCloudAvailableModelsFallbackAsync(AgyMetrics metrics)
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

        private static bool ParseQuotaSummaryJson(JsonElement root, AgyMetrics metrics)
        {
            JsonElement target = root;
            if (root.TryGetProperty("response", out var respEl))
            {
                target = respEl;
            }

            if (!target.TryGetProperty("groups", out var groupsProp))
            {
                return false;
            }

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

                return true;
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
