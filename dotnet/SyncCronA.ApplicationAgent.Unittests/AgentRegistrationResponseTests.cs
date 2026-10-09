using Grpc.Core;
using SyncCronA.ApplicationAgent.Agent;
using SyncCronA.Contracts.ApplicationAgents;

namespace SyncCronA.ApplicationAgent.Unittests;

[TestFixture]
public sealed class AgentRegistrationResponseTests
{
    [Test]
    public async Task Stream_closed_before_confirmation_is_a_normal_disconnect()
    {
        var result = await AgentConnection.ReadRegistrationAsync(new Responses(null), default);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Valid_confirmation_is_accepted()
    {
        var registration = new ApplicationAgentRegistered { ProtocolVersion = 2, AgentId = "agent", SessionId = "connection" };
        var result = await AgentConnection.ReadRegistrationAsync(new Responses(new() { Registered = registration }), default);
        Assert.That(result, Is.SameAs(registration));
    }

    [Test]
    public void Actual_protocol_mismatch_reports_received_version()
    {
        var error = Assert.ThrowsAsync<InvalidDataException>(() => AgentConnection.ReadRegistrationAsync(
            new Responses(new() { Registered = new() { ProtocolVersion = 1 } }), default));
        Assert.That(error!.Message, Does.Contain("Core confirmed protocol v1"));
    }

    [Test]
    public void Wrong_message_type_is_not_treated_as_disconnect()
    {
        var error = Assert.ThrowsAsync<InvalidDataException>(() => AgentConnection.ReadRegistrationAsync(
            new Responses(new() { Execute = new() }), default));
        Assert.That(error!.Message, Does.Contain("received Execute"));
    }

    [Test]
    public void Authentication_rejection_is_preserved()
    {
        var rejection = new RpcException(new Status(StatusCode.Unauthenticated, "Certificate rejected"));
        var error = Assert.ThrowsAsync<RpcException>(() => AgentConnection.ReadRegistrationAsync(new Responses(null, rejection), default));
        Assert.That(error, Is.SameAs(rejection));
    }

    private sealed class Responses(ApplicationControlPlaneMessage? message, Exception? error = null)
        : IAsyncStreamReader<ApplicationControlPlaneMessage>
    {
        public ApplicationControlPlaneMessage Current => message!;
        public Task<bool> MoveNext(CancellationToken cancellationToken) =>
            error is null ? Task.FromResult(message is not null) : Task.FromException<bool>(error);
    }
}
