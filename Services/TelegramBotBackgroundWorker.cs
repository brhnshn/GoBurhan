using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using GoBurhan.Data;
using GoBurhan.Models;
using GoBurhan.DTOs;

namespace GoBurhan.Services
{
    public class TelegramBotBackgroundWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TelegramBotBackgroundWorker> _logger;

        public TelegramBotBackgroundWorker(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<TelegramBotBackgroundWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var botToken = _configuration["Telegram:BotToken"];
            if (string.IsNullOrWhiteSpace(botToken) || botToken.Contains("YOUR_TELEGRAM_BOT_TOKEN"))
            {
                _logger.LogWarning("Telegram bot token is not configured. Telegram bot service will not start.");
                return;
            }

            _logger.LogInformation("Telegram Bot service is starting...");
            var botClient = new TelegramBotClient(botToken);

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message }
            };

            botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandleErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: stoppingToken
            );

            // Keep the service alive
            var tcs = new TaskCompletionSource();
            using (stoppingToken.Register(() => tcs.SetResult()))
            {
                await tcs.Task;
            }
            
            _logger.LogInformation("Telegram Bot service is stopping...");
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message) return;
            if (message.Text is not { } messageText) return;

            var chatId = message.Chat.Id;
            var userId = message.From?.Id;
            var username = message.From?.Username;

            // Validate user authorization
            if (!IsAuthorized(userId, username))
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "Bu bot kişiseldir. Erişim yetkiniz bulunmamaktadır.",
                    cancellationToken: cancellationToken
                );
                return;
            }

            // Handle help commands
            if (messageText.StartsWith("/start") || messageText.StartsWith("/help"))
            {
                var helpText = "👋 Merhaba!\n\n" +
                               "Bu bot üzerinden hızlıca link kısaltabilirsin.\n\n" +
                               "**Kullanım Yöntemleri:**\n" +
                               "1. Doğrudan uzun URL'yi mesaj olarak gönder (Rastgele kodla kısaltılır).\n" +
                               "2. `/shorten <uzun_url> <ozel_kod>` komutunu kullanarak özel kodla kısalt.\n\n" +
                               "Örnek: `/shorten https://burhansahin.com.tr blog`";
                
                await botClient.SendMessage(
                    chatId: chatId,
                    text: helpText,
                    cancellationToken: cancellationToken
                );
                return;
            }

            // Handle `/shorten` command
            if (messageText.StartsWith("/shorten"))
            {
                var parts = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await botClient.SendMessage(chatId, "Hatalı kullanım. Örnek: `/shorten <uzun_url> [ozel_kod]`", cancellationToken: cancellationToken);
                    return;
                }

                var targetUrl = parts[1];
                var customCode = parts.Length >= 3 ? parts[2] : null;

                await ShortenAndReplyAsync(botClient, chatId, targetUrl, customCode, cancellationToken);
                return;
            }

            // Handle raw URL sent directly
            var urlPattern = @"^(https?:\/\/)?([\da-z\.-]+)\.([a-z\.]{2,6})([\/\w \.-]*)*\/?$";
            if (Regex.IsMatch(messageText.Trim(), urlPattern, RegexOptions.IgnoreCase))
            {
                await ShortenAndReplyAsync(botClient, chatId, messageText.Trim(), null, cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, "Gönderdiğiniz mesaj geçerli bir URL veya komut değil. Yardım için `/help` yazabilirsiniz.", cancellationToken: cancellationToken);
        }

        private bool IsAuthorized(long? userId, string? username)
        {
            var authUserIdStr = _configuration["Telegram:AuthorizedUserId"];
            var authUsername = _configuration["Telegram:AuthorizedUsername"];

            if (userId != null && long.TryParse(authUserIdStr, out var authUserId) && userId == authUserId)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(authUsername) &&
                username.Equals(authUsername, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private async Task ShortenAndReplyAsync(
            ITelegramBotClient botClient,
            long chatId,
            string targetUrl,
            string? customCode,
            CancellationToken cancellationToken)
        {
            // Normalize URL
            if (!targetUrl.StartsWith("http://") && !targetUrl.StartsWith("https://"))
            {
                targetUrl = "https://" + targetUrl;
            }

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cacheService = scope.ServiceProvider.GetRequiredService<IRedisCacheService>();

            var shortCode = string.IsNullOrWhiteSpace(customCode)
                ? GenerateRandomCode()
                : customCode.ToLowerInvariant().Trim();

            // Check if code is already in use
            var codeExists = await dbContext.ShortLinks.AnyAsync(l => l.ShortCode == shortCode, cancellationToken);
            if (codeExists)
            {
                await botClient.SendMessage(chatId, $"❌ Hata: '{shortCode}' kısa kodu zaten kullanımda. Lütfen başka bir kod seçin.", cancellationToken: cancellationToken);
                return;
            }

            var shortLink = new ShortLink
            {
                Id = Guid.NewGuid(),
                ShortCode = shortCode,
                OriginalUrl = targetUrl,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            await dbContext.ShortLinks.AddAsync(shortLink, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Cache in Redis
            string cacheKey = $"redirect:{shortCode}";
            try
            {
                await cacheService.SetAsync(cacheKey, new CachedLink(shortLink.Id, shortLink.OriginalUrl), TimeSpan.FromHours(24));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache shortlink in Telegram service.");
            }

            var baseDomain = _configuration["Telegram:BaseDomain"] ?? "go.burhansahin.com.tr";
            var shortUrl = $"https://{baseDomain}/{shortCode}";

            await botClient.SendMessage(chatId, $"🚀 **Link Başarıyla Kısaltıldı!**\n\n🔗 {shortUrl}", cancellationToken: cancellationToken);
        }

        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            _logger.LogError(exception, "Telegram Bot polling error occurred.");
            return Task.CompletedTask;
        }

        private string GenerateRandomCode()
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
    }
}
