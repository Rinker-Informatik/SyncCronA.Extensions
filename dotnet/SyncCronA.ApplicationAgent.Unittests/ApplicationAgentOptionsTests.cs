using System.ComponentModel.DataAnnotations;
using SyncCronA.ApplicationAgent;
using SyncCronA.ApplicationAgent.Agent;

namespace SyncCronA.ApplicationAgent.Unittests;

[TestFixture]
public class ApplicationAgentOptionsTests
{
    [Test]
    public void Defaults_UseExpectedSectionAndIntervals()
    {
        var options = new ApplicationAgentOptions();

        Assert.Multiple(() =>
        {
            Assert.That(ApplicationAgentOptions.SectionName, Is.EqualTo("ApplicationAgent"));
            Assert.That(options.ApplicationId, Is.EqualTo(Guid.Empty));
            Assert.That(options.BootstrapSecret, Is.Empty);
            Assert.That(options.AutoApproveEnrollment, Is.False);
            Assert.That(options.HeartbeatInterval, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(options.RetryInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
        });
    }

    [Test]
    public void Validate_WithValidOptions_ReturnsNoErrors()
    {
        var options = ValidOptions();

        Assert.That(Validate(options), Is.Empty);
    }

    [TestCase("http://example.com/")]
    [TestCase("/relative")]
    public void Validate_WithNonHttpsOrRelativeUrl_ReturnsUrlError(string url)
    {
        var options = ValidOptions(url: new Uri(url, UriKind.RelativeOrAbsolute));

        Assert.That(Validate(options), Does.Contain("ControlPlaneUrl must be an absolute HTTPS URL."));
    }

    [Test]
    public void Validate_WithEmptyApplicationId_ReturnsIdError()
    {
        var options = ValidOptions(applicationId: Guid.Empty);

        Assert.That(Validate(options), Does.Contain("ApplicationId must be specified."));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Validate_WithNonPositiveHeartbeatInterval_ReturnsError(int seconds)
    {
        var options = ValidOptions(heartbeatInterval: TimeSpan.FromSeconds(seconds));

        Assert.That(Validate(options), Does.Contain("HeartbeatInterval must be greater than zero."));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Validate_WithNonPositiveRetryInterval_ReturnsError(int seconds)
    {
        var options = ValidOptions(retryInterval: TimeSpan.FromSeconds(seconds));

        Assert.That(Validate(options), Does.Contain("RetryInterval must be greater than zero."));
    }

    [Test]
    public void Validate_WithMultipleInvalidValues_ReturnsAllApplicableErrors()
    {
        var options = ValidOptions(
            url: new Uri("http://example.com/"),
            applicationId: Guid.Empty,
            heartbeatInterval: TimeSpan.Zero,
            retryInterval: TimeSpan.Zero);

        Assert.That(Validate(options), Has.Length.EqualTo(4));
    }

    [TestCase(31, false)]
    [TestCase(32, true)]
    public void DataAnnotations_EnforceBootstrapSecretMinimumLength(int length, bool valid)
    {
        var options = ValidOptions(secret: new string('x', length));
        var results = new List<ValidationResult>();

        var actual = Validator.TryValidateObject(options, new ValidationContext(options), results, true);

        Assert.That(actual, Is.EqualTo(valid));
        if (!valid)
            Assert.That(results.SelectMany(result => result.MemberNames),
                Does.Contain(nameof(ApplicationAgentOptions.BootstrapSecret)));
    }

    [Test]
    public void Validate_WithMissingUrl_ThrowsNullReferenceException()
    {
        var options = new ApplicationAgentOptions();

        Assert.Throws<NullReferenceException>(() => options.Validate(new ValidationContext(options)).ToArray());
    }

    private static string[] Validate(ApplicationAgentOptions options) =>
        options.Validate(new ValidationContext(options)).Select(result => result.ErrorMessage!).ToArray();

    private static ApplicationAgentOptions ValidOptions(
        Uri? url = null,
        Guid? applicationId = null,
        string? secret = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? retryInterval = null) => new()
        {
            ControlPlaneUrl = url ?? new Uri("https://example.com/"),
            ApplicationId = applicationId ?? Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            BootstrapSecret = secret ?? new string('x', 32),
            HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(15),
            RetryInterval = retryInterval ?? TimeSpan.FromSeconds(5)
        };
}
