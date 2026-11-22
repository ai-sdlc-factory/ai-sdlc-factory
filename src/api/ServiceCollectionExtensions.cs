using Temporalio.Client;

namespace AiSdlcFactory.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiSettings(this IServiceCollection services) =>
        services.AddSingleton(sp => ApiSettings.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

    /// <summary>
    /// Lazy connection: the API starts even if Temporal is briefly down, and readiness reports it.
    /// </summary>
    public static IServiceCollection AddTemporal(this IServiceCollection services)
    {
        services.AddSingleton<ITemporalClient>(sp =>
        {
            var settings = sp.GetRequiredService<ApiSettings>();
            return TemporalClient.CreateLazy(
                new TemporalClientConnectOptions(settings.TemporalAddress)
                {
                    Namespace = settings.TemporalNamespace,
                });
        });
        return services.AddSingleton<ITemporalGateway, TemporalGateway>();
    }
}
