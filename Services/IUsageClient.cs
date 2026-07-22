using UsageAI.Models;

namespace UsageAI.Services;

internal interface IUsageClient
{
    string Id { get; }

    string DisplayName { get; }

    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}
