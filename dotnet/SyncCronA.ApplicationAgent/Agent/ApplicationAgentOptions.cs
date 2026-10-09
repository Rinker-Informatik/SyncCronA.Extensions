using System.ComponentModel.DataAnnotations;

namespace SyncCronA.ApplicationAgent.Agent;

public sealed class ApplicationAgentOptions : IValidatableObject
{
    public const string SectionName = "ApplicationAgent";

    [Required] public Uri ControlPlaneUrl { get; init; } = null!;
    public string? ControlPlaneServerCertificatePem { get; init; }
    [Required] public Guid ApplicationId { get; init; } = Guid.Empty;
    public string? ApplicationName { get; init; }
    [Required, MinLength(32)] public string BootstrapSecret { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public bool AutoApproveEnrollment { get; init; }
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(5);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!ControlPlaneUrl.IsAbsoluteUri || ControlPlaneUrl.Scheme != Uri.UriSchemeHttps)
            yield return new ValidationResult("ControlPlaneUrl must be an absolute HTTPS URL.");

        if (ApplicationId == Guid.Empty)
            yield return new ValidationResult("ApplicationId must be specified.");

        if (ApplicationName is { Length: > 200 } || ApplicationName is not null && string.IsNullOrWhiteSpace(ApplicationName))
            yield return new ValidationResult("ApplicationName must be nonblank and at most 200 characters.");

        if (HeartbeatInterval <= TimeSpan.Zero)
            yield return new ValidationResult("HeartbeatInterval must be greater than zero.");

        if (RetryInterval <= TimeSpan.Zero)
            yield return new ValidationResult("RetryInterval must be greater than zero.");
    }
}
