using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SyncCronA.ApplicationAgent.Agent;
using SyncCronA.ApplicationAgent.Runtime;

namespace SyncCronA.ApplicationAgent.DependencyInjection;

public static class SyncCronAServiceCollectionExtensions
{
    public static SyncCronABuilder AddSyncCronA(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApplicationAgentOptions>()
            .Bind(configuration.GetSection(ApplicationAgentOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ApplicationAgentStatusService>();
        services.AddSingleton<HandlerRuntime>();
        services.AddHostedService<ApplicationAgentWorker>();
        return new SyncCronABuilder(services);
    }
}
