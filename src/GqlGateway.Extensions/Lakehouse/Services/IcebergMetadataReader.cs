namespace GqlGateway.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;
using GqlGateway.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Parser and loader for Apache Iceberg v2 table metadata, manifest lists, and data manifests.
/// </summary>
public sealed class IcebergMetadataReader : IIcebergMetadataReader
{
    private readonly ILakehouseStorageProvider _storageProvider;
    private readonly ILogger<IcebergMetadataReader> _logger;

    public IcebergMetadataReader(
        ILakehouseStorageProvider storageProvider,
        ILogger<IcebergMetadataReader> logger)
    {
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<IcebergTableMetadata> LoadTableMetadataAsync(
        string metadataLocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataLocation);

        var jsonText = await _storageProvider.ReadTextAsync(metadataLocation, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        var tableUuid = root.TryGetProperty("table-uuid", out var uuidProp) ? uuidProp.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
        var formatVersion = root.TryGetProperty("format-version", out var formatProp) ? formatProp.GetInt32() : 2;
        var location = root.TryGetProperty("location", out var locProp) ? locProp.GetString() ?? "" : "";
        var lastSeq = root.TryGetProperty("last-sequence-number", out var seqProp) ? seqProp.GetInt64() : 0L;
        var lastUpdated = root.TryGetProperty("last-updated-ms", out var updatedProp) ? updatedProp.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var currentSnapshotId = root.TryGetProperty("current-snapshot-id", out var snapProp) ? snapProp.GetInt64() : -1L;

        // Parse Current Schema
        var schemaFields = new List<IcebergField>();
        if (root.TryGetProperty("schemas", out var schemasProp) && schemasProp.ValueKind == JsonValueKind.Array)
        {
            var targetSchemaId = root.TryGetProperty("current-schema-id", out var curSchemaId) ? curSchemaId.GetInt32() : 0;
            foreach (var schemaElem in schemasProp.EnumerateArray())
            {
                var sId = schemaElem.TryGetProperty("schema-id", out var sid) ? sid.GetInt32() : 0;
                if (sId == targetSchemaId && schemaElem.TryGetProperty("fields", out var fieldsProp))
                {
                    foreach (var f in fieldsProp.EnumerateArray())
                    {
                        var fid = f.GetProperty("id").GetInt32();
                        var fname = f.GetProperty("name").GetString() ?? "";
                        var ftype = f.GetProperty("type").GetString() ?? "string";
                        var freq = f.TryGetProperty("required", out var r) && r.GetBoolean();
                        schemaFields.Add(new IcebergField(fid, fname, ftype, freq));
                    }
                    break;
                }
            }
        }

        // Parse Partition Spec
        var partitionFields = new List<IcebergPartitionField>();
        if (root.TryGetProperty("partition-specs", out var specsProp) && specsProp.ValueKind == JsonValueKind.Array)
        {
            var defaultSpecId = root.TryGetProperty("default-spec-id", out var defSpec) ? defSpec.GetInt32() : 0;
            foreach (var specElem in specsProp.EnumerateArray())
            {
                var spId = specElem.TryGetProperty("spec-id", out var sp) ? sp.GetInt32() : 0;
                if (spId == defaultSpecId && specElem.TryGetProperty("fields", out var pFields))
                {
                    foreach (var pf in pFields.EnumerateArray())
                    {
                        var sourceId = pf.GetProperty("source-id").GetInt32();
                        var fieldId = pf.GetProperty("field-id").GetInt32();
                        var name = pf.GetProperty("name").GetString() ?? "";
                        var transform = pf.GetProperty("transform").GetString() ?? "identity";
                        partitionFields.Add(new IcebergPartitionField(sourceId, fieldId, name, transform));
                    }
                    break;
                }
            }
        }

        // Parse Snapshots
        var snapshots = new List<IcebergSnapshot>();
        if (root.TryGetProperty("snapshots", out var snapsProp) && snapsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var snapElem in snapsProp.EnumerateArray())
            {
                var sId = snapElem.GetProperty("snapshot-id").GetInt64();
                var timestamp = snapElem.GetProperty("timestamp-ms").GetInt64();
                var manifestList = snapElem.GetProperty("manifest-list").GetString() ?? "";
                snapshots.Add(new IcebergSnapshot(sId, timestamp, manifestList));
            }
        }

        var schema = new IcebergSchema(0, schemaFields);
        var partitionSpec = new IcebergPartitionSpec(0, partitionFields);

        _logger.LogInformation("Loaded Iceberg table {TableUuid} (Format: v{FormatVersion}, CurrentSnapshotId: {SnapshotId}, Columns: {ColCount}).",
            tableUuid, formatVersion, currentSnapshotId, schemaFields.Count);

        return new IcebergTableMetadata(
            tableUuid,
            formatVersion,
            location,
            lastSeq,
            lastUpdated,
            currentSnapshotId,
            schema,
            partitionSpec,
            snapshots
        );
    }

    public async ValueTask<IReadOnlyList<IcebergDataFile>> LoadDataFilesAsync(
        IcebergTableMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var dataFiles = new List<IcebergDataFile>();

        // Find current snapshot
        IcebergSnapshot? currentSnap = null;
        foreach (var s in metadata.Snapshots)
        {
            if (s.SnapshotId == metadata.CurrentSnapshotId)
            {
                currentSnap = s;
                break;
            }
        }

        if (currentSnap == null || string.IsNullOrWhiteSpace(currentSnap.ManifestListLocation))
        {
            _logger.LogWarning("Iceberg table has no current snapshot or manifest list.");
            return dataFiles;
        }

        // Check if manifest list exists
        if (!await _storageProvider.ExistsAsync(currentSnap.ManifestListLocation, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Iceberg manifest list '{Location}' not found in storage.", currentSnap.ManifestListLocation);
            return dataFiles;
        }

        var manifestListJson = await _storageProvider.ReadTextAsync(currentSnap.ManifestListLocation, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(manifestListJson);
        var root = doc.RootElement;

        // The manifest list contains references to manifest files or direct data files
        if (root.TryGetProperty("entries", out var entriesProp) && entriesProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entriesProp.EnumerateArray())
            {
                var filePath = entry.GetProperty("file_path").GetString() ?? "";
                var fileFormat = entry.TryGetProperty("file_format", out var ff) ? ff.GetString() ?? "PARQUET" : "PARQUET";
                var recordCount = entry.TryGetProperty("record_count", out var rc) ? rc.GetInt64() : 1000L;
                var fileSizeBytes = entry.TryGetProperty("file_size_in_bytes", out var fs) ? fs.GetInt64() : 1024L;

                var partitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("partition", out var partProp) && partProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in partProp.EnumerateObject())
                    {
                        partitions[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                var lowerBounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("lower_bounds", out var lbProp) && lbProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in lbProp.EnumerateObject())
                    {
                        lowerBounds[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                var upperBounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("upper_bounds", out var ubProp) && ubProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in ubProp.EnumerateObject())
                    {
                        upperBounds[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                dataFiles.Add(new IcebergDataFile(
                    filePath,
                    fileFormat,
                    partitions,
                    recordCount,
                    fileSizeBytes,
                    lowerBounds,
                    upperBounds
                ));
            }
        }

        _logger.LogInformation("Loaded {Count} Iceberg data files from manifest list for snapshot {SnapshotId}.",
            dataFiles.Count, currentSnap.SnapshotId);

        return dataFiles;
    }
}
