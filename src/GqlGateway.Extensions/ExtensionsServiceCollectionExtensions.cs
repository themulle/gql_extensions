namespace GqlGateway.Extensions;

using System;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.Dbt.Interfaces;
using GqlGateway.Domain.Options;
using GqlGateway.Extensions.Itsm;
using GqlGateway.Extensions.OpenMetadata;
using GqlGateway.Extensions.DataCatalog;
using GqlGateway.Extensions.Dbt;
using GqlGateway.Extensions.OData;
using GqlGateway.Extensions.Lakehouse.Interfaces;
using GqlGateway.Extensions.Lakehouse.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public static class ExtensionsServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayExtensions(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        // 1. ITSM Workflow Outbound Clients (ServiceNow & Jira)
        services.AddHttpClient<ServiceNowClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Itsm;
            if (!string.IsNullOrWhiteSpace(opts.ServiceNowBaseUrl))
            {
                client.BaseAddress = new Uri(opts.ServiceNowBaseUrl);
            }
        });

        services.AddHttpClient<JiraClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Itsm;
            if (!string.IsNullOrWhiteSpace(opts.JiraBaseUrl))
            {
                client.BaseAddress = new Uri(opts.JiraBaseUrl);
            }
        });

        services.AddScoped<IItsmWorkflowClient>(sp => sp.GetRequiredService<ServiceNowClient>());
        services.AddScoped<IItsmWorkflowClient>(sp => sp.GetRequiredService<JiraClient>());

        // 2. OpenMetadata Governance & Catalog Integration
        services.AddHttpClient<IOpenMetadataClient, OpenMetadataClient>();
        services.AddScoped<IOpenMetadataSyncService, OpenMetadataSyncService>();

        if (gatewayOptions.OpenMetadata.Enabled)
        {
            services.AddHostedService<OpenMetadataSyncBackgroundService>();
        }

        // 3. dbt (data build tool) Integration (F-DATA-11)
        services.AddScoped<IDbtMetadataIngestionService, DbtMetadataIngestionService>();
        services.AddScoped<IDbtExposurePublisher, DbtExposurePublisher>();
        services.AddScoped<IDbtContractValidator, DbtContractValidator>();

        // 4. OData v4 / Power BI & Excel Direct Adapter
        services.AddScoped<IODataHandler, ODataHandler>();

        // 5. Multi-Catalog Governance Integration (Purview, Collibra, Alation, OpenMetadata)
        services.AddHttpClient<MicrosoftPurviewCatalogClient>();
        services.AddHttpClient<CollibraCatalogClient>();
        services.AddHttpClient<AlationCatalogClient>();

        services.AddScoped<IDataCatalogClient, OpenMetadataCatalogAdapter>();
        services.AddScoped<IDataCatalogClient>(sp => sp.GetRequiredService<MicrosoftPurviewCatalogClient>());
        services.AddScoped<IDataCatalogClient>(sp => sp.GetRequiredService<CollibraCatalogClient>());
        services.AddScoped<IDataCatalogClient>(sp => sp.GetRequiredService<AlationCatalogClient>());

        services.AddScoped<IDataCatalogSyncService, DataCatalogSyncService>();
        services.AddScoped<IDataCatalogWebhookHandler, CatalogWebhookHandler>();

        if (gatewayOptions.Catalog.Enabled)
        {
            services.AddHostedService<DataCatalogSyncBackgroundService>();
        }

        // 6. Apache Iceberg Lakehouse Connector (P4 / ADR-015) is registered natively in GatewayServiceCollectionExtensions
        return services;
    }
}
