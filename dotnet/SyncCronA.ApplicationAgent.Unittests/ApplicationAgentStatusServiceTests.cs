using SyncCronA.ApplicationAgent;
using SyncCronA.ApplicationAgent.Agent;

namespace SyncCronA.ApplicationAgent.Unittests;

[TestFixture]
public class ApplicationAgentStatusServiceTests
{
    [Test]
    public void Get_BeforeFirstUpdate_ReturnsStartingSnapshot()
    {
        var before = DateTimeOffset.UtcNow;
        var status = new ApplicationAgentStatusService();

        var snapshot = status.Get();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State, Is.EqualTo("Starting"));
            Assert.That(snapshot.InstanceId, Is.EqualTo(Guid.Empty));
            Assert.That(snapshot.AgentId, Is.Null);
            Assert.That(snapshot.SessionId, Is.Null);
            Assert.That(snapshot.Detail, Is.Null);
            Assert.That(snapshot.UpdatedAt, Is.InRange(before, DateTimeOffset.UtcNow));
        });
    }

    [Test]
    public void Update_ReplacesAllFieldsAndRefreshesTimestamp()
    {
        var status = new ApplicationAgentStatusService();
        var previous = status.Get();
        var instanceId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        status.Update("Connected", instanceId, "agent", "session", "detail");

        var current = status.Get();
        Assert.Multiple(() =>
        {
            Assert.That(current.State, Is.EqualTo("Connected"));
            Assert.That(current.InstanceId, Is.EqualTo(instanceId));
            Assert.That(current.AgentId, Is.EqualTo("agent"));
            Assert.That(current.SessionId, Is.EqualTo("session"));
            Assert.That(current.Detail, Is.EqualTo("detail"));
            Assert.That(current.UpdatedAt, Is.InRange(before, DateTimeOffset.UtcNow));
            Assert.That(current, Is.Not.SameAs(previous));
            Assert.That(previous.State, Is.EqualTo("Starting"));
        });
    }

    [Test]
    public void Update_WithOptionalFieldsOmitted_ClearsPreviousValues()
    {
        var status = new ApplicationAgentStatusService();
        status.Update("Connected", Guid.NewGuid(), "agent", "session", "detail");

        status.Update("Retrying", Guid.Empty);

        var snapshot = status.Get();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State, Is.EqualTo("Retrying"));
            Assert.That(snapshot.AgentId, Is.Null);
            Assert.That(snapshot.SessionId, Is.Null);
            Assert.That(snapshot.Detail, Is.Null);
        });
    }

    [Test]
    public void Get_DuringConcurrentUpdates_AlwaysReturnsACompleteSnapshot()
    {
        var status = new ApplicationAgentStatusService();
        var ids = Enumerable.Range(1, 100).Select(_ => Guid.NewGuid()).ToArray();

        Parallel.For(0, ids.Length, i => status.Update(i.ToString(), ids[i], i.ToString()));

        var snapshot = status.Get();
        var index = int.Parse(snapshot.State);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.InstanceId, Is.EqualTo(ids[index]));
            Assert.That(snapshot.AgentId, Is.EqualTo(snapshot.State));
        });
    }
}