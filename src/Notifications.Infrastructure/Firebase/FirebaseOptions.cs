namespace Notifications.Infrastructure.Firebase;

/// <summary>
/// Bound from the "Firebase" configuration section. Provide exactly one credential source:
/// <see cref="CredentialsPath"/>, <see cref="CredentialsJson"/>, or <see cref="UseApplicationDefaultCredentials"/>.
/// Never commit the service-account key; use user-secrets locally and a secret store in production.
/// </summary>
public sealed class FirebaseOptions
{
    public const string SectionName = "Firebase";

    /// <summary>Path to the service-account JSON key file. A relative path is resolved from the content root.</summary>
    public string? CredentialsPath { get; set; }

    /// <summary>The service-account JSON key content (e.g. injected from a secret store as an environment variable).</summary>
    public string? CredentialsJson { get; set; }

    /// <summary>Use Google Application Default Credentials (GOOGLE_APPLICATION_CREDENTIALS, workload identity).</summary>
    public bool UseApplicationDefaultCredentials { get; set; }

    /// <summary>Optional; inferred from the credentials when omitted.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Android notification channel the app creates (Android 8+). Defaults to the FCM fallback channel when null.</summary>
    public string? AndroidChannelId { get; set; }

    /// <summary>Sound played on both platforms; "default" uses the system sound.</summary>
    public string Sound { get; set; } = "default";
}
