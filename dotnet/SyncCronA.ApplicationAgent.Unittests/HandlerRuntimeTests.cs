using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SyncCronA.ApplicationAgent;
using SyncCronA.ApplicationAgent.DependencyInjection;
using SyncCronA.ApplicationAgent.Runtime;
using SyncCronA.Contracts.ApplicationAgents;
using ExecutionContext = SyncCronA.ApplicationAgent.Runtime.ExecutionContext;

namespace SyncCronA.ApplicationAgent.Unittests;

[TestFixture]
public sealed class HandlerRuntimeTests
{
    [Test]
    public void Duplicate_handler_name_is_rejected()
    {
        var builder = new ServiceCollection().AddSyncCronA(new ConfigurationBuilder().Build());
        builder.AddHandler<FailingHandler>("same");
        Assert.That(() => builder.AddHandler<FailingHandler>("same"), Throws.ArgumentException);
    }

    [Test]
    public async Task Concurrent_executions_have_separate_scopes_and_duplicate_commands_do_not_restart()
    {
        var probe = new Probe();
        using var services = new ServiceCollection().AddSingleton(probe).AddScoped<ScopedMarker>().AddScoped<BlockingHandler>().BuildServiceProvider();
        var runtime = new HandlerRuntime([new("block", typeof(BlockingHandler))],
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HandlerRuntime>.Instance);
        var first = new ExecuteHandler { ExecutionId = Guid.NewGuid().ToString("D"), HandlerName = "block" };
        var second = new ExecuteHandler { ExecutionId = Guid.NewGuid().ToString("D"), HandlerName = "block" };
        runtime.Execute(first, Guid.NewGuid(), Guid.NewGuid());
        runtime.Execute(first, Guid.NewGuid(), Guid.NewGuid());
        runtime.Execute(second, Guid.NewGuid(), Guid.NewGuid());
        await probe.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(probe.Scopes.Distinct().Count(), Is.EqualTo(2));
        Assert.That(runtime.Pending().Count(x => x.State == HandlerExecutionState.Running), Is.EqualTo(2));
        probe.Release.TrySetResult();
        await WaitForAsync(() => runtime.Pending().Count(x => x.State == HandlerExecutionState.Success) == 2);
        runtime.Execute(first, Guid.NewGuid(), Guid.NewGuid());
        Assert.That(probe.Scopes.Count, Is.EqualTo(2));
        Assert.That(probe.Disposed, Is.EqualTo(2));
        AcknowledgeAll(runtime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await runtime.StopAsync(timeout.Token);
    }

    [Test]
    public async Task Failure_and_unknown_handler_are_reported_and_remain_until_acknowledged()
    {
        using var services = new ServiceCollection().AddScoped<FailingHandler>().BuildServiceProvider();
        var runtime = new HandlerRuntime([new("fail", typeof(FailingHandler))],
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HandlerRuntime>.Instance);
        runtime.Execute(new ExecuteHandler { ExecutionId = Guid.NewGuid().ToString("D"), HandlerName = "fail" }, Guid.NewGuid(), Guid.NewGuid());
        runtime.Execute(new ExecuteHandler { ExecutionId = Guid.NewGuid().ToString("D"), HandlerName = "missing" }, Guid.NewGuid(), Guid.NewGuid());
        await WaitForAsync(() => runtime.Pending().Count(x => x.State == HandlerExecutionState.Failed) == 2);
        Assert.That(runtime.Pending().Select(x => x.ErrorMessage), Does.Contain("expected failure"));
        Assert.That(runtime.Pending().Select(x => x.ErrorMessage), Does.Contain("Handler is not registered."));
        Assert.That(runtime.Pending().Length, Is.EqualTo(4));
        AcknowledgeAll(runtime);
        Assert.That(runtime.Pending(), Is.Empty);
    }

    [Test]
    public async Task Shutdown_cancels_handlers_and_waits_for_acknowledgements()
    {
        var probe = new CancellationProbe();
        using var services = new ServiceCollection().AddSingleton(probe).AddScoped<CancellationHandler>().BuildServiceProvider();
        var runtime = new HandlerRuntime([new("cancel", typeof(CancellationHandler))],
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HandlerRuntime>.Instance);
        runtime.Execute(new ExecuteHandler { ExecutionId = Guid.NewGuid().ToString("D"), HandlerName = "cancel" }, Guid.NewGuid(), Guid.NewGuid());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stop = runtime.StopAsync(timeout.Token);
        await WaitForAsync(() => runtime.Pending().Any(x => x.State == HandlerExecutionState.Cancelled));
        Assert.That(stop.IsCompleted, Is.False);
        AcknowledgeAll(runtime);
        await stop;
    }

    [Test]
    public async Task Cancel_stops_only_the_requested_execution_and_reports_cancellation()
    {
        var probe = new Probe();
        using var services = new ServiceCollection().AddSingleton(probe).AddScoped<ScopedMarker>().AddScoped<BlockingHandler>().BuildServiceProvider();
        var runtime = new HandlerRuntime([new("block", typeof(BlockingHandler))],
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HandlerRuntime>.Instance);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        runtime.Execute(new ExecuteHandler { ExecutionId = first.ToString("D"), HandlerName = "block" }, Guid.NewGuid(), Guid.NewGuid());
        runtime.Execute(new ExecuteHandler { ExecutionId = second.ToString("D"), HandlerName = "block" }, Guid.NewGuid(), Guid.NewGuid());
        await probe.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runtime.Cancel(new CancelHandler { ExecutionId = first.ToString("D") });
        await WaitForAsync(() => runtime.Pending().Any(x => x.ExecutionId == first.ToString("D") && x.State == HandlerExecutionState.Cancelled));
        Assert.That(runtime.Pending().Any(x => x.ExecutionId == second.ToString("D") && x.State == HandlerExecutionState.Cancelled), Is.False);

        probe.Release.TrySetResult();
        await WaitForAsync(() => runtime.Pending().Any(x => x.ExecutionId == second.ToString("D") && x.State == HandlerExecutionState.Success));
        AcknowledgeAll(runtime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await runtime.StopAsync(timeout.Token);
    }

    private static void AcknowledgeAll(HandlerRuntime runtime)
    {
        foreach (var report in runtime.Pending()) runtime.Acknowledge(new ExecutionReportAcknowledged
        { ExecutionId = report.ExecutionId, Sequence = report.Sequence });
    }
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Probe
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<Guid> Scopes = [];
        public readonly TaskCompletionSource BothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed;
    }
    private sealed class ScopedMarker(Probe probe) : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public void Dispose() => Interlocked.Increment(ref probe.Disposed);
    }
    private sealed class BlockingHandler(Probe probe, ScopedMarker marker) : ISyncCronAHandler
    {
        public async Task ExecuteAsync(ExecutionContext context, CancellationToken cancellationToken)
        {
            probe.Scopes.Add(marker.Id);
            if (probe.Scopes.Count == 2) probe.BothStarted.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class FailingHandler : ISyncCronAHandler
    {
        public Task ExecuteAsync(ExecutionContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("expected failure");
    }
    private sealed class CancellationProbe
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class CancellationHandler(CancellationProbe probe) : ISyncCronAHandler
    {
        public Task ExecuteAsync(ExecutionContext context, CancellationToken cancellationToken)
        {
            probe.Started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
