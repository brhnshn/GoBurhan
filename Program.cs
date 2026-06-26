using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GoBurhan.Data;
using GoBurhan.Models;
using GoBurhan.Services;
using GoBurhan.Helpers;
using GoBurhan.DTOs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();

// PostgreSQL DB Context Setup
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Resilient Redis Connection Setup (prevent throwing on startup if Redis is down)
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = builder.Configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379";
    
    // Add abortOnConnectFail=false to ensure start-up isn't blocked by Redis outage
    var options = ConfigurationOptions.Parse(connectionString);
    options.AbortOnConnectFail = false;
    options.ConnectTimeout = 3000; // 3 seconds timeout
    
    return ConnectionMultiplexer.Connect(options);
});

// Register services
builder.Services.AddSingleton<IRedisCacheService, RedisCacheService>();
builder.Services.AddSingleton<IAnalyticsQueue, AnalyticsQueue>();
builder.Services.AddHostedService<AnalyticsBackgroundWorker>();
builder.Services.AddHostedService<TelegramBotBackgroundWorker>();

// Configure Fixed Window Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("AdminPolicy", opt =>
    {
        opt.Window = TimeSpan.FromSeconds(10);
        opt.PermitLimit = 20;
        opt.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("RedirectPolicy", opt =>
    {
        opt.Window = TimeSpan.FromSeconds(10);
        opt.PermitLimit = 100;
        opt.QueueLimit = 0;
    });
});

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// Run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        await dbContext.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "An error occurred while migrating the database.");
    }
}

app.UseCors("AllowAll");
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

// Fallback to serve index.html for SPA router
app.MapFallbackToFile("index.html");

// ----------------------------------------------------
// AUTHENTICATION ENDPOINTS
// ----------------------------------------------------
app.MapGet("/api/auth/status", async (AppDbContext dbContext) =>
{
    bool anyUsers = await dbContext.AdminUsers.AnyAsync();
    return Results.Ok(new AuthStatusDto(RegisterOpen: !anyUsers));
});

app.MapPost("/api/auth/register", async (RegisterRequest request, AppDbContext dbContext, IRedisCacheService cacheService) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { error = "Kullanıcı adı ve şifre boş olamaz." });
    }

    // Check if any user exists
    if (await dbContext.AdminUsers.AnyAsync())
    {
        return Results.BadRequest(new { error = "Kayıtlar kapalıdır. Sistemde zaten bir yönetici mevcut." });
    }

    var (hash, salt) = PasswordHasher.HashPassword(request.Password);
    var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    var admin = new AdminUser
    {
        Id = Guid.NewGuid(),
        Username = request.Username.Trim().ToLowerInvariant(),
        PasswordHash = hash,
        PasswordSalt = salt,
        AuthToken = token
    };

    await dbContext.AdminUsers.AddAsync(admin);
    await dbContext.SaveChangesAsync();

    // Cache the token in Redis
    try
    {
        await cacheService.SetAsync($"auth:token:{token}", admin.Username, TimeSpan.FromDays(7));
    }
    catch {}

    return Results.Ok(new { token = token, username = admin.Username });
});

app.MapPost("/api/auth/login", async (LoginRequest request, AppDbContext dbContext, IRedisCacheService cacheService) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { error = "Kullanıcı adı ve şifre boş olamaz." });
    }

    var username = request.Username.Trim().ToLowerInvariant();
    var admin = await dbContext.AdminUsers.FirstOrDefaultAsync(u => u.Username == username);

    if (admin == null || !PasswordHasher.VerifyPassword(request.Password, admin.PasswordHash, admin.PasswordSalt))
    {
        return Results.Json(new { error = "Hatalı kullanıcı adı veya şifre." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    admin.AuthToken = token;
    await dbContext.SaveChangesAsync();

    // Cache in Redis
    try
    {
        await cacheService.SetAsync($"auth:token:{token}", admin.Username, TimeSpan.FromDays(7));
    }
    catch {}

    return Results.Ok(new { token = token, username = admin.Username });
});

// ----------------------------------------------------
// REDIRECT MOTOR ENDPOINT
// ----------------------------------------------------
app.MapGet("/{shortCode}", async (
    string shortCode,
    HttpContext httpContext,
    IRedisCacheService cacheService,
    IConnectionMultiplexer redisMultiplexer,
    AppDbContext dbContext,
    IAnalyticsQueue analyticsQueue,
    IConfiguration configuration) =>
{
    var cleanCode = shortCode.ToLowerInvariant().Trim();

    if (string.IsNullOrWhiteSpace(cleanCode) || cleanCode == "index.html")
    {
        return Results.Redirect("/index.html");
    }

    Guid linkId;
    string originalUrl;

    // 1. Check Redis Cache using Cache-Aside Pattern
    string cacheKey = $"redirect:{cleanCode}";
    CachedLink? cachedLink = null;

    try
    {
        cachedLink = await cacheService.GetAsync<CachedLink>(cacheKey);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Error reading from Redis cache service for key {Key}", cacheKey);
    }

    if (cachedLink != null)
    {
        // Cache Hit
        linkId = cachedLink.Id;
        originalUrl = cachedLink.OriginalUrl;

        // Safely increment hits metric in Redis in the background
        if (redisMultiplexer.IsConnected)
        {
            try
            {
                _ = redisMultiplexer.GetDatabase().StringIncrementAsync("goburhan:metrics:hits");
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to increment Redis hit counter.");
            }
        }
    }
    else
    {
        // Cache Miss: Query PostgreSQL
        if (redisMultiplexer.IsConnected)
        {
            try
            {
                _ = redisMultiplexer.GetDatabase().StringIncrementAsync("goburhan:metrics:misses");
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to increment Redis miss counter.");
            }
        }
        
        var link = await dbContext.ShortLinks
            .FirstOrDefaultAsync(l => l.ShortCode == cleanCode && l.IsActive);

        if (link == null)
        {
            return Results.NotFound(new { error = "Kısaltılmış link bulunamadı veya pasif durumda." });
        }

        linkId = link.Id;
        originalUrl = link.OriginalUrl;

        // Cache the entire CachedLink structure (Id and URL)
        try
        {
            await cacheService.SetAsync(cacheKey, new CachedLink(linkId, originalUrl), TimeSpan.FromHours(24));
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Failed to populate Redis cache for key {Key}", cacheKey);
        }
    }

    // 2. Asynchronous Click Analytics logging (unless client IP is in FilteredIPs)
    var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var filteredIps = configuration.GetSection("FilteredIPs").Get<List<string>>() ?? new List<string>();

    if (!filteredIps.Contains(clientIp))
    {
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        var (browser, os) = UserAgentParser.Parse(userAgent);

        var analytics = new ClickAnalytics
        {
            Id = Guid.NewGuid(),
            ShortLinkId = linkId,
            ClickedAt = DateTime.UtcNow,
            IpAddress = clientIp,
            UserAgent = userAgent,
            Browser = browser,
            OperatingSystem = os
        };

        // Queue in background worker queue
        await analyticsQueue.QueueClickAsync(analytics);
    }

    // 3. HTTP 302 Found redirect
    return Results.Redirect(originalUrl, permanent: false, preserveMethod: false);
})
.RequireRateLimiting("RedirectPolicy");

// ----------------------------------------------------
// ADMIN CONTROLS ENDPOINTS (Protected by X-Admin-Token)
// ----------------------------------------------------
var adminGroup = app.MapGroup("/api/admin")
                    .RequireRateLimiting("AdminPolicy")
                    .AddEndpointFilter(async (context, next) =>
                    {
                        if (!context.HttpContext.Request.Headers.TryGetValue("X-Admin-Token", out var extractedToken) ||
                            string.IsNullOrWhiteSpace(extractedToken))
                        {
                            return Results.Json(new { error = "Kimlik doğrulama başarısız: X-Admin-Token başlığı bulunamadı veya boş." }, statusCode: StatusCodes.Status401Unauthorized);
                        }

                        var tokenStr = extractedToken.ToString();
                        var cacheService = context.HttpContext.RequestServices.GetRequiredService<IRedisCacheService>();
                        var dbContext = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

                        // 1. Check Redis Cache first (extremely fast)
                        string cacheKey = $"auth:token:{tokenStr}";
                        string? cachedUsername = null;
                        try
                        {
                            cachedUsername = await cacheService.GetAsync<string>(cacheKey);
                        }
                        catch {}

                        if (cachedUsername != null)
                        {
                            return await next(context);
                        }

                        // 2. Fallback to PostgreSQL DB
                        var adminUser = await dbContext.AdminUsers.FirstOrDefaultAsync(u => u.AuthToken == tokenStr);
                        if (adminUser != null)
                        {
                            // Populate token back to Redis cache
                            try
                            {
                                await cacheService.SetAsync(cacheKey, adminUser.Username, TimeSpan.FromDays(7));
                            }
                            catch {}

                            return await next(context);
                        }

                        return Results.Json(new { error = "Kimlik doğrulama başarısız: Geçersiz veya süresi dolmuş token." }, statusCode: StatusCodes.Status401Unauthorized);
                    });

// Create ShortLink
adminGroup.MapPost("/links", async (CreateShortLinkRequest request, AppDbContext dbContext, IRedisCacheService cacheService) =>
{
    if (string.IsNullOrWhiteSpace(request.OriginalUrl))
    {
        return Results.BadRequest(new { error = "Hedef URL alanı boş olamaz." });
    }

    // Generate random shortcode if not supplied
    var shortCode = string.IsNullOrWhiteSpace(request.ShortCode) 
        ? GenerateRandomCode() 
        : request.ShortCode.ToLowerInvariant().Trim();

    // Check if unique ShortCode already exists
    if (await dbContext.ShortLinks.AnyAsync(l => l.ShortCode == shortCode))
    {
        return Results.Conflict(new { error = $"'{shortCode}' kısa kodu zaten kullanımda." });
    }

    var shortLink = new ShortLink
    {
        Id = Guid.NewGuid(),
        ShortCode = shortCode,
        OriginalUrl = request.OriginalUrl.Trim(),
        CreatedAt = DateTime.UtcNow,
        IsActive = true
    };

    await dbContext.ShortLinks.AddAsync(shortLink);
    await dbContext.SaveChangesAsync();

    // Seed Redis cache immediately (Pre-caching)
    string cacheKey = $"redirect:{shortCode}";
    try
    {
        await cacheService.SetAsync(cacheKey, new CachedLink(shortLink.Id, shortLink.OriginalUrl), TimeSpan.FromHours(24));
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to pre-cache shortlink '{ShortCode}'", shortCode);
    }

    return Results.Created($"/api/admin/links/{shortLink.Id}", new ShortLinkDto(
        shortLink.Id,
        shortLink.ShortCode,
        shortLink.OriginalUrl,
        shortLink.CreatedAt,
        shortLink.IsActive,
        0
    ));
});

// List Links
adminGroup.MapGet("/links", async (AppDbContext dbContext) =>
{
    var tempLinks = await dbContext.ShortLinks
        .OrderByDescending(l => l.CreatedAt)
        .Select(l => new
        {
            l.Id,
            l.ShortCode,
            l.OriginalUrl,
            l.CreatedAt,
            l.IsActive,
            ClickCount = l.ClickAnalytics.Count
        })
        .ToListAsync();

    var links = tempLinks.Select(l => new ShortLinkDto(
        l.Id,
        l.ShortCode,
        l.OriginalUrl,
        l.CreatedAt,
        l.IsActive,
        l.ClickCount
    )).ToList();

    return Results.Ok(links);
});

// Delete Link (and invalidate cache)
adminGroup.MapDelete("/links/{id:guid}", async (Guid id, AppDbContext dbContext, IRedisCacheService cacheService) =>
{
    var link = await dbContext.ShortLinks.FindAsync(id);
    if (link == null)
    {
        return Results.NotFound(new { error = "Silinmek istenen link bulunamadı." });
    }

    dbContext.ShortLinks.Remove(link);
    await dbContext.SaveChangesAsync();

    // Invalidate Redis Cache
    string cacheKey = $"redirect:{link.ShortCode}";
    try
    {
        await cacheService.RemoveAsync(cacheKey);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to invalidate Redis cache for deleted shortlink '{ShortCode}'", link.ShortCode);
    }

    return Results.Ok(new { message = "Link başarıyla silindi ve cache temizlendi." });
});

// Get System Metrics & Analytics Trend
adminGroup.MapGet("/metrics", async (AppDbContext dbContext, IConnectionMultiplexer redisMultiplexer) =>
{
    var totalClicks = await dbContext.ClickAnalytics.CountAsync();
    var activeLinksCount = await dbContext.ShortLinks.CountAsync(l => l.IsActive);

    // Calculate Redis Cache hit rate
    double hitRate = 100.0;
    
    if (redisMultiplexer.IsConnected)
    {
        try
        {
            var redisDb = redisMultiplexer.GetDatabase();
            var hitsVal = await redisDb.StringGetAsync("goburhan:metrics:hits");
            var missesVal = await redisDb.StringGetAsync("goburhan:metrics:misses");

            double hits = hitsVal.HasValue ? (double)hitsVal : 0;
            double misses = missesVal.HasValue ? (double)missesVal : 0;
            double total = hits + misses;
            hitRate = total > 0 ? (hits / total) * 100 : 100.0;
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Failed to read Redis hit/miss counters for metrics.");
        }
    }

    // Get Click Trend for the last 7 days
    var startDate = DateTime.UtcNow.Date.AddDays(-6);
    var analyticsData = await dbContext.ClickAnalytics
        .Where(c => c.ClickedAt >= startDate)
        .GroupBy(c => c.ClickedAt.Date)
        .Select(g => new { Date = g.Key, Count = g.Count() })
        .ToListAsync();

    var trend = new List<AnalyticsTrendDto>();
    for (int i = 6; i >= 0; i--)
    {
        var date = DateTime.UtcNow.Date.AddDays(-i);
        var label = date.ToString("dd MMM"); // e.g. "02 Haz"
        var match = analyticsData.FirstOrDefault(a => a.Date == date);
        trend.Add(new AnalyticsTrendDto(label, match?.Count ?? 0));
    }

    return Results.Ok(new SystemMetricsDto(
        totalClicks,
        activeLinksCount,
        Math.Round(hitRate, 1),
        trend
    ));
});

app.Run();

// Helper to generate a random short code
static string GenerateRandomCode()
{
    const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
    var random = new Random();
    var code = new char[6];
    for (int i = 0; i < 6; i++)
    {
        code[i] = chars[random.Next(chars.Length)];
    }
    return new string(code);
}
