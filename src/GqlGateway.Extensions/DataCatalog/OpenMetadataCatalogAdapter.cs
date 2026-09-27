namespace GqlGateway.Extensions.DataCatalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.DataCatalog.Models;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Application.OpenMetadata.Models;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class OpenMetadataCatalogAdapter : IDataCatalogClient
{
    private readonly IOpenMetadataClient _client;
    private readonly ILogger<OpenMetadataCatalogAdapter> _logger;

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.OpenMetadata;

    public OpenMetadataCatalogAdapter(
        IOpenMetadataClient client,
        ILogger<OpenMetadataCatalogAdapter> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
    {
        var omTables = await _client.GetTablesAsync(filter, ct).ConfigureAwait(false);
        var list = new List<CatalogTableAsset>(omTables.Count);

        foreach (var omTable in omTables)
        {
            list.Add(MapToCatalogAsset(omTable));
        }

        return list;
    }

    public async Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var fqn = $"{table.Domain}.{table.Schema}.{table.TableName}";
        try
        {
            var omTable = await _client.GetTableByFqnAsync(fqn, ct).ConfigureAwait(false);
            return omTable != null ? MapToCatalogAsset(omTable) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get table {Table} from OpenMetadata", table);
            return null;
        }
    }

    private static CatalogTableAsset MapToCatalogAsset(OpenMetadataTable omTable)
    {
        var domain = omTable.Service?.Name ?? omTable.Database?.Name ?? "default";
        var schema = omTable.DatabaseSchema?.Name ?? "dbo";
        var tableName = omTable.Name;

        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema))
        {
            var parts = omTable.FullyQualifiedName.Split('.');
            if (parts.Length >= 4)
            {
                domain = parts[0];
                schema = parts[2];
                tableName = parts[3];
            }
            else if (parts.Length == 3)
            {
                domain = parts[0];
                schema = parts[1];
                tableName = parts[2];
            }
        }

        var tableId = new TableIdentifier(domain, schema, tableName);

        var columns = omTable.Columns.Select(c => new CatalogColumnAsset
        {
            ColumnName = c.Name,
            DataType = c.DataType,
            Description = c.Description,
            Tags = c.Tags.Select(t => t.TagFQN).ToList(),
            Classifications = c.Tags.Select(t => t.TagFQN).ToList(),
            IsNullable = true
        }).ToList();

        var owner = omTable.Owners.FirstOrDefault()?.Name;

        return new CatalogTableAsset
        {
            Identifier = tableId,
            DisplayName = omTable.DisplayName ?? omTable.Name,
            Description = omTable.Description,
            SourceType = omTable.ServiceType ?? "PostgreSQL",
            ExternalAssetId = omTable.Id.ToString(),
            OwnerRef = owner,
            Tags = omTable.Tags.Select(t => t.TagFQN).ToList(),
            Classifications = omTable.Tags.Select(t => t.TagFQN).ToList(),
            Columns = columns
        };
    }
}
