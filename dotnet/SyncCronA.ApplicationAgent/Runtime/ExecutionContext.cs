namespace SyncCronA.ApplicationAgent.Runtime;

public sealed record ExecutionContext(Guid ExecutionId, Guid ApplicationId, Guid InstanceId);
