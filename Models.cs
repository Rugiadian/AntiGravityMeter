using System;
using System.Collections.Generic;

namespace AntiGravityMeter
{
    public enum AgyStatus
    {
        Offline,
        Idle,
        Busy
    }

    public class ModelQuotaItem
    {
        public string Title { get; set; } = "";
        /// <summary>"weekly", "5h", or empty when unknown.</summary>
        public string Window { get; set; } = "";
        public string Description { get; set; } = "";
        public int RemainingPercent { get; set; } = 100;
        public string RefreshTimeText { get; set; } = "";
        public DateTime ResetTime { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// A quota pool shared by several models (e.g. "Claude and GPT models"),
    /// usually containing a weekly bucket and a 5-hour bucket.
    /// </summary>
    public class QuotaGroup
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>True if built from fetchAvailableModels instead of the official quota summary.</summary>
        public bool IsSynthesized { get; set; }
        public List<ModelQuotaItem> Buckets { get; set; } = new();
    }

    /// <summary>
    /// Per-model remaining quota (from fetchAvailableModels). Effort variants
    /// such as "(Low)/(Medium)/(High)" are merged into a single entry.
    /// </summary>
    public class ModelUsage
    {
        public string DisplayName { get; set; } = "";
        public string Provider { get; set; } = "";
        public List<string> ModelIds { get; set; } = new();
        public int RemainingPercent { get; set; } = 100;
        public DateTime ResetTime { get; set; } = DateTime.MinValue;
        public int SortOrder { get; set; } = int.MaxValue;
    }

    public class AgyMetrics
    {
        public AgyStatus Status { get; set; } = AgyStatus.Offline;
        public int ProcessId { get; set; }
        public double MemoryMb { get; set; }
        public double CpuPercent { get; set; }

        public string UserPlan { get; set; } = "Google AI Pro";

        // Gemini Weekly & 5-Hour Limits
        public ModelQuotaItem WeeklyLimit { get; set; } = new()
        {
            Title = "Weekly Limit Remaining",
            RemainingPercent = 100,
            RefreshTimeText = ""
        };

        public ModelQuotaItem FiveHourLimit { get; set; } = new()
        {
            Title = "Five Hour Limit Remaining",
            RemainingPercent = 100,
            RefreshTimeText = ""
        };

        // All quota groups (Gemini, Claude & GPT, ...)
        public List<QuotaGroup> Groups { get; set; } = new();

        // Per-model usage for every available model
        public List<ModelUsage> Models { get; set; } = new();

        public DateTime LastActivityTime { get; set; } = DateTime.MinValue;
    }
}
