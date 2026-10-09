using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SyncCronA.ApplicationAgent;
using SyncCronA.ApplicationAgent.Agent;

namespace SyncCronA.ApplicationAgent.Unittests;

[TestFixture]
public class ApplicationAgentWorkerTests
{
    [Test]
    public async Task StartAsync_WhenControlPlaneIsUnavailable_PublishesRetryStatus()
    {
        var status = new ApplicationAgentStatusService();
        using var worker = new ApplicationAgentWorker(
            Options.Create(OptionsForUnavailableControlPlane()),
            status,
            NullLogger<ApplicationAgentWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (status.Get().State != "Retrying")
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, timeout.Token);
            }

            var snapshot = status.Get();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.State, Is.EqualTo("Retrying"));
                Assert.That(snapshot.InstanceId, Is.Not.EqualTo(Guid.Empty));
                Assert.That(snapshot.Detail, Is.Not.Empty);
                Assert.That(snapshot.AgentId, Is.Null);
                Assert.That(snapshot.SessionId, Is.Null);
            });
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task ConstructorWithoutStatus_CanStartAndStop()
    {
        using var worker = new ApplicationAgentWorker(
            Options.Create(OptionsForUnavailableControlPlane()),
            NullLogger<ApplicationAgentWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
    }

    [TestCase("Pending", false, "AwaitingApproval")]
    [TestCase("Rejected", false, "AwaitingApproval")]
    [TestCase("Approved", false, "AwaitingApproval")]
    [TestCase("Pending", true, "AwaitingApproval")]
    public async Task StartAsync_WithEnrollmentNotApproved_PublishesAwaitingApproval(
        string enrollmentState, bool autoApprove, string expectedState)
    {
        var approvals = 0;
        await using var server = await StartServerAsync(app =>
        {
            app.MapPost("/api/application-enrollment/bootstrap", () => Results.Json(new
            {
                Status = enrollmentState,
                AgentId = Guid.NewGuid()
            }));
            app.MapPost("/api/agents/{id:guid}/approve", () =>
            {
                Interlocked.Increment(ref approvals);
                return Results.Ok();
            });
        });

        var status = new ApplicationAgentStatusService();
        using var worker = new ApplicationAgentWorker(
            Options.Create(OptionsForControlPlane(server.BaseAddress, autoApprove)),
            status,
            NullLogger<ApplicationAgentWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var snapshot = await WaitForStateAsync(status, expectedState);
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Detail, autoApprove
                    ? Is.EqualTo("Test enrollment approved; connecting next.")
                    : Is.EqualTo($"Enrollment status: {enrollmentState}"));
                Assert.That(approvals, Is.EqualTo(autoApprove ? 1 : 0));
            });
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TestCase(500, null, "Internal Server Error")]
    [TestCase(200, "null", "Empty enrollment response.")]
    [TestCase(200, "{\"status\":\"Approved\"}", "Enrollment response has no agent ID.")]
    public async Task StartAsync_WithBadBootstrapResponse_PublishesRetryReason(
        int statusCode, string? body, string expectedDetail)
    {
        await using var server = await StartServerAsync(app =>
            app.MapPost("/api/application-enrollment/bootstrap", () =>
                Results.Text(body ?? "", "application/json", statusCode: statusCode)));

        var status = new ApplicationAgentStatusService();
        using var worker = new ApplicationAgentWorker(
            Options.Create(OptionsForControlPlane(server.BaseAddress)),
            status,
            NullLogger<ApplicationAgentWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var snapshot = await WaitForStateAsync(status, "Retrying");
            Assert.That(snapshot.Detail, Does.Contain(expectedDetail));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StartAsync_WithApprovedEnrollmentAndInvalidChallenge_PublishesRetryReason(bool invalidBase64)
    {
        await using var server = await StartServerAsync(app =>
            app.MapPost("/api/application-enrollment/bootstrap", () => Results.Json(new
            {
                Status = "Approved",
                AgentId = Guid.NewGuid(),
                ChallengeId = Guid.NewGuid(),
                Challenge = invalidBase64 ? "not base64!" : Convert.ToBase64String([0x01, 0x02])
            })));

        var status = new ApplicationAgentStatusService();
        using var worker = new ApplicationAgentWorker(
            Options.Create(OptionsForControlPlane(server.BaseAddress)),
            status,
            NullLogger<ApplicationAgentWorker>.Instance);

        // A valid challenge gets past parsing and eventually fails because no completion endpoint exists.
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var snapshot = await WaitForStateAsync(status, "Retrying");
            Assert.That(snapshot.Detail, invalidBase64
                ? Does.Contain("Base-64")
                : Does.Contain("404"));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<ApplicationAgentStatusService.Snapshot> WaitForStateAsync(
        ApplicationAgentStatusService statusService, string expectedState)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (statusService.Get().State != expectedState)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }

        return statusService.Get();
    }

    private static async Task<TestServer> StartServerAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        configure(app);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        return new TestServer(app, new Uri(addresses.Addresses.Single()));
    }

    private sealed record TestServer(WebApplication App, Uri BaseAddress) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static ApplicationAgentOptions OptionsForControlPlane(Uri url, bool autoApprove = false) => new()
    {
        ControlPlaneUrl = url,
        ApplicationId = Guid.NewGuid(),
        BootstrapSecret = new string('x', 32),
        AutoApproveEnrollment = autoApprove,
        RetryInterval = TimeSpan.FromSeconds(10)
    };

    private static ApplicationAgentOptions OptionsForUnavailableControlPlane() => new()
    {
        ControlPlaneUrl = new Uri("http://127.0.0.1:1/"),
        ApplicationId = Guid.NewGuid(),
        BootstrapSecret = new string('x', 32),
        RetryInterval = TimeSpan.FromMilliseconds(50)
    };
}
