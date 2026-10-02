namespace GqlGateway.Extensions.Backstage;

using GqlGateway.Application.Integrations.Backstage;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class BackstageServiceCollectionExtensions
{
    /// <summary>
    /// Backstage.io catalog export. Always registered; the endpoints in the core are only mapped when
    /// <c>Gateway:Backstage:Enabled</c> is true.
    /// </summary>
    public static IServiceCollection AddBackstageIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<BackstageIntegrationMarker>(services))
        {
            return services;
        }

        services.TryAddSingleton<IBackstageCatalogExportService, BackstageCatalogExportService>();
        return services;
    }

    private sealed class BackstageIntegrationMarker
    {
    }
}
