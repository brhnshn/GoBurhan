using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GoBurhan.Models;

namespace GoBurhan.Services
{
    public class AnalyticsQueue : IAnalyticsQueue
    {
        private readonly Channel<ClickAnalytics> _channel;

        public AnalyticsQueue()
        {
            var options = new BoundedChannelOptions(50000)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            };
            _channel = Channel.CreateBounded<ClickAnalytics>(options);
        }

        public async ValueTask QueueClickAsync(ClickAnalytics click)
        {
            await _channel.Writer.WriteAsync(click);
        }

        public IAsyncEnumerable<ClickAnalytics> DequeueAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
