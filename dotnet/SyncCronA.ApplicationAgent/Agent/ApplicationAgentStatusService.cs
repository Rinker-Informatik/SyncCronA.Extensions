namespace SyncCronA.ApplicationAgent.Agent;

/// <summary>Thread-safe runtime status published by the sample application agent.</summary>
public sealed class ApplicationAgentStatusService
{
    private readonly Lock sync = new();
    private Snapshot snapshot = new("Starting", Guid.Empty, null, null, null, DateTimeOffset.UtcNow);

    public Snapshot Get()
    {
        lock (sync)
            return snapshot;
    }

    public void Update(string state, Guid instanceId, string? agentId = null, string? sessionId = null,
        string? detail = null)
    {
        lock (sync)
            snapshot = new Snapshot(state, instanceId, agentId, sessionId, detail, DateTimeOffset.UtcNow);
    }

    public sealed record Snapshot(
        string State,
        Guid InstanceId,
        string? AgentId,
        string? SessionId,
        string? Detail,
        DateTimeOffset UpdatedAt);
}
