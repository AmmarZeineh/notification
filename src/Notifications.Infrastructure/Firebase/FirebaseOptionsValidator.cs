using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Firebase;

internal sealed class FirebaseOptionsValidator : IValidateOptions<FirebaseOptions>
{
    public ValidateOptionsResult Validate(string? name, FirebaseOptions options)
    {
        var sources = new[]
        {
            !string.IsNullOrWhiteSpace(options.CredentialsPath),
            !string.IsNullOrWhiteSpace(options.CredentialsJson),
            options.UseApplicationDefaultCredentials,
        }.Count(configured => configured);

        // Checked here so a wrong path stops the app at startup instead of failing the first send.
        if (!string.IsNullOrWhiteSpace(options.CredentialsPath) && !File.Exists(options.CredentialsPath))
        {
            return ValidateOptionsResult.Fail(
                $"The Firebase credentials file '{options.CredentialsPath}' does not exist.");
        }

        return sources switch
        {
            1 => ValidateOptionsResult.Success,
            0 => ValidateOptionsResult.Fail(
                $"Firebase credentials are not configured. Set '{FirebaseOptions.SectionName}:{nameof(FirebaseOptions.CredentialsPath)}', " +
                $"'{FirebaseOptions.SectionName}:{nameof(FirebaseOptions.CredentialsJson)}' or " +
                $"'{FirebaseOptions.SectionName}:{nameof(FirebaseOptions.UseApplicationDefaultCredentials)}'."),
            _ => ValidateOptionsResult.Fail("Configure only one Firebase credential source."),
        };
    }
}
