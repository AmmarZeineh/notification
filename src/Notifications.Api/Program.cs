using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Notifications.Api;
using Notifications.Api.Development;
using Notifications.Application;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<NotificationsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Notifications")));

builder.Services.AddNotificationsApplication();
builder.Services.AddNotificationsInfrastructure<NotificationsDbContext>(builder.Configuration);

// This host is a development harness: the main project provides real authentication.
// Fail at startup rather than answering every request with a 500 for a missing auth scheme.
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Notifications.Api only runs in Development. Host the feature in the main project, which provides authentication.");
}

builder.Services
    .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(NotificationPolicies.SendNotifications, policy => policy.RequireRole(NotificationPolicies.AdminRole));

var app = builder.Build();

// Checked after Build so configuration from every source (including test hosts) is in place.
// Fails at startup like the Firebase options, instead of answering every device request with a 500.
if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Notifications")))
{
    throw new InvalidOperationException(
        "The 'Notifications' connection string is missing. Set ConnectionStrings:Notifications with user-secrets (see README).");
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
