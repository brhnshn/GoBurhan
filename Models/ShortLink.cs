using System;
using System.Collections.Generic;

namespace GoBurhan.Models
{
    public class ShortLink
    {
        public Guid Id { get; set; }
        public string ShortCode { get; set; } = string.Empty;
        public string OriginalUrl { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; } = true;

        // Navigation property
        public ICollection<ClickAnalytics> ClickAnalytics { get; set; } = new List<ClickAnalytics>();
    }
}
