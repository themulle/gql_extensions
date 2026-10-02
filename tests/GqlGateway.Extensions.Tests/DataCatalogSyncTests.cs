namespace GqlGateway.Extensions.Tests;

using System;
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
using GqlGateway.Extensions.DataCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class DataCatalogSyncTests
{
    private readonly IDataCatalogClient _catalogClient = Substitute.For<IDataCatalogClient>();
    private readonly ITableMetadataRepository _tableRepo = Substitute.For<ITableMetadataRepository>();
    private readonly GatewayOptions _gatewayOptions;
    private readonly DataCatalogSyncService _sut;

    public DataCatalogSyncTests()
    {
        _catalogClient.ProviderType.Returns(DataCatalogProviderType.MicrosoftPurview);
        _gatewayOptions = new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Provider = DataCatalogProviderType.MicrosoftPurview
            }
        };

        _sut = new DataCatalogSyncService(
            new[] { _catalogClient },
            _tableRepo,
            Options.Create(_gatewayOptions),
            NullLogger<DataCatalogSyncService>.Instance);
    }

    [Fact]
    public async Task SyncCatalogAsync_WhenGdprArticle9TagPresent_EnforcesHighSensitivityAndRedactMasking()
    {
        var tableId = new TableIdentifier("healthcare", "dbo", "patient_health_records");
        var catalogTable = new CatalogTableAsset
        {
            Identifier = tableId,
            DisplayName = "Patient Health Records",
            Description = "Sensitive clinical data",
            Tags = ["gdpr_art9", "clinical"],
            Columns =
            [
                new()
                {
                    ColumnName = "genetic_markers",
                    DataType = "varchar",
                    Tags = ["biometric", "gdpr_article_9"],
                    Classifications = ["Art9SpecialCategory"]
                },
                new()
                {
                    ColumnName = "patient_email",
                    DataType = "varchar",
                    Tags = ["PII.Email"],
                    Classifications = ["PII"]
                }
            ]
        };

        _catalogClient.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CatalogTableAsset> { catalogTable });

        var result = await _sut.SyncCatalogAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        result.SyncedTablesCount.ShouldBe(1);
        result.Art9ProtectedTablesCount.ShouldBe(1);
        result.MaskedColumnsCount.ShouldBe(2);

        // Verify repository upsert with GDPR Art. 9 rules
        await _tableRepo.Received(1).UpsertTableMetadataAsync(
            Arg.Is<TableMetadata>(m =>
                m.Identifier.Equals(tableId) &&
                m.Table.Sensitivity == "HIGH" &&
                m.Table.RequiresFourEyes == true &&
                m.ColumnMaskingRules.ContainsKey("genetic_markers") &&
                m.ColumnMaskingRules["genetic_markers"].RuleType == "REDACT" &&
                m.ColumnMaskingRules.ContainsKey("patient_email") &&
                m.ColumnMaskingRules["patient_email"].RuleType == "MASK_EMAIL"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCatalogAsync_DryRunMode_DoesNotPersistToRepository()
    {
        var tableId = new TableIdentifier("sales", "dbo", "customers");
        var catalogTable = new CatalogTableAsset
        {
            Identifier = tableId,
            Tags = ["crm"],
            Columns =
            [
                new() { ColumnName = "phone_number", Tags = ["phone"] }
            ]
        };

        _catalogClient.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CatalogTableAsset> { catalogTable });

        var result = await _sut.SyncCatalogAsync(dryRun: true);

        result.Success.ShouldBeTrue();
        result.SyncedTablesCount.ShouldBe(1);

        // Dry-run must never call UpsertTableMetadataAsync
        await _tableRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }
}
