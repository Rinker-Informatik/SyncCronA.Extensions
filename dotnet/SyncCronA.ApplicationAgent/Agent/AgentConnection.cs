using Grpc.Core;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using SyncCronA.ApplicationAgent.Runtime;
using SyncCronA.Contracts.ApplicationAgents;

namespace SyncCronA.ApplicationAgent.Agent;

internal static class AgentConnection
{
    internal static async Task RunAsync(
        ApplicationAgentOptions options,
        ApplicationAgentStatusService statusService,
        HandlerRuntime runtime,
        AgentEnrollmentClient enrollment,
        ILogger logger,
        Guid agentId,
        Guid instanceId,
        X509Certificate2 certificate,

        CancellationToken token)
    {
        using var handler = enrollment.CreateControlPlaneHandler();
        handler.ClientCertificates.Add(certificate);
        using var channel =
            GrpcChannel.ForAddress(options.ControlPlaneUrl, new GrpcChannelOptions { HttpHandler = handler });
        // Keep the call alive long enough to half-close its send stream on host shutdown.
        using var session = new CancellationTokenSource();
        using var call =
            new ApplicationAgentControlPlane.ApplicationAgentControlPlaneClient(channel).Connect(
                cancellationToken: session.Token);
        var registration = new RegisterApplicationAgent
        {
            ProtocolVersion = 2,
            AgentKey = agentId.ToString("D"),
            Name = options.ApplicationId.ToString("D"),
            ApplicationName = options.ApplicationName ?? string.Empty,
            ApplicationInstanceId = instanceId.ToString("D"),
            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            Environment = options.Environment
        };
        registration.Handlers.AddRange(runtime.Names.Select(name => new HandlerRegistration { Name = name }));
        await call.RequestStream.WriteAsync(new ApplicationAgentMessage { Registration = registration }, token);
        var registered = await ReadRegistrationAsync(call.ResponseStream, token);
        if (registered is null) return; // Core may stop or replace the connection during registration.
        statusService.Update("Connected", instanceId, registered.AgentId, registered.SessionId);
        logger.LogInformation("Application agent {AgentId} connected with session {SessionId}", registered.AgentId,
            registered.SessionId);

        async Task SendAsync()
        {
            var lastHeartbeat = DateTimeOffset.MinValue;
            var sent = new HashSet<(string, uint)>();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            do
            {
                if (DateTimeOffset.UtcNow - lastHeartbeat >= options.HeartbeatInterval)
                {
                    await call.RequestStream.WriteAsync(new ApplicationAgentMessage
                    {
                        Heartbeat = new ApplicationAgentHeartbeat
                        {
                            ProtocolVersion = 2,
                            AgentId = agentId.ToString("D"),
                            SessionId = registered.SessionId,
                            SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                        }
                    }, session.Token);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }

                var pending = runtime.Pending();
                sent.IntersectWith(pending.Select(x => (x.ExecutionId, x.Sequence)));
                foreach (var report in pending)
                {
                    if (!sent.Add((report.ExecutionId, report.Sequence))) continue;
                    report.SessionId = registered.SessionId;
                    await call.RequestStream.WriteAsync(new ApplicationAgentMessage { Report = report }, session.Token);
                }

                try
                {
                    if (!await timer.WaitForNextTickAsync(token)) return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
            } while (!token.IsCancellationRequested);
        }

        async Task ReceiveAsync()
        {
            while (await call.ResponseStream.MoveNext(session.Token))
            {
                var message = call.ResponseStream.Current;
                if (message.Execute is { } execute) runtime.Execute(execute, options.ApplicationId, instanceId);
                else if (message.Cancel is { } cancel) runtime.Cancel(cancel);
                else if (message.Acknowledged is { } acknowledgement) runtime.Acknowledge(acknowledgement);
                else throw new InvalidDataException("Unsupported control-plane message.");
            }
            // A clean end of the server stream is a normal reconnect condition.
        }

        var sender = SendAsync();
        var receiver = ReceiveAsync();
        try
        {
            await await Task.WhenAny(sender, receiver);
            if (token.IsCancellationRequested)
            {
                await call.RequestStream.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await receiver.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (TimeoutException) when (token.IsCancellationRequested)
        {
            logger.LogDebug("Control plane did not finish the stream before the shutdown grace period elapsed");
        }
        finally
        {
            await session.CancelAsync();
            try
            {
                await Task.WhenAll(sender, receiver);
            }
            catch
            {
                /* The first failure is propagated above. */
            }
        }
    }

    internal static async Task<ApplicationAgentRegistered?> ReadRegistrationAsync(
        IAsyncStreamReader<ApplicationControlPlaneMessage> responses, CancellationToken token)
    {
        if (!await responses.MoveNext(token)) return null;
        var message = responses.Current;
        if (message.Registered is not { } registered)
            throw new InvalidDataException($"Expected a registration confirmation, received {message.PayloadCase}.");
        if (registered.ProtocolVersion != 2)
            throw new InvalidDataException(
                $"Core confirmed protocol v{registered.ProtocolVersion}; this agent requires v2.");
        return registered;
    }
}
