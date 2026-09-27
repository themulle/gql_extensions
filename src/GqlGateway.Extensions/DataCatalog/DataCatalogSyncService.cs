namespace GqlGateway.Extensions.DataCatalog;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.DataCatalog.Models;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class DataCatalogSyncService : IDataCatalogSyncService
{
    private readonly IEnumerable<IDataCatalogClient> _clients;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<DataCatalogSyncService> _logger;

    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private readonly ConcurrentDictionary<TableIdentifier, CatalogTableAsset> _referencedCatalogAssets = new();

    public DataCatalogSyncService(
        IEnumerable<IDataCatalogClient> clients,
        ITableMetadataRepository metadataRepo,
        IOptions<GatewayOptions> options,
        ILogger<DataCatalogSyncService> logger)
    {
        _clients = clients;
        _metadataRepo = metadataRepo;
        _options = options;
        _logger = logger;
    }

    public async Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, CancellationToken ct = default)
    {
        if (!await SyncLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _logger.LogWarning("Data Catalog sync already in progress. Skipping concurrent request.");
            return new CatalogSyncResult(0, 0, 0, 0, [], ["Sync already in progress."], false);
        }

        try
        {
            var catalogOpts = _options.Value.Catalog;
            var providerType = catalogOpts.Provider;

            var client = _clients.FirstOrDefault(c => c.ProviderType == providerType)
                ?? _clients.FirstOrDefault();

            if (client == null)
            {
                _logger.LogWarning("No Data Catalog client registered for provider {Provider}", providerType);
                return new CatalogSyncResult(0, 0, 0, 0, [], [$"No client registered for provider {providerType}"], false);
            }

            _logger.LogInformation("Starting Data Catalog sync with provider {Provider}. Mode: {Mode}, DryRun: {DryRun}",
                client.ProviderType, catalogOpts.SyncMode, dryRun);

            IReadOnlyList<CatalogTableAsset> catalogTables;
            try
            {
                catalogTables = await client.GetTablesAsync(ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query tables from Data Catalog provider {Provider}", client.ProviderType);
                return new CatalogSyncResult(0, 0, 0, 0, [], [ex.Message], false);
            }

            int syncedTablesCount = 0;
            int syncedColumnsCount = 0;
            int maskedColumnsCount = 0;
            int art9ProtectedTablesCount = 0;
            var affectedTables = new List<TableIdentifier>();
            var warnings = new List<string>();

            foreach (var asset in catalogTables)
            {
                try
                {
                    var (metadata, colCount, maskCount, isArt9) = MapCatalogAssetToMetadata(asset, catalogOpts);

                    syncedTablesCount++;
                    syncedColumnsCount += colCount;
                    maskedColumnsCount += maskCount;
                    if (isArt9) art9ProtectedTablesCount++;
                    affectedTables.Add(asset.Identifier);

                    if (catalogOpts.SyncMode == DataCatalogSyncMode.Reference)
                    {
                        // Federated referencing mode: cache reference in-memory
                        _referencedCatalogAssets[asset.Identifier] = asset;
                    }

                    if (!dryRun && catalogOpts.SyncMode == DataCatalogSyncMode.Mirror)
                    {
                        // Mirror mode: persist into Governance Repository
                        await _metadataRepo.UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to map or persist catalog asset '{asset.Identifier}': {ex.Message}";
                    _logger.LogWarning(ex, "{Message}", msg);
                    warnings.Add(msg);
                }
            }

            _logger.LogInformation(
                "Data Catalog sync completed successfully. Tables: {Tables}, Columns: {Cols}, Masked: {Masked}, Art9: {Art9}",
                syncedTablesCount, syncedColumnsCount, maskedColumnsCount, art9ProtectedTablesCount);

            return new CatalogSyncResult(
                syncedTablesCount,
                syncedColumnsCount,
                maskedColumnsCount,
                art9ProtectedTablesCount,
                affectedTables,
                warnings,
                true);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    public async Task<TableMetadata?> EnrichOrReferenceTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var catalogOpts = _options.Value.Catalog;

        // 1. Check in-memory referenced catalog assets
        if (_referencedCatalogAssets.TryGetValue(table, out var cachedAsset))
        {
            var (metadata, _, _, _) = MapCatalogAssetToMetadata(cachedAsset, catalogOpts);
            return metadata;
        }

        // 2. Query live from active catalog provider
        var client = _clients.FirstOrDefault(c => c.ProviderType == catalogOpts.Provider)
            ?? _clients.FirstOrDefault();

        if (client == null) return null;

        try
        {
            var asset = await client.GetTableAsync(table, ct).ConfigureAwait(false);
            if (asset == null) return null;

            _referencedCatalogAssets[table] = asset;
            var (metadata, _, _, _) = MapCatalogAssetToMetadata(asset, catalogOpts);
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch table {Table} from Data Catalog on-demand", table);
            return null;
        }
    }

    private (TableMetadata Metadata, int ColumnCount, int MaskingRulesCount, bool IsArt9Protected) MapCatalogAssetToMetadata(
        CatalogTableAsset asset,
        DataCatalogOptions catalogOpts)
    {
        var allTableTags = asset.Tags.Concat(asset.Classifications).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check table-level GDPR Article 9 (health, biometric, religious, political data)
        bool isArt9Table = allTableTags.Any(t => catalogOpts.GdprArticle9Tags.Any(g => string.Equals(g, t, StringComparison.OrdinalIgnoreCase) || t.Contains(g, StringComparison.OrdinalIgnoreCase)));

        // Check table-level PII
        bool isPiiTable = isArt9Table || allTableTags.Any(t => catalogOpts.PiiTags.Any(p => string.Equals(p, t, StringComparison.OrdinalIgnoreCase) || t.Contains(p, StringComparison.OrdinalIgnoreCase)));

        var tableEntity = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = asset.Identifier.Domain,
            SchemaName = asset.Identifier.Schema,
            TableName = asset.Identifier.TableName,
            DisplayName = asset.DisplayName ?? asset.Identifier.TableName,
            SourceType = asset.SourceType,
            Sensitivity = isArt9Table ? "HIGH" : isPiiTable ? "HIGH" : "NORMAL",
            RequiresFourEyes = isArt9Table, // GDPR Art. 9 strictly enforces four-eyes approval
            IsActive = true
        };

        var columns = new List<TableColumn>();
        var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var colAsset in asset.Columns)
        {
            var colId = Guid.NewGuid();
            var allColTags = colAsset.Tags.Concat(colAsset.Classifications).ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool isArt9Col = allColTags.Any(t => catalogOpts.GdprArticle9Tags.Any(g => string.Equals(g, t, StringComparison.OrdinalIgnoreCase) || t.Contains(g, StringComparison.OrdinalIgnoreCase)));
            bool isPiiCol = isArt9Col || allColTags.Any(t => catalogOpts.PiiTags.Any(p => string.Equals(p, t, StringComparison.OrdinalIgnoreCase) || t.Contains(p, StringComparison.OrdinalIgnoreCase)));

            if (isArt9Col)
            {
                maskingRules[colAsset.ColumnName] = new MaskingRule
                {
                    Id = Guid.NewGuid(),
                    TableColumnId = colId,
                    RuleType = "REDACT",
                    Replacement = "[REDACTED-GDPR-ART9]"
                };
            }
            else if (isPiiCol)
            {
                // Match against TagToMaskingRuleMap or default to REDACT
                string ruleType = "REDACT";
                string? replacement = "[REDACTED]";
                string? pattern = null;
                string? hmacKeyId = null;

                foreach (var tag in allColTags)
                {
                    if (catalogOpts.TagToMaskingRuleMap.TryGetValue(tag, out var configuredRule))
                    {
                        ruleType = configuredRule;
                        if (string.Equals(ruleType, "MASK_EMAIL", StringComparison.OrdinalIgnoreCase))
                        {
                            replacement = null;
                        }
                        else if (string.Equals(ruleType, "HMAC_SHA256", StringComparison.OrdinalIgnoreCase))
                        {
                            pattern = "SHA256";
                            replacement = null;
                            hmacKeyId = _options.Value.DataMasking.HmacKeyId;
                        }
                        break;
                    }
                }

                maskingRules[colAsset.ColumnName] = new MaskingRule
                {
                    Id = Guid.NewGuid(),
                    TableColumnId = colId,
                    RuleType = ruleType,
                    PatternOrFormat = pattern,
                    Replacement = replacement,
                    HmacKeyId = hmacKeyId
                };
            }

            columns.Add(new TableColumn
            {
                Id = colId,
                TableId = tableEntity.Id,
                ColumnName = colAsset.ColumnName,
                DataType = colAsset.DataType,
                IsSensitive = isArt9Col || isPiiCol
            });
        }

        var metadata = new TableMetadata
        {
            Table = tableEntity,
            Identifier = asset.Identifier,
            Columns = columns,
            ColumnMaskingRules = maskingRules
        };

        return (metadata, columns.Count, maskingRules.Count, isArt9Table);
    }
}
