using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace GoBurhan.Services
{
    public class RedisCacheService : IRedisCacheService
    {
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly ILogger<RedisCacheService> _logger;

        public RedisCacheService(IConnectionMultiplexer connectionMultiplexer, ILogger<RedisCacheService> logger)
        {
            _connectionMultiplexer = connectionMultiplexer;
            _logger = logger;
        }

        private IDatabase? GetDatabase()
        {
            try
            {
                if (!_connectionMultiplexer.IsConnected)
                {
                    _logger.LogWarning("Redis connection is currently unavailable.");
                    return null;
                }
                return _connectionMultiplexer.GetDatabase();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Redis database.");
                return null;
            }
        }

        public async Task<T?> GetAsync<T>(string key)
        {
            try
            {
                var db = GetDatabase();
                if (db == null) return default;

                var value = await db.StringGetAsync(key);
                if (value.IsNullOrEmpty)
                {
                    return default;
                }

                return JsonSerializer.Deserialize<T>(value!);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving key '{Key}' from Redis.", key);
                return default;
            }
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
        {
            try
            {
                var db = GetDatabase();
                if (db == null) return;

                var json = JsonSerializer.Serialize(value);
                Expiration redisExpiry = expiry.HasValue ? expiry.Value : default;
                await db.StringSetAsync(key, json, redisExpiry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing key '{Key}' to Redis.", key);
            }
        }

        public async Task RemoveAsync(string key)
        {
            try
            {
                var db = GetDatabase();
                if (db == null) return;

                await db.KeyDeleteAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting key '{Key}' from Redis.", key);
            }
        }
    }
}
