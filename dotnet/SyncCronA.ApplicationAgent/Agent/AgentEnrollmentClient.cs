using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using SyncCronA.Contracts.Enrollment;

namespace SyncCronA.ApplicationAgent.Agent;

internal sealed class AgentEnrollmentClient(ApplicationAgentOptions options, ApplicationAgentStatusService statusService, ILogger logger)
{
    internal async Task<(Guid AgentId, X509Certificate2 Certificate)?> EnrollAsync(Guid instanceId, CancellationToken token)
    {
        using var http = new HttpClient(CreateControlPlaneHandler()) { BaseAddress = options.ControlPlaneUrl };
        using var bootstrapResponse = await http.PostAsJsonAsync("api/application-enrollment/bootstrap",
            new { options.ApplicationId, InstanceId = instanceId }, token);
        bootstrapResponse.EnsureSuccessStatusCode();

        var response = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapResponse>(cancellationToken: token)
                       ?? throw new InvalidDataException("Empty enrollment response.");
        var agentId = response.AgentId ?? throw new InvalidDataException("Enrollment response has no agent ID.");

        if (!string.Equals(response.Status, "Approved", StringComparison.Ordinal)
            || response.ChallengeId is null || response.Challenge is null)
        {
            if (options.AutoApproveEnrollment && string.Equals(response.Status, "Pending", StringComparison.Ordinal))
            {
                using var approvalResponse = await http.PostAsJsonAsync(
                    $"api/agents/{agentId:D}/approve",
                    new { options.BootstrapSecret }, token);
                approvalResponse.EnsureSuccessStatusCode();
                statusService.Update("AwaitingApproval", instanceId, detail: "Test enrollment approved; connecting next.");
                logger.LogInformation("Approved the test application enrollment.");
                return null;
            }

            statusService.Update("AwaitingApproval", instanceId, detail: $"Enrollment status: {response.Status}");
            logger.LogInformation("Application enrollment is {Status}; awaiting approval.", response.Status);
            return null;
        }

        using var rsaKey = RSA.Create(3072);
        var csr = new CertificateRequest($"CN={options.ApplicationId}-{instanceId}", rsaKey, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1)
            .CreateSigningRequestPem();
        var challenge = Convert.FromBase64String(response.Challenge);
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(csr));
        using var hmac = new HMACSHA256(System.Text.Encoding.UTF8.GetBytes(options.BootstrapSecret));
        var proof = Convert.ToBase64String(hmac.ComputeHash(
            EnrollmentProof.Canonicalize(agentId, instanceId, challenge, hash)));

        using var completeResponse = await http.PostAsJsonAsync("api/application-enrollment/complete",
            new { AgentId = agentId, InstanceId = instanceId, ChallengeId = response.ChallengeId, CsrPem = csr, Proof = proof },
            token);
        completeResponse.EnsureSuccessStatusCode();
        var enrollmentResponse = await completeResponse.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: token)
                                 ?? throw new InvalidDataException("Empty enrollment response.");

        using var publicCertificate = X509Certificate2.CreateFromPem(enrollmentResponse.ClientCertificatePem);
        return (agentId, publicCertificate.CopyWithPrivateKey(rsaKey));
    }

    internal HttpClientHandler CreateControlPlaneHandler()
    {
        var handler = new HttpClientHandler();
        if (string.IsNullOrWhiteSpace(options.ControlPlaneServerCertificatePem))
            return handler;

        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                return false;

            using var trustedRoot = X509Certificate2.CreateFromPem(options.ControlPlaneServerCertificatePem);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(certificate));
        };
        return handler;
    }
}