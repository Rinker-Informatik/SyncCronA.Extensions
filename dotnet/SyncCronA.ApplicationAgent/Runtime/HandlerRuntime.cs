using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncCronA.ApplicationAgent.DependencyInjection;
using SyncCronA.Contracts.ApplicationAgents;

namespace SyncCronA.ApplicationAgent.Runtime;

// State deliberately survives gRPC sessions, but not an application process restart.
internal sealed class HandlerRuntime(
    IEnumerable<HandlerDescriptor> descriptors,
    IServiceScopeFactory scopes,
    ILogger<HandlerRuntime> logger)
{
    private readonly Dictionary<string, System.Type> handlers = descriptors.ToDictionary(x => x.Name, x => x.Type, StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Task> executions = [];
    private readonly Dictionary<Guid, CancellationTokenSource> cancellations = [];
    private readonly Dictionary<(Guid Id, uint Sequence), ExecutionReport> pending = [];
    private readonly CancellationTokenSource shutdown = new();
    private bool stopping;

    public IEnumerable<string> Names => handlers.Keys;

    public void Execute(ExecuteHandler request, Guid applicationId, Guid instanceId)
    {
        if (!Guid.TryParse(request.ExecutionId, out var id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid execution ID.");
        lock (gate)
        {
            if (stopping || executions.ContainsKey(id)) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            cancellations.Add(id, cancellation);
            // Schedule even synchronous handlers away from the stream reader.
            executions.Add(id, Task.Run(() => RunAsync(id, request.HandlerName, applicationId, instanceId, cancellation.Token)));
        }
    }

    public void Cancel(CancelHandler request)
    {
        if (!Guid.TryParse(request.ExecutionId, out var id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid execution ID.");
        lock (gate)
        {
            if (cancellations.TryGetValue(id, out var cancellation)) cancellation.Cancel();
        }
    }

    private async Task RunAsync(Guid id, string name, Guid applicationId, Guid instanceId, CancellationToken cancellationToken)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["ExecutionId"] = id,
            ["Handler"] = name,
            ["InstanceId"] = instanceId
        });
        try
        {
            if (!handlers.TryGetValue(name, out var type))
            {
                Report(id, 1, HandlerExecutionState.Failed, "Handler is not registered.");
                return;
            }
            Report(id, 1, HandlerExecutionState.Started);
            cancellationToken.ThrowIfCancellationRequested();
            await using (var scope = scopes.CreateAsyncScope())
            {
                var handler = (ISyncCronAHandler)scope.ServiceProvider.GetRequiredService(type);
                Report(id, 2, HandlerExecutionState.Running);
                logger.LogInformation("Executing handler {Handler}", name);
                await handler.ExecuteAsync(new ExecutionContext(id, applicationId, instanceId), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Report(id, 3, HandlerExecutionState.Success);
            logger.LogInformation("Handler completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report(id, 3, HandlerExecutionState.Cancelled);
            logger.LogInformation("Handler cancelled");
        }
        catch (Exception exception)
        {
            Report(id, 3, HandlerExecutionState.Failed,
                string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message);
            logger.LogWarning(exception, "Handler failed");
        }
        finally
        {
            lock (gate)
            {
                var cancellation = cancellations[id];
                cancellations.Remove(id);
                cancellation.Dispose();
            }
        }
    }

    private void Report(Guid id, uint sequence, HandlerExecutionState state, string error = "")
    {
        lock (gate) pending.Add((id, sequence), new ExecutionReport
        {
            ExecutionId = id.ToString("D"),
            Sequence = sequence,
            State = state,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ErrorMessage = error.Length <= 8000 ? error : error[..8000]
        });
    }

    public ExecutionReport[] Pending()
    {
        lock (gate) return pending.Values.OrderBy(x => x.ExecutionId).ThenBy(x => x.Sequence).Select(x => x.Clone()).ToArray();
    }

    public void Acknowledge(ExecutionReportAcknowledged acknowledgement)
    {
        if (!Guid.TryParse(acknowledgement.ExecutionId, out var id)) return;
        lock (gate) pending.Remove((id, acknowledgement.Sequence));
    }

    public async Task StopAsync(CancellationToken token)
    {
        Task[] running;
        lock (gate) { stopping = true; running = executions.Values.ToArray(); }
        await shutdown.CancelAsync();
        await Task.WhenAll(running).WaitAsync(token);
        while (Pending().Length > 0) await Task.Delay(20, token);
    }
}
