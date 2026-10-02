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

public sealed class CollibraCatalogClient : IDataCatalogClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<CollibraCatalogClient> _logger;
    private const long MaxAllowedResponseBytes = 10 * 1024 * 1024; // 10 MB maximum payload cap

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.Collibra;

    public CollibraCatalogClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<CollibraCatalogClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        var collibraOpts = options.Value.Catalog.Collibra;
        if (!string.IsNullOrWhiteSpace(collibraOpts.BaseUrl) && _httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(collibraOpts.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
    {
        var collibraOpts = _options.Value.Catalog.Collibra;
        if (string.IsNullOrWhiteSpace(collibraOpts.BaseUrl) || _httpClient.BaseAddress == null)
        {
            _logger.LogInformation("Collibra base URL unconfigured; returning empty catalog assets.");
            return Array.Empty<CatalogTableAsset>();
        }

        try
        {
            // Collibra REST Core API v2: /rest/2.0/assets?typeNames=Table
            var response = await _httpClient.GetAsync("rest/2.0/assets?typeNames=Table&limit=250", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Collibra API returned status {StatusCode}", response.StatusCode);
                return Array.Empty<CatalogTableAsset>();
            }

            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > MaxAllowedResponseBytes)
            {
                throw new InvalidOperationException($"Collibra response size ({response.Content.Headers.ContentLength.Value} bytes) exceeds maximum allowed limit of {MaxAllowedResponseBytes} bytes.");
            }

            var jsonDoc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            if (jsonDoc == null || !jsonDoc.RootElement.TryGetProperty("results", out var resultsElement))
            {
                return Array.Empty<CatalogTableAsset>();
            }

            var tables = new List<CatalogTableAsset>();
            foreach (var asset in resultsElement.EnumerateArray())
            {
                var id = asset.GetProperty("id").GetString() ?? Guid.NewGuid().ToString();
                var name = asset.GetProperty("name").GetString() ?? "unknown";

                // In Collibra, domains and communities are part of the asset hierarchy
                var domainName = asset.TryGetProperty("domain", out var dObj) && dObj.TryGetProperty("name", out var dn)
                    ? dn.GetString() ?? "default"
                    : "default";

                var classifications = new List<string>();
                if (asset.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tagsElem.EnumerateArray())
                    {
                        var tName = tag.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(tName)) classifications.Add(tName);
                    }
                }

                var tableId = new TableIdentifier(domainName, "dbo", name);

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
            _logger.LogError(ex, "Failed to query Collibra catalog tables.");
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
