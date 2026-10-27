using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Firebase;

/// <summary>
/// Resolves a relative <see cref="FirebaseOptions.CredentialsPath"/> from the content root (the project
/// folder), not from the working directory, which depends on how and where the process was started.
/// </summary>
/// <remarks>Without a host (e.g. a console tool), the path is left as configured.</remarks>
internal sealed class FirebaseCredentialsPathResolver(IHostEnvironment? environment = null) : IPostConfigureOptions<FirebaseOptions>
{
    public void PostConfigure(string? name, FirebaseOptions options)
    {
        if (environment is not null &&
            !string.IsNullOrWhiteSpace(options.CredentialsPath) &&
            !Path.IsPathRooted(options.CredentialsPath))
        {
            options.CredentialsPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.CredentialsPath));
        }
    }
}
