using System;

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
        public string Description { get; set; } = "";
        public int RemainingPercent { get; set; } = 100;
        public string RefreshTimeText { get; set; } = "";
        public DateTime ResetTime { get; set; } = DateTime.MinValue;
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

        public DateTime LastActivityTime { get; set; } = DateTime.MinValue;
    }
}
