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

public sealed class MicrosoftPurviewCatalogClient : IDataCatalogClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<MicrosoftPurviewCatalogClient> _logger;

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.MicrosoftPurview;

    public MicrosoftPurviewCatalogClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<MicrosoftPurviewCatalogClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        var purviewOpts = options.Value.Catalog.Purview;
        if (!string.IsNullOrWhiteSpace(purviewOpts.Endpoint) && _httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(purviewOpts.Endpoint.TrimEnd('/') + "/");
        }
    }

    public async Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
    {
        var purviewOpts = _options.Value.Catalog.Purview;
        if (string.IsNullOrWhiteSpace(purviewOpts.Endpoint) || _httpClient.BaseAddress == null)
        {
            _logger.LogInformation("Purview endpoint unconfigured; returning empty or simulated catalog assets.");
            return Array.Empty<CatalogTableAsset>();
        }

        try
        {
            // Apache Atlas Search API: /catalog/api/atlas/v2/search/basic?typeName=rdbms_table
            var typeName = string.IsNullOrWhiteSpace(filter) ? "rdbms_table" : filter;
            var response = await _httpClient.GetAsync($"catalog/api/atlas/v2/search/basic?typeName={typeName}", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Purview search API returned {Status}", response.StatusCode);
                return Array.Empty<CatalogTableAsset>();
            }

            var jsonDoc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            if (jsonDoc == null || !jsonDoc.RootElement.TryGetProperty("entities", out var entitiesElement))
            {
                return Array.Empty<CatalogTableAsset>();
            }

            var tables = new List<CatalogTableAsset>();
            foreach (var entity in entitiesElement.EnumerateArray())
            {
                var guid = entity.GetProperty("guid").GetString() ?? Guid.NewGuid().ToString();
                var qualifiedName = entity.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("qualifiedName", out var qn)
                    ? qn.GetString() ?? string.Empty
                    : string.Empty;

                var name = attrs.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown";

                var classifications = new List<string>();
                if (entity.TryGetProperty("classifications", out var classElement) && classElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in classElement.EnumerateArray())
                    {
                        if (c.TryGetProperty("typeName", out var cName))
                        {
                            var val = cName.GetString();
                            if (!string.IsNullOrWhiteSpace(val)) classifications.Add(val);
                        }
                    }
                }

                // Parse domain/schema from qualifiedName (e.g. mssql://server/database/schema/table)
                var tableId = ParsePurviewQualifiedName(qualifiedName, name);

                tables.Add(new CatalogTableAsset
                {
                    Identifier = tableId,
                    DisplayName = name,
                    ExternalAssetId = guid,
                    Classifications = classifications,
                    Tags = classifications,
                    Columns = []
                });
            }

            return tables;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query Purview catalog tables.");
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

    private static TableIdentifier ParsePurviewQualifiedName(string qualifiedName, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return new TableIdentifier("default", "dbo", defaultName);
        }

        var parts = qualifiedName.Split(['/', '.', ':'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            var domain = parts[^3];
            var schema = parts[^2];
            var tableName = parts[^1];
            return new TableIdentifier(domain, schema, tableName);
        }

        return new TableIdentifier("default", "dbo", defaultName);
    }
}
