namespace GqlGateway.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Dbt.Interfaces;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class DbtMetadataIngestionService : IDbtMetadataIngestionService
{
    private readonly IDbtProposalRepository _proposalRepository;
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ILineageGraphStore _lineageGraphStore;
    private readonly IPolicyEpochRepository? _epochRepository;
    private readonly ILogger<DbtMetadataIngestionService> _logger;

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, null, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        ILogger<DbtMetadataIngestionService> logger)
    {
        _proposalRepository = proposalRepository ?? throw new ArgumentNullException(nameof(proposalRepository));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _lineageGraphStore = lineageGraphStore ?? throw new ArgumentNullException(nameof(lineageGraphStore));
        _epochRepository = epochRepository;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DbtSyncResult> IngestManifestFileAsync(string filePath, bool dryRun = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        ValidateSafeFilePath(fullPath);

        if (!File.Exists(fullPath))
        {
            return new DbtSyncResult(false, 0, 0, 0, [], $"Dbt manifest file not found at: {fullPath}");
        }

        await using var stream = File.OpenRead(fullPath);
        return await IngestManifestStreamAsync(stream, dryRun, ct).ConfigureAwait(false);
    }

    private static void ValidateSafeFilePath(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.StartsWith("/etc") ||
            normalized.StartsWith("/proc") ||
            normalized.StartsWith("/sys") ||
            normalized.StartsWith("/dev") ||
            normalized.StartsWith("/root/.ssh") ||
            normalized.Contains("/.ssh/") ||
            normalized.Contains("/appsettings") ||
            normalized.Contains("windows/system32"))
        {
            throw new System.Security.SecurityException($"Access to restricted path '{fullPath}' is strictly forbidden.");
        }
    }

    public async Task<DbtSyncResult> IngestManifestStreamAsync(Stream manifestStream, bool dryRun = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifestStream);

        var warnings = new List<string>();
        IReadOnlyList<DbtModelDefinition> models;

        try
        {
            models = await DbtArtifactStreamingParser.ParseManifestStreamAsync(manifestStream, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse dbt manifest stream.");
            return new DbtSyncResult(false, 0, 0, 0, [], $"Manifest parse failure: {ex.Message}");
        }

        _logger.LogInformation("Parsed {Count} dbt models from manifest.", models.Count);

        var generatedProposals = 0;
        var lineageNodesToUpdate = new List<LineageNode>();

        // Build a mapping from unique_id to TableIdentifier string
        var uniqueIdToTableId = models.ToDictionary(m => m.UniqueId, m => m.ToTableIdentifier().ToString(), StringComparer.OrdinalIgnoreCase);

        // Build child map (downstream nodes)
        var childMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            var myTableId = m.ToTableIdentifier().ToString();
            foreach (var depUniqueId in m.DependsOnNodes)
            {
                if (uniqueIdToTableId.TryGetValue(depUniqueId, out var parentTableId))
                {
                    if (!childMap.TryGetValue(parentTableId, out var children))
                    {
                        children = [];
                        childMap[parentTableId] = children;
                    }
                    if (!children.Contains(myTableId))
                    {
                        children.Add(myTableId);
                    }
                }
            }
        }

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            var tableId = model.ToTableIdentifier();
            var ownerTeam = model.Meta.GetValueOrDefault("owner");

            // Deduplication: get existing pending proposals for this table
            var existingPending = dryRun
                ? []
                : await _proposalRepository.GetPendingProposalsAsync(tableId, ct).ConfigureAwait(false);

            // 1. Zero-Trust Proposal Evaluation (SEC-DBT-01)
            foreach (var (colName, colDef) in model.Columns)
            {
                var isPii = false;
                var suggestedRule = "REDACT";

                if (colDef.Meta.TryGetValue("pii", out var piiVal) &&
                    (string.Equals(piiVal, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(piiVal, "1", StringComparison.OrdinalIgnoreCase)))
                {
                    isPii = true;
                }

                if (colDef.Tags.Any(t => t.Contains("email", StringComparison.OrdinalIgnoreCase)) ||
                    colName.Contains("email", StringComparison.OrdinalIgnoreCase))
                {
                    isPii = true;
                    suggestedRule = "MASK_EMAIL";
                }
                else if (colDef.Tags.Any(t => t.Contains("ssn", StringComparison.OrdinalIgnoreCase)) ||
                         colName.Contains("ssn", StringComparison.OrdinalIgnoreCase))
                {
                    isPii = true;
                    suggestedRule = "REDACT";
                }
                else if (colDef.Tags.Any(t => t.Contains("pseudonym", StringComparison.OrdinalIgnoreCase)))
                {
                    isPii = true;
                    suggestedRule = "HMAC_SHA256";
                }

                if (isPii)
                {
                    // Check if already pending review to avoid duplicate proposals
                    if (existingPending.Any(p => string.Equals(p.ColumnName, colName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var sourceTag = colDef.Tags.FirstOrDefault() ??
                        (colDef.Meta.ContainsKey("pii") ? "meta.pii=true" : $"column:{colName}");

                    var proposal = new DbtMetadataProposal(
                        Id: Guid.NewGuid(),
                        Table: tableId,
                        ColumnName: colName,
                        SuggestedRuleType: suggestedRule,
                        SuggestedSensitivity: "HIGH",
                        SuggestedOwnerTeam: ownerTeam,
                        SourceDbtTag: sourceTag,
                        Status: DbtProposalStatus.PendingReview,
                        CreatedAt: DateTimeOffset.UtcNow
                    );

                    if (!dryRun)
                    {
                        await _proposalRepository.AddProposalAsync(proposal, ct).ConfigureAwait(false);
                    }

                    generatedProposals++;
                }
            }

            // 2. Lineage Node Construction
            var myTableIdStr = tableId.ToString();
            var downstream = childMap.TryGetValue(myTableIdStr, out var cList) ? cList : (IReadOnlyCollection<string>)[];

            lineageNodesToUpdate.Add(new LineageNode(
                Id: myTableIdStr,
                Name: model.Name,
                Type: LineageNodeType.Table,
                DownstreamNodeIds: downstream,
                OwnerTeam: ownerTeam,
                OwnerEmail: model.Meta.GetValueOrDefault("owner_email")
            ));
        }

        // Apply lineage updates
        if (!dryRun && lineageNodesToUpdate.Count > 0)
        {
            _lineageGraphStore.UpdateGraph(lineageNodesToUpdate);
        }

        _logger.LogInformation("Completed dbt ingestion: {Models} models, {Proposals} proposals, {Lineage} lineage nodes.",
            models.Count, generatedProposals, lineageNodesToUpdate.Count);

        return new DbtSyncResult(
            Success: true,
            ParsedModelsCount: models.Count,
            GeneratedProposalsCount: generatedProposals,
            UpdatedLineageNodesCount: lineageNodesToUpdate.Count,
            Warnings: warnings
        );
    }

    public async Task<DbtMetadataProposal> ApproveProposalAsync(Guid proposalId, string reviewedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedBy);

        var proposal = await _proposalRepository.GetProposalByIdAsync(proposalId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Dbt metadata proposal '{proposalId}' not found.");

        var updated = await _proposalRepository.UpdateProposalStatusAsync(proposalId, DbtProposalStatus.Approved, reviewedBy, ct).ConfigureAwait(false);

        var tableMeta = await _metadataRepository.GetTableMetadataAsync(proposal.Table, ct).ConfigureAwait(false);
        if (tableMeta != null)
        {
            var newRule = new MaskingRule
            {
                RuleType = proposal.SuggestedRuleType,
                Replacement = proposal.SuggestedRuleType == "REDACT" ? "[REDACTED]" : null
            };

            var updatedRules = new Dictionary<string, MaskingRule>(tableMeta.ColumnMaskingRules, StringComparer.OrdinalIgnoreCase)
            {
                [proposal.ColumnName] = newRule
            };

            var newTableMeta = new TableMetadata
            {
                Table = tableMeta.Table,
                Identifier = tableMeta.Identifier,
                Columns = tableMeta.Columns,
                PrimaryKeyColumns = tableMeta.PrimaryKeyColumns,
                ColumnMaskingRules = updatedRules
            };

            await _metadataRepository.UpsertTableMetadataAsync(newTableMeta, ct).ConfigureAwait(false);

            if (_epochRepository != null)
            {
                await _epochRepository.IncrementTableEpochAsync(proposal.Table, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Applied approved dbt proposal {Id} to table {Table} column {Column} with rule {Rule}.",
                proposalId, proposal.Table, proposal.ColumnName, proposal.SuggestedRuleType);
        }

        return updated;
    }

    public async Task<DbtMetadataProposal> RejectProposalAsync(Guid proposalId, string reviewedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedBy);
        return await _proposalRepository.UpdateProposalStatusAsync(proposalId, DbtProposalStatus.Rejected, reviewedBy, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default)
    {
        return _proposalRepository.GetPendingProposalsAsync(table, ct);
    }
}
