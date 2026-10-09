using Serilog;
using SyncCronA.ApplicationAgent.Agent;
using SyncCronA.ApplicationAgent.DependencyInjection;
using ExecutionContext = SyncCronA.ApplicationAgent.Runtime.ExecutionContext;

var app = TestWebApiApplication.Build(args);

app.Run();

public static class TestWebApiApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "SyncCronA.TestWebApi")
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {NewLine}{Exception}"),
            preserveStaticLogger: true);

        builder.Services.AddSyncCronA(builder.Configuration)
            .AddHandler<CleanupHandler>("cleanup")
            .AddHandler<FailingHandler>("failure")
            .AddHandler<SlowHandler>("slow");

        var app = builder.Build();
        app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());

        app.MapGet("/", () => Results.Ok(new
        {
            name = "SyncCronA Application Agent Test Web API",
            endpoints = new[] { "/health", "/status", "/api/test/ping" }
        }));
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/status", (ApplicationAgentStatusService agentStatusService) => Results.Ok(agentStatusService.Get()));
        app.MapGet("/api/test/ping", (ApplicationAgentStatusService agentStatusService) => Results.Ok(new
        {
            message = "Test Web API is running.",
            applicationAgent = agentStatusService.Get()
        }));

        return app;
    }
}

public sealed class CleanupHandler(ILogger<CleanupHandler> logger) : ISyncCronAHandler
{
    public Task ExecuteAsync(ExecutionContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("Cleanup completed for {ExecutionId}", context.ExecutionId);
        return Task.CompletedTask;
    }
}
public sealed class FailingHandler : ISyncCronAHandler
{
    public Task ExecuteAsync(ExecutionContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Example handler failure.");
}
public sealed class SlowHandler : ISyncCronAHandler
{
    public Task ExecuteAsync(ExecutionContext context, CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
}
