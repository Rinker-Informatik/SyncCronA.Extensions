using ExecutionContext = SyncCronA.ApplicationAgent.Runtime.ExecutionContext;

namespace SyncCronA.ApplicationAgent.DependencyInjection;

public interface ISyncCronAHandler
{
    Task ExecuteAsync(ExecutionContext context, CancellationToken cancellationToken);
}
