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
        public string Description { get; set; } = "";
        public int RemainingPercent { get; set; } = 100;
        public string RefreshTimeText { get; set; } = "";
        public DateTime ResetTime { get; set; } = DateTime.MinValue;
    }

    public class ConvSummaryItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Preview { get; set; } = "";
        public int StepCount { get; set; }
        public DateTime LastModified { get; set; }
    }

    public class PromptHistoryItem
    {
        public string Text { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string ConversationId { get; set; } = "";
    }

    public class AgyMetrics
    {
        public AgyStatus Status { get; set; } = AgyStatus.Offline;
        public int ProcessId { get; set; }
        public double MemoryMb { get; set; }
        public double CpuPercent { get; set; }

        public string UserPlan { get; set; } = "Google AI Pro";

        // Main Quotas (Gemini)
        public ModelQuotaItem FiveHourLimit { get; set; } = new()
        {
            Title = "Five Hour Limit Remaining",
            RemainingPercent = 92,
            RefreshTimeText = "it will fully refresh in 4 hours, 40 minutes."
        };

        public ModelQuotaItem WeeklyLimit { get; set; } = new()
        {
            Title = "Weekly Limit Remaining",
            RemainingPercent = 85,
            RefreshTimeText = "it will fully refresh in 2 days, 12 hours."
        };

        // Advanced metrics
        public int TotalConversations { get; set; }
        public int TotalSteps { get; set; }

        public int TodayPrompts { get; set; }
        public int TotalPrompts { get; set; }

        public int TodayApiCalls { get; set; }
        public int TotalApiCalls { get; set; }

        public string ActiveConvId { get; set; } = "";
        public string ActiveConvTitle { get; set; } = "대화 없음";
        public int ActiveConvSteps { get; set; }
        public string ActiveModel { get; set; } = "gemini-3.8-flash";

        public DateTime LastActivityTime { get; set; } = DateTime.MinValue;

        public List<ConvSummaryItem> RecentConversations { get; set; } = new();
        public List<PromptHistoryItem> RecentPrompts { get; set; } = new();
    }
}
