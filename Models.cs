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
