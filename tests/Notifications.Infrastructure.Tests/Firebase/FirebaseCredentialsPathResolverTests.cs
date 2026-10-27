using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure.Firebase;

namespace Notifications.Infrastructure.Tests.Firebase;

public sealed class FirebaseCredentialsPathResolverTests
{
    private static readonly string ContentRoot = Path.Combine(Path.GetTempPath(), "content-root");

    [Fact]
    public void Resolves_relative_path_from_the_content_root()
    {
        var options = new FirebaseOptions { CredentialsPath = Path.Combine("secrets", "key.json") };

        new FirebaseCredentialsPathResolver(new TestEnvironment()).PostConfigure(null, options);

        Assert.Equal(Path.Combine(ContentRoot, "secrets", "key.json"), options.CredentialsPath);
    }

    [Fact]
    public void Keeps_absolute_path()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "key.json");
        var options = new FirebaseOptions { CredentialsPath = absolute };

        new FirebaseCredentialsPathResolver(new TestEnvironment()).PostConfigure(null, options);

        Assert.Equal(absolute, options.CredentialsPath);
    }

    [Fact]
    public void Leaves_path_unchanged_without_a_host()
    {
        var options = new FirebaseOptions { CredentialsPath = "key.json" };

        new FirebaseCredentialsPathResolver().PostConfigure(null, options);

        Assert.Equal("key.json", options.CredentialsPath);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = ContentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
