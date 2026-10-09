using Microsoft.Extensions.DependencyInjection;
using SyncCronA.ApplicationAgent.Runtime;

namespace SyncCronA.ApplicationAgent.DependencyInjection;

public sealed class SyncCronABuilder
{
    internal SyncCronABuilder(IServiceCollection services) => Services = services;
    internal IServiceCollection Services { get; }

    public SyncCronABuilder AddHandler<THandler>(string name) where THandler : class, ISyncCronAHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200) throw new ArgumentException("Handler names must not exceed 200 characters.", nameof(name));
        if (Services.Any(x => x.ImplementationInstance is HandlerDescriptor handler && handler.Name == name))
            throw new ArgumentException($"Handler '{name}' is already registered.", nameof(name));
        Services.AddSingleton(new HandlerDescriptor(name, typeof(THandler)));
        Services.AddScoped<THandler>();

        return this;
    }
}
