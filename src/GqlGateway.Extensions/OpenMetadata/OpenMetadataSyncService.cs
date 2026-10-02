using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Application.OpenMetadata.Models;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GqlGateway.Extensions.OpenMetadata;

public sealed class OpenMetadataSyncService : IOpenMetadataSyncService
{
    private readonly IOpenMetadataClient _client;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IConsentRepository _consentRepo;
    private readonly IPolicyEpochRepository _epochRepo;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<OpenMetadataSyncService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// SEC H-19: Marker stored in <see cref="Consent.ConsentRequestId"/> for consents created by this sync (used for reconcile).
    /// </summary>
    internal static readonly Guid OpenMetadataSyncConsentMarker = new("0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19");
    private static readonly Sid SyncActorSid = new("system:openmetadata-sync");
    private static readonly HashSet<string> AllowDataOperations = new(StringComparer.OrdinalIgnoreCase) { "ViewAll", "ViewSampleData" };
    private static readonly HashSet<string> DenyDataOperations = new(StringComparer.OrdinalIgnoreCase) { "All", "ViewAll", "ViewSampleData" };

    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> ProcessedWebhookEvents = new();

    public OpenMetadataSyncService(
        IOpenMetadataClient client,
        ITableMetadataRepository metadataRepo,
        IConsentRepository consentRepo,
        IPolicyEpochRepository epochRepo,
        IOptions<GatewayOptions> options,
        ILogger<OpenMetadataSyncService> logger)
    {
        _client = client;
        _metadataRepo = metadataRepo;
        _consentRepo = consentRepo;
        _epochRepo = epochRepo;
        _options = options;
        _logger = logger;
    }

    public async Task<OpenMetadataSyncResult> SyncPermissionsAsync(bool dryRun = false, CancellationToken ct = default)
    {
        if (!await SyncLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _logger.LogWarning("OpenMetadata sync is already in progress. Skipping concurrent request.");
            return new OpenMetadataSyncResult(0, 0, 0, Array.Empty<TableIdentifier>(), ["Sync already in progress."], false);
        }

        try
        {
            var omOptions = _options.Value.OpenMetadata;
            var warnings = new List<string>();
            var affectedTables = new HashSet<TableIdentifier>();

        int syncedTablesCount = 0;
        int syncedMaskingRulesCount = 0;
        int syncedConsentsCount = 0;

        _logger.LogInformation("Starting OpenMetadata synchronization. DryRun: {DryRun}", dryRun);

        // 1. Fetch tables
        IReadOnlyList<OpenMetadataTable> omTables;
        try
        {
            var serviceFilter = string.IsNullOrWhiteSpace(omOptions.ServiceFilter) ? null : omOptions.ServiceFilter;
            omTables = await _client.GetTablesAsync(serviceFilter, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch tables from OpenMetadata.");
            return new OpenMetadataSyncResult(0, 0, 0, Array.Empty<TableIdentifier>(), [ex.Message], false);
        }

        var tableMetadataMap = new Dictionary<TableIdentifier, TableMetadata>();

        foreach (var omTable in omTables)
        {
            try
            {
                var tableId = ParseTableIdentifier(omTable);
                var (tableMeta, maskingCount) = MapToTableMetadata(omTable, tableId, omOptions);

                tableMetadataMap[tableId] = tableMeta;
                syncedTablesCount++;
                syncedMaskingRulesCount += maskingCount;
                affectedTables.Add(tableId);

                if (!dryRun)
                {
                    // SEC M-32: merge with persisted state – sync may only tighten governance flags.
                    var existing = await _metadataRepo.GetTableMetadataAsync(tableId, ct);
                    var merged = GqlGateway.Application.DataCatalog.Services.CatalogGovernanceRatchet.Merge(tableMeta, existing);
                    tableMetadataMap[tableId] = merged;
                    await _metadataRepo.UpsertTableMetadataAsync(merged, ct);
                }
            }
            catch (Exception ex)
            {
                var warning = $"Failed to map or upsert table {omTable.FullyQualifiedName}: {ex.Message}";
                _logger.LogWarning(ex, "{Warning}", warning);
                warnings.Add(warning);
            }
        }

        // 2. Fetch Policies, Roles, Teams, Users for Consent mapping
        try
        {
            var policies = await _client.GetPoliciesAsync(ct);
            var roles = await _client.GetRolesAsync(ct);
            var teams = await _client.GetTeamsAsync(ct);
            var users = await _client.GetUsersAsync(ct);

            var policyById = policies.ToDictionary(p => p.Id);
            var policyByName = policies.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            // Map Team SIDs
            var teamSidMap = new Dictionary<string, Sid>(StringComparer.OrdinalIgnoreCase);
            foreach (var team in teams)
            {
                if (TryResolveSid(team.Name, team.FullyQualifiedName, omOptions.TeamToGroupSidMap, out var sid))
                {
                    teamSidMap[team.Name] = sid;
                    if (!string.IsNullOrEmpty(team.FullyQualifiedName))
                    {
                        teamSidMap[team.FullyQualifiedName] = sid;
                    }
                }
            }

            // Map User SIDs
            var userSidMap = new Dictionary<string, Sid>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in users)
            {
                if (TryResolveSid(user.Name, user.Email, omOptions.UserToUserSidMap, out var sid))
                {
                    userSidMap[user.Name] = sid;
                    if (!string.IsNullOrEmpty(user.Email))
                    {
                        userSidMap[user.Email] = sid;
                    }
                }
            }

            // SEC H-19: consents are only derived from unconditional rules with explicit data-read operations,
            // rules on resource "all" no longer apply to every table, and sync-created consents are reconciled.
            var desired = new Dictionary<SyncConsentKey, Consent>();

            void AddDesired(Consent consent)
            {
                var key = SyncConsentKey.From(consent);
                desired.TryAdd(key, consent);
            }

            // 2a. Role policies
            foreach (var role in roles)
            {
                var rolePolicies = ResolvePolicies(role.Policies, policyById, policyByName);
                foreach (var policy in rolePolicies.Where(p => p.Enabled))
                {
                    foreach (var rule in policy.Rules)
                    {
                        if (!TryGetDataAccessEffect(rule, policy.Name, out var effect))
                        {
                            continue;
                        }

                        foreach (var (tableIdent, tableMeta) in tableMetadataMap)
                        {
                            if (IsRuleApplicableToTable(rule, tableIdent))
                            {
                                AddDesired(CreateConsentForRole(role.Name, tableMeta, effect));
                            }
                        }
                    }
                }
            }

            // 2b. Team policies
            foreach (var team in teams)
            {
                if (!teamSidMap.TryGetValue(team.Name, out var teamSid) &&
                    (string.IsNullOrEmpty(team.FullyQualifiedName) || !teamSidMap.TryGetValue(team.FullyQualifiedName, out teamSid)))
                {
                    continue;
                }

                var teamPolicies = ResolvePolicies(team.Policies, policyById, policyByName);
                foreach (var policy in teamPolicies.Where(p => p.Enabled))
                {
                    foreach (var rule in policy.Rules)
                    {
                        if (!TryGetDataAccessEffect(rule, policy.Name, out var effect))
                        {
                            continue;
                        }

                        foreach (var (tableIdent, tableMeta) in tableMetadataMap)
                        {
                            if (IsRuleApplicableToTable(rule, tableIdent))
                            {
                                AddDesired(CreateConsentForSid(GranteeType.Group, teamSid, tableMeta, effect));
                            }
                        }
                    }
                }
            }

            // 2c. User policies
            foreach (var user in users)
            {
                if (!userSidMap.TryGetValue(user.Name, out var userSid) &&
                    (string.IsNullOrEmpty(user.Email) || !userSidMap.TryGetValue(user.Email, out userSid)))
                {
                    continue;
                }

                // Collect policies from user's roles
                var userPolicies = new List<OpenMetadataPolicy>();
                foreach (var roleRef in user.Roles)
                {
                    var matchedRole = roles.FirstOrDefault(r =>
                        (roleRef.Id.HasValue && r.Id == roleRef.Id.Value) ||
                        string.Equals(r.Name, roleRef.Name, StringComparison.OrdinalIgnoreCase));
                    if (matchedRole != null)
                    {
                        userPolicies.AddRange(ResolvePolicies(matchedRole.Policies, policyById, policyByName));
                    }
                }

                foreach (var policy in userPolicies.Where(p => p.Enabled).DistinctBy(p => p.Id))
                {
                    foreach (var rule in policy.Rules)
                    {
                        if (!TryGetDataAccessEffect(rule, policy.Name, out var effect))
                        {
                            continue;
                        }

                        foreach (var (tableIdent, tableMeta) in tableMetadataMap)
                        {
                            if (IsRuleApplicableToTable(rule, tableIdent))
                            {
                                AddDesired(CreateConsentForSid(GranteeType.User, userSid, tableMeta, effect));
                            }
                        }
                    }
                }
            }

            // Load the currently active consents of all known OM grantees once (used for dedup and reconcile).
            var knownSids = new HashSet<Sid>();
            foreach (var sidValue in omOptions.TeamToGroupSidMap.Values.Concat(omOptions.UserToUserSidMap.Values))
            {
                if (!string.IsNullOrWhiteSpace(sidValue)) knownSids.Add(new Sid(sidValue));
            }
            foreach (var sid in teamSidMap.Values.Concat(userSidMap.Values))
            {
                knownSids.Add(sid);
            }
            var knownRoles = roles.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            IReadOnlyList<Consent> activeConsents = Array.Empty<Consent>();
            if (!dryRun)
            {
                activeConsents = await _consentRepo.GetAllActiveConsentsForSubjectsAsync(knownSids, knownRoles, DateTimeOffset.UtcNow, ct) ?? Array.Empty<Consent>();
            }

            foreach (var (key, consent) in desired)
            {
                try
                {
                    consent.Validate();
                    syncedConsentsCount++;

                    if (consent.Effect == ConsentEffect.Allow && !omOptions.AutoCreateConsents)
                    {
                        // SEC H-19: no automatic data grants from metadata policies – proposal only (requires four-eyes approval in the gateway).
                        _logger.LogInformation(
                            "OpenMetadata consent proposal (not created, AutoCreateConsents=false): {Effect} {GranteeType} '{Grantee}' on {Table}.",
                            consent.Effect, consent.GranteeType, key.Grantee, consent.TableIdentifier);
                        continue;
                    }

                    affectedTables.Add(consent.TableIdentifier);

                    if (!dryRun)
                    {
                        bool alreadyExists = activeConsents.Any(c => SyncConsentKey.From(c).Equals(key));
                        if (!alreadyExists)
                        {
                            await _consentRepo.CreateConsentAsync(consent, ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var warn = $"Failed to create consent for {consent.TableIdentifier}: {ex.Message}";
                    _logger.LogWarning(ex, "{Warning}", warn);
                    warnings.Add(warn);
                }
            }

            // SEC H-19: reconcile – revoke sync-created consents whose source rule no longer exists (only for tables synced in this run).
            if (!dryRun)
            {
                foreach (var stale in activeConsents.Where(c =>
                             c.ConsentRequestId == OpenMetadataSyncConsentMarker &&
                             tableMetadataMap.ContainsKey(c.TableIdentifier) &&
                             !desired.ContainsKey(SyncConsentKey.From(c))))
                {
                    try
                    {
                        await _consentRepo.RevokeConsentAsync(stale.Id, SyncActorSid, "OpenMetadata source policy rule no longer grants this access.", ct);
                        affectedTables.Add(stale.TableIdentifier);
                        _logger.LogInformation("Revoked stale OpenMetadata-synced consent {ConsentId} on {Table}.", stale.Id, stale.TableIdentifier);
                    }
                    catch (Exception ex)
                    {
                        var warn = $"Failed to revoke stale consent {stale.Id}: {ex.Message}";
                        _logger.LogWarning(ex, "{Warning}", warn);
                        warnings.Add(warn);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var warn = $"Failed during policy/role/team synchronization: {ex.Message}";
            _logger.LogWarning(ex, "{Warning}", warn);
            warnings.Add(warn);
        }

        // 3. Increment policy epochs for affected tables
        if (!dryRun)
        {
            foreach (var table in affectedTables)
            {
                try
                {
                    await _epochRepo.IncrementTableEpochAsync(table, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to increment epoch for table {Table}", table);
                }
            }
        }

        _logger.LogInformation(
            "OpenMetadata synchronization completed. Tables: {Tables}, Consents: {Consents}, MaskingRules: {Masks}, DryRun: {DryRun}",
            syncedTablesCount, syncedConsentsCount, syncedMaskingRulesCount, dryRun);

            return new OpenMetadataSyncResult(
                syncedTablesCount,
                syncedConsentsCount,
                syncedMaskingRulesCount,
                affectedTables.ToList(),
                warnings,
                true);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    public async Task<bool> HandleWebhookEventAsync(string eventPayload, string? signatureHeader = null, CancellationToken ct = default)
    {
        var omOptions = _options.Value.OpenMetadata;

        bool bypassSignature = _options.Value.IsWebhookSignatureBypassed;
        if (!bypassSignature)
        {
            // Enforce signature verification (fail closed)
            if (string.IsNullOrWhiteSpace(omOptions.WebhookSecret))
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: WebhookSecret is not configured.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(signatureHeader))
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: missing signature header.");
                return false;
            }

            if (!VerifyWebhookSignature(eventPayload, signatureHeader, omOptions.WebhookSecret, _logger))
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: signature verification failed.");
                return false;
            }
        }
        else
        {
            _logger.LogWarning("[INSECURE GETTING STARTED] Bypassing OpenMetadata webhook signature verification.");
        }

        OpenMetadataWebhookEvent? webhookEvent;
        try
        {
            webhookEvent = JsonSerializer.Deserialize<OpenMetadataWebhookEvent>(eventPayload, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize OpenMetadata webhook payload.");
            return false;
        }

        if (webhookEvent == null)
        {
            _logger.LogWarning("OpenMetadata webhook event payload is null.");
            return false;
        }

        bool ignoreTimestampTolerance = _options.Value.IsWebhookTimestampToleranceIgnored;
        if (!ignoreTimestampTolerance)
        {
            // SEC-07: Enforce mandatory Id and Timestamp to prevent replay attacks (fail-closed)
            if (!webhookEvent.Id.HasValue || !webhookEvent.Timestamp.HasValue)
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: mandatory event 'Id' or 'Timestamp' is missing (fail-closed replay defense).");
                return false;
            }

            // Validate timestamp to prevent replay attacks (tolerance: 5 minutes)
            var eventTime = webhookEvent.Timestamp.Value > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(webhookEvent.Timestamp.Value)
                : DateTimeOffset.FromUnixTimeSeconds(webhookEvent.Timestamp.Value);

            var skew = Math.Abs((DateTimeOffset.UtcNow - eventTime).TotalMinutes);
            if (skew > 5)
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: event timestamp is skewed or outside acceptable replay window ({Skew:F1} minutes).", skew);
                return false;
            }
        }

        // Event ID deduplication
        if (webhookEvent.Id.HasValue && !ProcessedWebhookEvents.TryAdd(webhookEvent.Id.Value, DateTimeOffset.UtcNow))
        {
            _logger.LogInformation("OpenMetadata webhook event {EventId} has already been processed. Skipping duplicate.", webhookEvent.Id.Value);
            return true;
        }

            // Bound the size of the deduplication dictionary
            if (ProcessedWebhookEvents.Count > 10_000)
            {
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
                foreach (var (k, v) in ProcessedWebhookEvents)
                {
                    if (v < cutoff)
                    {
                        ProcessedWebhookEvents.TryRemove(k, out _);
                    }
                }
            }

        _logger.LogInformation(
            "Processing OpenMetadata webhook event: Type={Type}, EntityType={Entity}, FQN={Fqn}",
            webhookEvent.EventType, webhookEvent.EntityType, webhookEvent.EntityFullyQualifiedName);

        if (string.Equals(webhookEvent.EntityType, "table", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(webhookEvent.EntityFullyQualifiedName))
        {
            var omTable = await _client.GetTableByFqnAsync(webhookEvent.EntityFullyQualifiedName, ct);
            if (omTable != null)
            {
                var tableId = ParseTableIdentifier(omTable);
                var (tableMeta, _) = MapToTableMetadata(omTable, tableId, omOptions);
                // SEC M-32: webhook-triggered updates use the same tighten-only merge.
                var existing = await _metadataRepo.GetTableMetadataAsync(tableId, ct);
                var merged = GqlGateway.Application.DataCatalog.Services.CatalogGovernanceRatchet.Merge(tableMeta, existing);
                await _metadataRepo.UpsertTableMetadataAsync(merged, ct);
                await _epochRepo.IncrementTableEpochAsync(tableId, ct);
                return true;
            }
        }
        else if (string.Equals(webhookEvent.EntityType, "policy", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(webhookEvent.EntityType, "role", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(webhookEvent.EntityType, "team", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(webhookEvent.EntityType, "user", StringComparison.OrdinalIgnoreCase))
        {
            var result = await SyncPermissionsAsync(dryRun: false, ct);
            return result.Success;
        }

        return true;
    }

    public static bool VerifyWebhookSignature(string payload, string signatureHeader, string secret, ILogger? logger = null)
    {
        try
        {
            var cleanSignature = signatureHeader.Trim();
            if (cleanSignature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                cleanSignature = cleanSignature[7..];
            }

            var expectedBytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
            var expectedHex = Convert.ToHexStringLower(expectedBytes);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedHex),
                Encoding.UTF8.GetBytes(cleanSignature.ToLowerInvariant()));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "OpenMetadata webhook signature verification failed due to an exception.");
            return false;
        }
    }

    private static TableIdentifier ParseTableIdentifier(OpenMetadataTable table)
    {
        var domain = table.Service?.Name ?? table.Database?.Name;
        var schema = table.DatabaseSchema?.Name;
        var tableName = table.Name;

        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(schema))
        {
            // Parse from FullyQualifiedName: service.database.schema.table
            var parts = table.FullyQualifiedName.Split('.');
            if (parts.Length >= 4)
            {
                domain ??= parts[0];
                schema ??= parts[2];
                tableName = parts[3];
            }
            else if (parts.Length == 3)
            {
                domain ??= parts[0];
                schema ??= parts[1];
                tableName = parts[2];
            }
            else if (parts.Length == 2)
            {
                domain ??= "default";
                schema ??= parts[0];
                tableName = parts[1];
            }
        }

        domain = string.IsNullOrWhiteSpace(domain) ? "default" : domain;
        schema = string.IsNullOrWhiteSpace(schema) ? "dbo" : schema;

        return new TableIdentifier(domain, schema, tableName);
    }

    private (TableMetadata Metadata, int MaskingRulesCount) MapToTableMetadata(
        OpenMetadataTable omTable,
        TableIdentifier tableId,
        OpenMetadataOptions omOptions)
    {
        var tableEntity = new Table
        {
            Id = omTable.Id == Guid.Empty ? Guid.NewGuid() : omTable.Id,
            SourceType = omTable.ServiceType ?? "PostgreSQL",
            SourceName = tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            DisplayName = omTable.DisplayName ?? omTable.Name,
            Sensitivity = omTable.Tags.Any(t => t.TagFQN.Contains("Sensitive", StringComparison.OrdinalIgnoreCase)) ? "HIGH" : "NORMAL",
            RequiresFourEyes = omTable.Tags.Any(t => t.TagFQN.Contains("FourEyes", StringComparison.OrdinalIgnoreCase)),
            IsActive = true
        };

        var columns = new List<TableColumn>();
        var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var omCol in omTable.Columns)
        {
            var colId = Guid.NewGuid();
            bool isSensitive = false;

            foreach (var tag in omCol.Tags)
            {
                string? ruleType = null;
                if (!omOptions.TagToMaskingRuleMap.TryGetValue(tag.TagFQN, out ruleType))
                {
                    if (tag.TagFQN.StartsWith("PII.", StringComparison.OrdinalIgnoreCase) ||
                        tag.TagFQN.StartsWith("PersonalData.", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tag.TagFQN, "PII", StringComparison.OrdinalIgnoreCase))
                    {
                        ruleType = "REDACT";
                    }
                }

                if (ruleType != null)
                {
                    isSensitive = true;
                    var maskingRule = new MaskingRule
                    {
                        Id = Guid.NewGuid(),
                        TableColumnId = colId,
                        RuleType = ruleType,
                        PatternOrFormat = string.Equals(ruleType, "HMAC_SHA256", StringComparison.OrdinalIgnoreCase) ? "SHA256" : null,
                        Replacement = string.Equals(ruleType, "REDACT", StringComparison.OrdinalIgnoreCase) ? "[REDACTED]" : null,
                        HmacKeyId = string.Equals(ruleType, "HMAC_SHA256", StringComparison.OrdinalIgnoreCase) ? _options.Value.DataMasking.HmacKeyId : null
                    };
                    maskingRules[omCol.Name] = maskingRule;
                    break;
                }
            }

            columns.Add(new TableColumn
            {
                Id = colId,
                TableId = tableEntity.Id,
                ColumnName = omCol.Name,
                DataType = omCol.DataType,
                IsSensitive = isSensitive
            });
        }

        var metadata = new TableMetadata
        {
            Table = tableEntity,
            Identifier = tableId,
            Columns = columns,
            ColumnMaskingRules = maskingRules
        };

        return (metadata, maskingRules.Count);
    }

    private static bool TryResolveSid(
        string primaryName,
        string? secondaryKey,
        IReadOnlyDictionary<string, string> map,
        out Sid sid)
    {
        if (map.TryGetValue(primaryName, out var sidStr) ||
            (!string.IsNullOrWhiteSpace(secondaryKey) && map.TryGetValue(secondaryKey, out sidStr)))
        {
            sid = new Sid(sidStr);
            return true;
        }

        if (primaryName.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            sid = new Sid(primaryName);
            return true;
        }

        sid = default;
        return false;
    }

    private static List<OpenMetadataPolicy> ResolvePolicies(
        IEnumerable<OpenMetadataEntityReference> policyRefs,
        IReadOnlyDictionary<Guid, OpenMetadataPolicy> byId,
        IReadOnlyDictionary<string, OpenMetadataPolicy> byName)
    {
        var resolved = new List<OpenMetadataPolicy>();
        foreach (var pRef in policyRefs)
        {
            if (pRef.Id.HasValue && byId.TryGetValue(pRef.Id.Value, out var policyById))
            {
                resolved.Add(policyById);
            }
            else if (!string.IsNullOrEmpty(pRef.Name) && byName.TryGetValue(pRef.Name, out var policyByName))
            {
                resolved.Add(policyByName);
            }
        }
        return resolved;
    }

    private static bool IsRuleApplicableToTable(OpenMetadataRule rule, TableIdentifier table)
    {
        // SEC H-19: resource "all" is no longer treated as "every table"; only the explicit entity type "table"
        // or explicitly named tables apply.
        if (rule.Resources.Any(r => string.Equals(r, "table", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return rule.Resources.Any(r =>
            string.Equals(r, table.TableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, $"{table.Schema}.{table.TableName}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, $"{table.Domain}.{table.Schema}.{table.TableName}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// SEC H-19: Only unconditional rules with explicit data-read operations produce consents.
    /// Allow: ViewAll / ViewSampleData. Deny (tightening only): additionally "All".
    /// </summary>
    private bool TryGetDataAccessEffect(OpenMetadataRule rule, string policyName, out ConsentEffect effect)
    {
        var isDeny = string.Equals(rule.Effect, "deny", StringComparison.OrdinalIgnoreCase);
        effect = isDeny ? ConsentEffect.Deny : ConsentEffect.Allow;

        if (!isDeny && !string.Equals(rule.Effect, "allow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Condition))
        {
            _logger.LogWarning("Ignoring OpenMetadata rule '{Rule}' in policy '{Policy}': conditional rules are not mapped to gateway consents.", rule.Name, policyName);
            return false;
        }

        var allowedOperations = isDeny ? DenyDataOperations : AllowDataOperations;
        if (!rule.Operations.Any(op => allowedOperations.Contains(op)))
        {
            _logger.LogDebug("Ignoring OpenMetadata rule '{Rule}' in policy '{Policy}': no data-read operation.", rule.Name, policyName);
            return false;
        }

        return true;
    }

    internal readonly record struct SyncConsentKey(TableIdentifier Table, GranteeType GranteeType, string Grantee, ConsentEffect Effect)
    {
        public static SyncConsentKey From(Consent consent)
        {
            var grantee = consent.GranteeType == GranteeType.Role
                ? (consent.RoleName ?? string.Empty).ToUpperInvariant()
                : (consent.GranteeSid?.Value ?? string.Empty).ToUpperInvariant();
            return new SyncConsentKey(consent.TableIdentifier, consent.GranteeType, grantee, consent.Effect);
        }
    }

    private static Consent CreateConsentForRole(string roleName, TableMetadata table, ConsentEffect effect)
    {
        return new Consent
        {
            Id = Guid.NewGuid(),
            ConsentRequestId = OpenMetadataSyncConsentMarker,
            TableId = table.Table.Id,
            TableIdentifier = table.Identifier,
            GranteeType = GranteeType.Role,
            RoleName = roleName,
            Effect = effect,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddYears(1)
        };
    }

    private static Consent CreateConsentForSid(GranteeType granteeType, Sid sid, TableMetadata table, ConsentEffect effect)
    {
        return new Consent
        {
            Id = Guid.NewGuid(),
            TableId = table.Table.Id,
            TableIdentifier = table.Identifier,
            ConsentRequestId = OpenMetadataSyncConsentMarker,
            GranteeType = granteeType,
            GranteeSid = sid,
            Effect = effect,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddYears(1)
        };
    }
}
