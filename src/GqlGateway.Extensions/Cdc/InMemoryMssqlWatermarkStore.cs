namespace GqlGateway.Extensions.Cdc;

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Streaming.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;

/// <summary>
/// In-memory thread-safe implementation of IMssqlWatermarkStore.
/// </summary>
public sealed class InMemoryMssqlWatermarkStore : IMssqlWatermarkStore
{
    private readonly ConcurrentDictionary<TableIdentifier, long> _watermarks = new();

    public ValueTask<long> GetWatermarkAsync(TableIdentifier table, CancellationToken ct = default)
    {
        _watermarks.TryGetValue(table, out var version);
        return ValueTask.FromResult(version);
    }

    public ValueTask SetWatermarkAsync(TableIdentifier table, long watermark, CancellationToken ct = default)
    {
        _watermarks.AddOrUpdate(table, watermark, (_, existing) => System.Math.Max(existing, watermark));
        return ValueTask.CompletedTask;
    }
}
