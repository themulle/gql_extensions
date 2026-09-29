namespace GqlGateway.Extensions.Lakehouse.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Options;
using GqlGateway.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Options;

/// <summary>
/// Storage provider for local filesystem or mounted persistent volumes (Kubernetes PVC, Docker bind-mounts).
/// </summary>
public sealed class LocalStorageProvider : ILakehouseStorageProvider
{
    private readonly string _basePath;

    public LocalStorageProvider(IOptions<GatewayOptions>? options = null)
    {
        var localPath = options?.Value.Lakehouse.Storage.LocalBasePath;
        _basePath = !string.IsNullOrWhiteSpace(localPath) ? localPath : AppContext.BaseDirectory;
    }

    public async ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var resolvedPath = ResolvePath(location);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Lakehouse file not found at '{resolvedPath}'.", resolvedPath);
        }

        return await File.ReadAllTextAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Stream> OpenReadStreamAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var resolvedPath = ResolvePath(location);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Lakehouse file not found at '{resolvedPath}'.", resolvedPath);
        }

        Stream stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return ValueTask.FromResult(stream);
    }

    public ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location)) return ValueTask.FromResult(false);
        var resolvedPath = ResolvePath(location);
        return ValueTask.FromResult(File.Exists(resolvedPath));
    }

    private string ResolvePath(string location)
    {
        if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            location = location[7..];
        }

        var fullBasePath = Path.GetFullPath(_basePath);
        var normalizedBase = fullBasePath.EndsWith(Path.DirectorySeparatorChar)
            ? fullBasePath
            : fullBasePath + Path.DirectorySeparatorChar;

        var combined = Path.IsPathRooted(location)
            ? Path.GetFullPath(location)
            : Path.GetFullPath(Path.Combine(fullBasePath, location));

        if (!combined.StartsWith(normalizedBase, StringComparison.Ordinal) &&
            !string.Equals(combined, fullBasePath, StringComparison.Ordinal))
        {
            throw new System.Security.SecurityException($"Access to path outside configured lakehouse base directory is denied: '{location}'.");
        }

        return combined;
    }
}
