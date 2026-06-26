using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using GoBurhan.Data;
using GoBurhan.Models;

namespace GoBurhan.Services
{
    public class AnalyticsBackgroundWorker : BackgroundService
    {
        private readonly IAnalyticsQueue _queue;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AnalyticsBackgroundWorker> _logger;
        private readonly List<ClickAnalytics> _batch = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public AnalyticsBackgroundWorker(
            IAnalyticsQueue queue,
            IServiceProvider serviceProvider,
            ILogger<AnalyticsBackgroundWorker> logger)
        {
            _queue = queue;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Analytics background worker started.");

            // Start a background task that flushes the batch periodically (every 1 second)
            var flushTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                        await _semaphore.WaitAsync(stoppingToken);
                        try
                        {
                            if (_batch.Count > 0)
                            {
                                await FlushBatchAsync(_batch);
                            }
                        }
                        finally
                        {
                            _semaphore.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal shutdown
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error occurred during periodic analytics flush.");
                    }
                }
            }, stoppingToken);

            try
            {
                await foreach (var click in _queue.DequeueAllAsync(stoppingToken))
                {
                    await _semaphore.WaitAsync(stoppingToken);
                    try
                    {
                        _batch.Add(click);
                        if (_batch.Count >= 100)
                        {
                            await FlushBatchAsync(_batch);
                        }
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading from analytics queue.");
            }

            // Wait for the background timer task to finish
            try
            {
                await flushTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while waiting for periodic flush task to complete on shutdown.");
            }

            // Final flush of remaining items
            await _semaphore.WaitAsync();
            try
            {
                if (_batch.Count > 0)
                {
                    await FlushBatchAsync(_batch);
                }
            }
            finally
            {
                _semaphore.Release();
            }

            _logger.LogInformation("Analytics background worker stopped.");
        }

        private async Task FlushBatchAsync(List<ClickAnalytics> batch)
        {
            if (batch.Count == 0) return;

            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                try
                {
                    await dbContext.ClickAnalytics.AddRangeAsync(batch);
                    await dbContext.SaveChangesAsync();
                    _logger.LogInformation("Flushed {Count} click analytics records to PostgreSQL database.", batch.Count);
                    batch.Clear();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write batch of {Count} analytics records to database.", batch.Count);
                    // Clear the batch to avoid memory growth.
                    batch.Clear();
                }
            }
        }
    }
}
