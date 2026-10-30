# Integrating the Notifications feature into the main project

The feature is split along Clean Architecture boundaries. Copy each project's folder into the matching
layer of the main solution (or reference the projects directly). The `Notifications.Api` project is a demo
host; only its controllers/contracts are meant to be reused.

| This repo | Main project layer | What to take |
|---|---|---|
| `src/Notifications.Domain` | Domain | `DeviceTokens/` |
| `src/Notifications.Application` | Application | `Abstractions/`, `Common/`, `Models/`, `Services/`, `DependencyInjection.cs` |
| `src/Notifications.Infrastructure` | Infrastructure | `Firebase/`, `Cleanup/`, `Persistence/` (except `NotificationsDbContext` and its migrations), `DependencyInjection.cs` |
| `src/Notifications.Api` | API / Web | `Controllers/`, `Contracts/`, `Extensions/`, `NotificationPolicies.cs` |
| `src/Notifications.Api/Development/` | — | **Do not copy.** Development-only header authentication. |

If the main project already has its own `Result`/`Error` types, replace `Notifications.Application.Common`
with them; the services only use `Success`, `Failure`, `IsSuccess`, `Value` and `Error`.

## 1. Packages (versions match the team's .NET 10.0.3)

- Infrastructure: `FirebaseAdmin` 3.5.0, `Microsoft.EntityFrameworkCore.SqlServer` 10.0.3,
  `Microsoft.Extensions.Options.ConfigurationExtensions` 10.0.3
- Application: `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.3, `Microsoft.Extensions.Logging.Abstractions` 10.0.3

## 2. Add the table to the existing DbContext

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyNotificationsConfiguration();
}
```

Then create a migration in the main project:

```bash
dotnet ef migrations add AddDeviceTokens
```

This creates the `DeviceTokens` table (unique index on `Token`, index on `UserId`).

## 3. Register services (Program.cs)

```csharp
builder.Services.AddNotificationsApplication();
builder.Services.AddNotificationsInfrastructure<AppDbContext>(builder.Configuration); // your DbContext
```

`AddNotificationsInfrastructure` does not register a DbContext. It reuses the one the main project already
registers for the request, so tenant filters, audit interceptors and transactions apply to device tokens as
well. The repository calls `SaveChangesAsync` itself, so it works with or without a unit-of-work pattern.

> **Shared DbContext:** registering a device calls `SaveChangesAsync` on the request's context, so it saves
> **every** pending change in it. Unregistering, and removing tokens Firebase reports as unregistered, are
> single `DELETE` statements that run immediately, outside the context's save and any unit of work (they are
> not rolled back if the request's own save later fails). Call `IDeviceRegistrationService` from its own request
> (as `DevicesController` does), or after the request's own changes are saved, never in the middle of an
> unsaved unit of work.

The `Token` and `UserId` columns use the binary collation `Latin1_General_100_BIN2`, because FCM tokens are
case-sensitive and the code compares user ids exactly, while SQL Server compares text case-insensitively by
default. Keep it if you write the migration by hand.

Add the authorization policy used by the sending endpoints (adjust to the main project's roles):

```csharp
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(NotificationPolicies.SendNotifications, p => p.RequireRole(NotificationPolicies.AdminRole));
```

The controllers read the current user from the `ClaimTypes.NameIdentifier` claim
(`ClaimsPrincipalExtensions.GetUserId`). Change that one method if the main project uses a different claim
or an `ICurrentUser` service.

## 4. Configuration

```json
"Firebase": {
  "CredentialsPath": "",          // or CredentialsJson, or UseApplicationDefaultCredentials: true
  "AndroidChannelId": "default_channel"
}
```

- Exactly one credential source must be set; the app fails at startup with a clear message otherwise.
- Never commit the service-account JSON. Locally use `dotnet user-secrets`; in production use environment
  variables (`Firebase__CredentialsJson`) or the hosting platform's secret store.
- The feature creates its own **named** `FirebaseApp` (prefixed "notifications-"), so it does not clash with any
  `FirebaseApp.DefaultInstance` the main project may already create.

### Stale device tokens (optional)

```json
"DeviceTokenCleanup": {
  "Enabled": true,
  "StaleAfter": "270.00:00:00",   // delete tokens not re-registered for this long (minimum 30 days)
  "Interval": "1.00:00:00"        // how often the cleanup runs
}
```

Off by default. Firebase treats a token as expired after 270 days without use, so these tokens only cost wasted
sends. **Enable it only once the app re-registers its token on every start** (see the mobile app contract below):
the cleanup goes by the last registration, so without that, a phone that is still in use looks stale and loses
its notifications. Each run is a single `DELETE`, run in its own scope.

## 5. Sending from application code

Inject `IPushNotificationService` anywhere in the Application layer:

```csharp
await pushNotifications.SendToUserAsync(
    userId,
    new PushNotification(
        Title: "Subscription completed",
        Body: "Your subscription of 5,000 SAR to Al-Noor Fund was completed",
        Data: new Dictionary<string, string> { ["screen"] = "requests" }),
    cancellationToken);
```

Results are returned as `Result<T>`, so callers decide what to do with expected failures. Tokens that
Firebase reports as unregistered are deleted automatically.

## 6. Mobile app contract

| When | App calls |
|---|---|
| After sign-in, on every app start while signed in, and whenever FCM gives a new token (`onNewToken`) | `POST /api/devices` `{ "token": "...", "platform": "Android" \| "Ios" }` |
| On sign-out (then delete the FCM token on the device) | `POST /api/devices/unregister` `{ "token": "..." }` |
| `POST /api/devices` returned **409** (token belongs to another user) | Delete the FCM token, get the new one, register it |

- A token is never moved from one user to another: knowing a token does not prove owning the device.
  The app deletes its token on sign-out, so the next user on a shared phone registers a fresh one.
- The token is always sent in the body, never in a URL, so it stays out of server and proxy logs.
- Registering on every start is idempotent: it only refreshes the token's last-seen time, which the stale
  token cleanup relies on.

- Android 8+: the app must create the notification channel set in `Firebase:AndroidChannelId`.
  The Fonerium app creates `default_channel`, so keep that value.
- The app subscribes every installation to the `announcements` topic, signed in or not.

### Opening a screen when the notification is tapped

Put a `screen` key in `PushNotification.Data`. Unknown values are ignored, so the app never crashes on them.

| `screen` | Extra keys | Opens |
|---|---|---|
| `home` | | Market tab |
| `assets` | | Assets tab |
| `requests` | | Requests tab |
| `settings` | | Settings tab |
| `fund_details` | `fundCode` | Fund details screen |

```csharp
Data: new Dictionary<string, string> { ["screen"] = "fund_details", ["fundCode"] = "AFT" }
```
- iOS: upload an APNs authentication key in Firebase Console → Project settings → Cloud Messaging.
  Images on iOS need a Notification Service Extension in the app (`mutable-content` is already set).
  Image URLs must be `https` (iOS blocks plain http), and an image needs a title or body to be shown.
