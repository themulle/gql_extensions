namespace GqlGateway.Extensions.DataCatalog;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Real-time Webhook Receiver for Enterprise Data Catalogs (OpenMetadata, Microsoft Purview, Collibra, Alation).
/// Validates HMAC-SHA256 signatures, prevents replay attacks, parses changed table entities,
/// and increments policy epochs to trigger zero-trust distributed cache invalidation.
/// </summary>
public sealed class CatalogWebhookHandler : IDataCatalogWebhookHandler
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly IPolicyEpochRepository _epochRepo;
    private readonly IDataCatalogSyncService _syncService;
    private readonly ILogger<CatalogWebhookHandler> _logger;

    private static readonly TimeSpan DefaultTimestampTolerance = TimeSpan.FromMinutes(5);

    public CatalogWebhookHandler(
        IOptions<GatewayOptions> options,
        IPolicyEpochRepository epochRepo,
        IDataCatalogSyncService syncService,
        ILogger<CatalogWebhookHandler> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _epochRepo = epochRepo ?? throw new ArgumentNullException(nameof(epochRepo));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CatalogWebhookResult> HandleWebhookAsync(
        string rawPayload,
        string? signature,
        DateTimeOffset? timestamp,
        string? provider = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);

        var opts = _options.Value;

        // 1. Replay attack verification (5-minute window)
        if (!opts.IsWebhookTimestampToleranceIgnored)
        {
            if (!timestamp.HasValue)
            {
                _logger.LogWarning("Catalog webhook rejected: missing timestamp header.");
                return new CatalogWebhookResult(false, "REJECTED_MISSING_TIMESTAMP", [], "Missing timestamp header.");
            }

            var delta = DateTimeOffset.UtcNow - timestamp.Value;
            if (delta.Duration() > DefaultTimestampTolerance)
            {
                _logger.LogWarning("Catalog webhook rejected: timestamp delta {Delta} exceeds allowed tolerance {Tolerance}.",
                    delta, DefaultTimestampTolerance);
                return new CatalogWebhookResult(false, "REJECTED_TIMESTAMP_EXPIRED", [], "Webhook timestamp expired.");
            }
        }

        // 2. HMAC-SHA256 signature validation with timing-safe comparison
        var secret = !string.IsNullOrWhiteSpace(opts.Catalog.WebhookSecret)
            ? opts.Catalog.WebhookSecret
            : opts.OpenMetadata.WebhookSecret;

        if (!opts.IsWebhookSignatureBypassed)
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                _logger.LogWarning("Catalog webhook rejected: missing signature header.");
                return new CatalogWebhookResult(false, "REJECTED_MISSING_SIGNATURE", [], "Missing signature header.");
            }

            if (!VerifyHmacSignature(rawPayload, signature, secret))
            {
                _logger.LogWarning("Catalog webhook rejected: invalid HMAC signature.");
                return new CatalogWebhookResult(false, "REJECTED_INVALID_SIGNATURE", [], "Invalid HMAC signature.");
            }
        }
        else
        {
            _logger.LogDebug("[INSECURE GETTING STARTED] Catalog webhook signature validation bypassed.");
        }

        // 3. Parse changed table entities from JSON payload
        var affectedTables = ExtractAffectedTables(rawPayload, provider);
        if (affectedTables.Count == 0)
        {
            _logger.LogInformation("Catalog webhook processed: no relevant table changes detected in payload.");
            return new CatalogWebhookResult(true, "IGNORED_NO_RELEVANT_TABLES", []);
        }

        // 4. Invalidate distributed cache & bump policy epochs
        foreach (var tableId in affectedTables)
        {
            try
            {
                await _epochRepo.IncrementTableEpochAsync(tableId, ct).ConfigureAwait(false);
                await _syncService.EnrichOrReferenceTableAsync(tableId, ct).ConfigureAwait(false);
                _logger.LogInformation("Real-time Data Catalog invalidation applied: bumped epoch for table '{Table}'", tableId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply real-time catalog invalidation for table '{Table}'", tableId);
            }
        }

        return new CatalogWebhookResult(true, "INVALIDATED", affectedTables);
    }

    internal static bool VerifyHmacSignature(string payload, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var expectedHash = hmac.ComputeHash(payloadBytes);

        var cleanSig = signature.Trim();
        if (cleanSig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            cleanSig = cleanSig[7..];
        }

        byte[] providedBytes;
        try
        {
            if (cleanSig.Length == 64 && cleanSig.All(Uri.IsHexDigit))
            {
                providedBytes = Convert.FromHexString(cleanSig);
            }
            else
            {
                providedBytes = Convert.FromBase64String(cleanSig);
            }
        }
        catch
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedHash, providedBytes);
    }

    private static IReadOnlyList<TableIdentifier> ExtractAffectedTables(string rawPayload, string? provider)
    {
        var tables = new List<TableIdentifier>();

        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var root = doc.RootElement;

            // OpenMetadata Change Event schema or single object
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("entityType", out var entType) &&
                    string.Equals(entType.GetString(), "table", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("entityFullyQualifiedName", out var fqnProp))
                    {
                        var fqn = fqnProp.GetString();
                        if (!string.IsNullOrWhiteSpace(fqn))
                        {
                            var parts = fqn.Split('.');
                            if (parts.Length >= 3)
                            {
                                tables.Add(new TableIdentifier(parts[0], parts[^2], parts[^1]));
                            }
                            else if (parts.Length == 2)
                            {
                                tables.Add(new TableIdentifier(parts[0], "public", parts[1]));
                            }
                        }
                    }
                }

                // Collibra / Generic schema
                if (root.TryGetProperty("assetName", out var assetProp) &&
                    root.TryGetProperty("domainName", out var domainProp))
                {
                    var domain = domainProp.GetString() ?? "default";
                    var asset = assetProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(asset))
                    {
                        tables.Add(new TableIdentifier(domain, "public", asset));
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                // Microsoft Purview / Azure EventGrid notification schema
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("eventType", out var evType) &&
                        item.TryGetProperty("data", out var dataElem) &&
                        dataElem.ValueKind == JsonValueKind.Object)
                    {
                        if (dataElem.TryGetProperty("qualifiedName", out var qnProp))
                        {
                            var qn = qnProp.GetString();
                            if (!string.IsNullOrWhiteSpace(qn))
                            {
                                var parts = qn.Split('/');
                                if (parts.Length >= 2)
                                {
                                    tables.Add(new TableIdentifier(parts[0], "public", parts[^1]));
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore parsing failures on malformed third-party webhooks
        }

        return tables;
    }
}
