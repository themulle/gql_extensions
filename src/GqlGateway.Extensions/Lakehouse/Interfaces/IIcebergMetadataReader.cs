namespace GqlGateway.Extensions.Lakehouse.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

/// <summary>
/// Parser and loader for Apache Iceberg v2 table metadata, manifest lists, and data manifests.
/// </summary>
public interface IIcebergMetadataReader
{
    /// <summary>
    /// Loads and parses the root table metadata (e.g. v2.metadata.json).
    /// </summary>
    ValueTask<IcebergTableMetadata> LoadTableMetadataAsync(
        string metadataLocation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Traverses the current snapshot's manifest list and returns all active data files.
    /// </summary>
    ValueTask<IReadOnlyList<IcebergDataFile>> LoadDataFilesAsync(
        IcebergTableMetadata metadata,
        CancellationToken cancellationToken = default);
}
