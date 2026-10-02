namespace GqlGateway.Extensions.DataCatalog;

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Bounded response reading for all data catalog clients: rejects responses above <see cref="MaxResponseBytes"/>,
/// both via the declared Content-Length and while streaming (chunked responses without Content-Length).
/// </summary>
internal static class CatalogHttpContent
{
    internal const long MaxResponseBytes = 10 * 1024 * 1024; // 10 MB maximum payload cap

    internal static async Task<string> ReadBoundedStringAsync(HttpResponseMessage response, string providerName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > MaxResponseBytes)
        {
            throw new InvalidOperationException(
                $"{providerName} response size ({declaredLength.Value} bytes) exceeds maximum allowed limit of {MaxResponseBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidOperationException(
                    $"{providerName} response exceeds maximum allowed limit of {MaxResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
