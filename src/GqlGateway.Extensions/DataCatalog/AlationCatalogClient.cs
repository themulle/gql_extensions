namespace GqlGateway.Extensions.DataCatalog;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.DataCatalog.Models;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class AlationCatalogClient : IDataCatalogClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AlationCatalogClient> _logger;
    private const long MaxAllowedResponseBytes = 10 * 1024 * 1024; // 10 MB maximum payload cap

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.Alation;

    public AlationCatalogClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<AlationCatalogClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        var alationOpts = options.Value.Catalog.Alation;
        if (!string.IsNullOrWhiteSpace(alationOpts.BaseUrl) && _httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(alationOpts.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
    {
        var alationOpts = _options.Value.Catalog.Alation;
        if (string.IsNullOrWhiteSpace(alationOpts.BaseUrl) || _httpClient.BaseAddress == null)
        {
            _logger.LogInformation("Alation base URL unconfigured; returning empty catalog assets.");
            return Array.Empty<CatalogTableAsset>();
        }

        try
        {
            // Alation Integration API v2: /integration/v2/table/
            var response = await _httpClient.GetAsync("integration/v2/table/?limit=250", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Alation API returned status {StatusCode}", response.StatusCode);
                return Array.Empty<CatalogTableAsset>();
            }

            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > MaxAllowedResponseBytes)
            {
                throw new InvalidOperationException($"Alation response size ({response.Content.Headers.ContentLength.Value} bytes) exceeds maximum allowed limit of {MaxAllowedResponseBytes} bytes.");
            }

            var jsonDoc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            if (jsonDoc == null)
            {
                return Array.Empty<CatalogTableAsset>();
            }

            var root = jsonDoc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() :
                        root.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array ? res.EnumerateArray() :
                        default;

            var tables = new List<CatalogTableAsset>();
            foreach (var item in items)
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.ToString() : Guid.NewGuid().ToString();
                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "unknown" : "unknown";
                var schema = item.TryGetProperty("schema_name", out var sProp) ? sProp.GetString() ?? "dbo" : "dbo";
                var dsId = item.TryGetProperty("ds_id", out var dsProp) ? $"ds_{dsProp}" : "default";

                var classifications = new List<string>();
                if (item.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tagsElem.EnumerateArray())
                    {
                        var tName = tag.GetString();
                        if (!string.IsNullOrWhiteSpace(tName)) classifications.Add(tName);
                    }
                }

                var tableId = new TableIdentifier(dsId, schema, name);

                tables.Add(new CatalogTableAsset
                {
                    Identifier = tableId,
                    DisplayName = name,
                    ExternalAssetId = id,
                    Classifications = classifications,
                    Tags = classifications,
                    Columns = []
                });
            }

            return tables;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query Alation catalog tables.");
            return Array.Empty<CatalogTableAsset>();
        }
    }

    public async Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var tables = await GetTablesAsync(ct: ct).ConfigureAwait(false);
        foreach (var t in tables)
        {
            if (t.Identifier.Equals(table)) return t;
        }
        return null;
    }
}
