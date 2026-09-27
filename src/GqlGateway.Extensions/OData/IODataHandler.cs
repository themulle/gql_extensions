namespace GqlGateway.Extensions.OData;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;

public sealed record ODataQueryResult(
    bool Success,
    int StatusCode,
    object Payload,
    string? ErrorCode = null,
    string? ErrorMessage = null
);

public interface IODataHandler
{
    Task<string> GetMetadataCsdlAsync(CancellationToken ct = default);
    Task<object> GetServiceDocumentAsync(string serviceRootUrl, CancellationToken ct = default);
    Task<ODataQueryResult> ExecuteEntitySetQueryAsync(
        ClaimsPrincipal? principal,
        string serviceRootUrl,
        TableIdentifier table,
        int? top,
        int? skip,
        string? select,
        bool includeCount,
        IReadOnlyDictionary<string, string[]>? headers,
        CancellationToken ct = default);
}
