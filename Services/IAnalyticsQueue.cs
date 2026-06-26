using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GoBurhan.Models;

namespace GoBurhan.Services
{
    public interface IAnalyticsQueue
    {
        ValueTask QueueClickAsync(ClickAnalytics click);
        IAsyncEnumerable<ClickAnalytics> DequeueAllAsync(CancellationToken cancellationToken);
    }
}
