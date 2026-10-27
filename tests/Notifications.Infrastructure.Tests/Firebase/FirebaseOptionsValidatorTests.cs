using Notifications.Infrastructure.Firebase;

namespace Notifications.Infrastructure.Tests.Firebase;

public sealed class FirebaseOptionsValidatorTests
{
    private readonly FirebaseOptionsValidator _validator = new();

    [Fact]
    public void Fails_when_no_credentials_are_configured() =>
        Assert.True(_validator.Validate(null, new FirebaseOptions()).Failed);

    [Fact]
    public void Fails_when_multiple_credential_sources_are_configured() =>
        Assert.True(_validator.Validate(null, new FirebaseOptions { CredentialsPath = "key.json", CredentialsJson = "{}" }).Failed);

    [Theory]
    [InlineData("{}", false)]
    [InlineData(null, true)]
    public void Succeeds_with_exactly_one_credential_source(string? json, bool useAdc) =>
        Assert.True(_validator.Validate(null, new FirebaseOptions
        {
            CredentialsJson = json,
            UseApplicationDefaultCredentials = useAdc,
        }).Succeeded);

    [Fact]
    public void Succeeds_with_an_existing_credentials_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.True(_validator.Validate(null, new FirebaseOptions { CredentialsPath = path }).Succeeded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fails_at_startup_when_the_credentials_file_is_missing()
    {
        var result = _validator.Validate(null, new FirebaseOptions { CredentialsPath = "/no/such/key.json" });

        Assert.True(result.Failed);
        Assert.Contains("does not exist", result.FailureMessage, StringComparison.Ordinal);
    }
}
