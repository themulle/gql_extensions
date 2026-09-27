namespace GqlGateway.Extensions.Dbt;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Dbt.Interfaces;
using GqlGateway.Application.Interfaces;
using Microsoft.Extensions.Logging;

public sealed class DbtExposurePublisher(
    ITableMetadataRepository metadataRepository,
    ILogger<DbtExposurePublisher> logger) : IDbtExposurePublisher
{
    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
    private readonly ILogger<DbtExposurePublisher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<string> GenerateExposuresYamlAsync(CancellationToken ct = default)
    {
        var tables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder();

        sb.AppendLine("version: 2");
        sb.AppendLine();
        sb.AppendLine("exposures:");

        foreach (var table in tables)
        {
            var expName = $"gql_gateway_{table.Identifier.Schema}_{table.Identifier.TableName}".ToLowerInvariant();
            sb.AppendLine($"  - name: {expName}");
            sb.AppendLine("    type: application");
            sb.AppendLine("    maturity: high");
            sb.AppendLine("    url: https://gateway.corp.local/graphql");
            sb.AppendLine($"    description: \"Managed table '{table.Identifier}' governed by Enterprise GraphQL Gateway.\"");
            sb.AppendLine("    depends_on:");
            sb.AppendLine($"      - ref('{table.Identifier.TableName}')");
            sb.AppendLine("    owner:");
            sb.AppendLine("      name: \"Gateway Governance Team\"");
            sb.AppendLine("      email: \"governance-team@corp.local\"");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task ExportExposuresFileAsync(string outputFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        var yaml = await GenerateExposuresYamlAsync(ct).ConfigureAwait(false);
        var dir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outputFilePath, yaml, Encoding.UTF8, ct).ConfigureAwait(false);
        _logger.LogInformation("Exported dbt exposures YAML to {Path}", outputFilePath);
    }
}
