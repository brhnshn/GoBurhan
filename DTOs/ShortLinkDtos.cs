using System;
using System.Collections.Generic;

namespace GoBurhan.DTOs
{
    public record CreateShortLinkRequest(string OriginalUrl, string? ShortCode);

    public record ShortLinkDto(
        Guid Id,
        string ShortCode,
        string OriginalUrl,
        DateTime CreatedAt,
        bool IsActive,
        int ClickCount
    );

    public record SystemMetricsDto(
        int TotalClicks,
        int ActiveLinksCount,
        double RedisHitRatePercent,
        List<AnalyticsTrendDto> ClicksTrend
    );

    public record AnalyticsTrendDto(
        string DateLabel,
        int ClickCount
    );

    public record CachedLink(
        Guid Id,
        string OriginalUrl
    );

    public record RegisterRequest(string Username, string Password);
    public record LoginRequest(string Username, string Password);
    public record AuthStatusDto(bool RegisterOpen);
}
