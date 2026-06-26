using System;

namespace GoBurhan.Models
{
    public class ClickAnalytics
    {
        public Guid Id { get; set; }
        public Guid ShortLinkId { get; set; }
        public DateTime ClickedAt { get; set; }
        public string IpAddress { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public string Browser { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;

        // Navigation property
        public ShortLink? ShortLink { get; set; }
    }
}
