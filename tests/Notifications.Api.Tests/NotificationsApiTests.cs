using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Models;
using Notifications.Domain.DeviceTokens;
using Notifications.Infrastructure.Persistence;

namespace Notifications.Api.Tests;

public sealed class NotificationsApiTests : IAsyncLifetime
{
    private readonly NotificationsApiFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _factory.InitializeDatabaseAsync();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Register_device_requires_authentication()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/devices", new { token = "token-1", platform = "Android" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_device_validates_request_body()
    {
        var response = await CreateClient("user-1").PostAsJsonAsync("/api/devices", new { platform = "Android" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Token", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_device_receives_notification_sent_to_its_user()
    {
        var user = CreateClient("user-1");
        var admin = CreateClient("admin-1", "Admin");
        _factory.Sender.SendToTokensAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<PushNotification>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyList<string>>().Select(t => new TokenSendResult(t, SendResult.Delivered("m"))).ToList());

        var register = await user.PostAsJsonAsync("/api/devices", new { token = "token-1", platform = 1 }, Ct);
        var send = await admin.PostAsJsonAsync("/api/notifications/users/user-1", new { title = "Hello", body = "World" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, register.StatusCode);
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal(new BatchSendSummary(1, 0, 0), await send.Content.ReadFromJsonAsync<BatchSendSummary>(Ct));
        await _factory.Sender.Received(1).SendToTokensAsync(
            Arg.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "token-1" })),
            Arg.Is<PushNotification>(n => n.Title == "Hello"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unregister_removes_device()
    {
        var user = CreateClient("user-1");
        await user.PostAsJsonAsync("/api/devices", new { token = "token-1", platform = 2 }, Ct);

        var response = await user.PostAsJsonAsync("/api/devices/unregister", new { token = "token-1" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<DeviceToken>());
    }

    [Fact]
    public async Task Registering_another_users_token_returns_409_and_keeps_the_owner()
    {
        await CreateClient("owner").PostAsJsonAsync("/api/devices", new { token = "token-1", platform = 1 }, Ct);

        var response = await CreateClient("intruder").PostAsJsonAsync("/api/devices", new { token = "token-1", platform = 1 }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = Assert.Single(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<DeviceToken>());
        Assert.Equal("owner", stored.UserId);
    }

    [Fact]
    public async Task Provider_outage_returns_503_with_retry_after()
    {
        _factory.Sender.SendToTopicAsync("news", Arg.Any<PushNotification>(), Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.Unavailable, "quota exceeded"));

        var response = await CreateClient("admin-1", "Admin").PostAsJsonAsync(
            "/api/notifications/topics/news", new { title = "Hi" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Unconfirmed_send_returns_202_so_retry_policies_do_not_resend()
    {
        _factory.Sender.SendToTopicAsync("news", Arg.Any<PushNotification>(), Arg.Any<CancellationToken>())
            .Returns(SendResult.Failed(DeliveryFailure.Unknown, "timeout"));

        var response = await CreateClient("admin-1", "Admin").PostAsJsonAsync(
            "/api/notifications/topics/news", new { title = "Hi" }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("\"confirmed\":false", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sending_requires_admin_role()
    {
        var response = await CreateClient("user-1").PostAsJsonAsync(
            "/api/notifications/topics/news", new { title = "Hi" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Send_to_user_without_devices_returns_problem_details_404()
    {
        var response = await CreateClient("admin-1", "Admin").PostAsJsonAsync(
            "/api/notifications/users/nobody", new { title = "Hi" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Topic_subscribe_and_send_round_trip()
    {
        var admin = CreateClient("admin-1", "Admin");
        _factory.Sender.SubscribeToTopicAsync(Arg.Any<IReadOnlyList<string>>(), "news", Arg.Any<CancellationToken>())
            .Returns(new TopicSubscriptionResult(2, []));
        _factory.Sender.SendToTopicAsync("news", Arg.Any<PushNotification>(), Arg.Any<CancellationToken>())
            .Returns(SendResult.Delivered("projects/demo/messages/1"));

        var subscribe = await admin.PostAsJsonAsync("/api/notifications/topics/news/subscribe", new { tokens = new[] { "a", "b" } }, Ct);
        var send = await admin.PostAsJsonAsync("/api/notifications/topics/news", new { title = "Offices closed Friday" }, Ct);

        Assert.Equal(HttpStatusCode.OK, subscribe.StatusCode);
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Contains("projects/demo/messages/1", await send.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relative_image_url_returns_400_instead_of_crashing()
    {
        var response = await CreateClient("admin-1", "Admin").PostAsJsonAsync(
            "/api/notifications/topics/news", new { title = "Hi", imageUrl = "banner.png" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Notifications.InvalidImageUrl", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_topic_returns_400()
    {
        var response = await CreateClient("admin-1", "Admin").PostAsJsonAsync(
            "/api/notifications/topics/bad%20topic", new { title = "Hi" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient CreateClient(string userId, string? roles = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add("X-User-Roles", roles);
        }

        return client;
    }
}
