# Firebase Push Notifications (ASP.NET Core 10, Clean Architecture)

A self-contained push notification feature built on Firebase Cloud Messaging (FCM) for Android and iOS.
It is designed to be merged into the main project; see **[INTEGRATION.md](INTEGRATION.md)**.

## Capabilities

| Capability | Service method | Demo endpoint |
|---|---|---|
| Register / unregister a device for the signed-in user | `IDeviceRegistrationService` | `POST /api/devices`, `POST /api/devices/unregister` |
| Send to one device | `SendToDeviceAsync` | `POST /api/notifications/device` |
| Send to many devices (up to 10,000 per request, batched automatically) | `SendToDevicesAsync` | `POST /api/notifications/devices` |
| Send to all devices of a user | `SendToUserAsync` | `POST /api/notifications/users/{userId}` |
| Send to a topic | `SendToTopicAsync` | `POST /api/notifications/topics/{topic}` |
| Subscribe / unsubscribe devices to a topic | `SubscribeToTopicAsync` / `UnsubscribeFromTopicAsync` | `POST /api/notifications/topics/{topic}/subscribe` / `unsubscribe` |

Tokens that Firebase reports as unregistered (app uninstalled, token expired) are removed automatically, on sends
and on topic subscribe/unsubscribe. A token Firebase calls malformed is kept: Firebase reports a bad token and a bad
message the same way, and wrongly deleting a token silently loses a user's device. When every device fails that way,
a dry-run validation tells the caller whether the message itself must change. A failure of a whole batch never deletes
tokens.
Tokens the app has not re-registered for 270 days can also be removed by an optional daily cleanup
(see [INTEGRATION.md](INTEGRATION.md#4-configuration)).
Wrong credentials (`SENDER_ID_MISMATCH`, permission errors) return 502 and never delete tokens.

A multi-device send returns `retryableTokens`: the tokens that failed only for a temporary reason (outage, quota).
Resend to those alone, never to everyone, so nobody gets the notification twice. `unconfirmedTokens` are the ones
Firebase never answered for (timeout, dropped connection): the notification may already have arrived, so resend to
them only if a duplicate is acceptable. Whenever any delivery is unconfirmed, the API answers **202 Accepted** instead of
200 (single sends return `{ "messageId": null, "confirmed": false }`). It is never a 5xx, so HTTP retry policies,
which retry server errors automatically, cannot resend it into a duplicate. Once a send has started it always
finishes and reports its result, even if the caller disconnects.

## When to use which

**Send to a user**: a personal message for one person, delivered to every phone they are signed in on.
- "Your subscription of 5,000 SAR to Al-Noor Fund was completed"
- "New sign-in to your account from iPhone 16"

**Topics**: one message for many people. The server sends once and Firebase delivers to every subscribed device,
with no database lookup.

| Topic | Who is subscribed | Example |
|---|---|---|
| `announcements` | Every user (subscribed at sign-in) | "Our offices are closed on Friday for the national holiday" |
| `fund-1024-updates` | Users who tapped **Follow** on a fund | "Al-Noor Fund daily report for Oct 26 is available" |
| `app-maintenance` | Every user | "The app will be under maintenance tonight 2–4 AM" |
| `promotions` | Users who enabled **Offers** in settings | "New fund launch: zero subscription fees this week" |
| `lang-ar` / `lang-en` | Based on the user's language | The same broadcast in each language |

Following a fund: the app calls *subscribe* with its token for `fund-1024-updates`. When the report is published, the backend
sends once to the topic. When the user taps **Unfollow**, the app calls *unsubscribe*.

## Project structure

```
src/
  Notifications.Domain           DeviceToken entity (no dependencies)
  Notifications.Application      Services, ports (IPushNotificationSender, IDeviceTokenRepository), Result types
  Notifications.Infrastructure   Firebase Admin SDK adapter, EF Core mapping + repository
  Notifications.Api              Demo host: controllers, dev-only auth, OpenAPI
tests/                           Unit + integration tests (no Firebase or SQL Server required)
```

## Running locally

Requirements: .NET SDK 10.0.1xx or later (the team runs 10.0.3; all Microsoft packages are pinned to 10.0.3).

```bash
dotnet build
dotnet test
```

To send real notifications:

1. In the Firebase console of the team's project: **Project settings → Service accounts → Generate new private key**.
   Save the JSON **outside** this repository.
2. Point the API to it with user-secrets (stored outside the repo):
   ```bash
   dotnet user-secrets --project src/Notifications.Api set "Firebase:CredentialsPath" "/path/to/key.json"
   ```
3. Choose a local SQL Server password (letters, digits and a symbol, 8+ characters), then start SQL Server and
   create the database. The password lives only in `.env` (git-ignored) and user-secrets:
   ```bash
   echo 'MSSQL_SA_PASSWORD=<password>' > .env
   dotnet user-secrets --project src/Notifications.Api set "ConnectionStrings:Notifications" \
     "Server=localhost,1433;Database=Notifications;User Id=sa;Password=<password>;TrustServerCertificate=True"
   docker compose up -d
   dotnet tool restore
   dotnet ef database update --project src/Notifications.Infrastructure --startup-project src/Notifications.Api
   ```
4. Run the API and use `src/Notifications.Api/Notifications.Api.http` (VS Code REST Client / Rider / Visual Studio):
   ```bash
   dotnet run --project src/Notifications.Api
   ```
   In development, requests authenticate with the `X-User-Id` and `X-User-Roles: Admin` headers.

For iOS delivery, also upload an APNs authentication key in **Project settings → Cloud Messaging**.
