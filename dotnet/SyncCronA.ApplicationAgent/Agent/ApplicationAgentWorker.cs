using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncCronA.ApplicationAgent.Runtime;

namespace SyncCronA.ApplicationAgent.Agent;

public sealed class ApplicationAgentWorker : BackgroundService
{
    private readonly ApplicationAgentOptions options;
    private readonly ApplicationAgentStatusService statusService;
    private readonly ILogger<ApplicationAgentWorker> logger;
    private readonly HandlerRuntime runtime;
    private readonly ServiceProvider? fallbackServices;

    public ApplicationAgentWorker(IOptions<ApplicationAgentOptions> configured, ILogger<ApplicationAgentWorker> logger)
        : this(configured, new ApplicationAgentStatusService(), logger) { }

    public ApplicationAgentWorker(IOptions<ApplicationAgentOptions> configured, ApplicationAgentStatusService statusService,
        ILogger<ApplicationAgentWorker> logger)
    {
        options = configured.Value;
        this.statusService = statusService;
        this.logger = logger;
        fallbackServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        runtime = new HandlerRuntime([], fallbackServices.GetRequiredService<IServiceScopeFactory>(),
            fallbackServices.GetRequiredService<ILogger<HandlerRuntime>>());
    }

    [ActivatorUtilitiesConstructor]
    public ApplicationAgentWorker(IOptions<ApplicationAgentOptions> configured, ApplicationAgentStatusService statusService,
        ILogger<ApplicationAgentWorker> logger, IServiceProvider services)
    {
        options = configured.Value;
        this.statusService = statusService;
        this.logger = logger;
        runtime = services.GetRequiredService<HandlerRuntime>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var instanceId = Guid.NewGuid();
        var enrollment = new AgentEnrollmentClient(options, statusService, logger);
        var outageReported = false;
        void Disconnected(string reason)
        {
            if (statusService.Get().State == "Connected") outageReported = false;
            statusService.Update("Retrying", instanceId, detail: reason);
            if (!outageReported)
                logger.LogInformation("Control plane is unavailable ({Reason}). Reconnecting every {RetryInterval}.", reason, options.RetryInterval);
            outageReported = true;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                statusService.Update("Enrolling", instanceId);
                var enrolled = await enrollment.EnrollAsync(instanceId, stoppingToken);
                if (enrolled is { } identity)
                {
                    using var certificate = identity.Certificate;
                    await AgentConnection.RunAsync(
                        options,
                        statusService,
                        runtime,
                        enrollment,
                        logger,
                        identity.AgentId,
                        instanceId,
                        certificate,
                        stoppingToken);
                    if (!stoppingToken.IsCancellationRequested) Disconnected("Connection closed");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (RpcException exception) when (exception.StatusCode is StatusCode.Cancelled or StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            {
                if (stoppingToken.IsCancellationRequested) break;
                Disconnected(exception.StatusCode.ToString());
            }
            catch (HttpRequestException exception) when (
                exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                || exception.StatusCode is System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout)
            {
                if (stoppingToken.IsCancellationRequested) break;
                Disconnected(exception.Message);
            }
            catch (Exception exception)
            {
                statusService.Update("Retrying", instanceId, detail: exception.Message);
                logger.LogWarning(exception, "Application agent connection failed. Retrying in {RetryInterval}.", options.RetryInterval);
            }
            try { await Task.Delay(options.RetryInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Keep the connection alive while handlers cooperate with cancellation and reports drain.
        try { await runtime.StopAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await base.StopAsync(cancellationToken); }
    }

    public override void Dispose()
    {
        base.Dispose();
        fallbackServices?.Dispose();
    }
}
