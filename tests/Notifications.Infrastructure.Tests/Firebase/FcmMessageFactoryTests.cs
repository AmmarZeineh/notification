using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Notifications.Application.Models;
using Notifications.Infrastructure.Firebase;

namespace Notifications.Infrastructure.Tests.Firebase;

public sealed class FcmMessageFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 15, 0, TimeSpan.FromHours(3));

    private readonly FcmMessageFactory _factory = new(Options.Create(new FirebaseOptions
    {
        AndroidChannelId = "alerts",
        Sound = "default",
    }), new FakeTimeProvider(Now));

    [Fact]
    public void Visible_notification_targets_token_with_android_and_ios_settings()
    {
        var data = new Dictionary<string, string> { ["screen"] = "fund", ["fundId"] = "1024" };
        var notification = new PushNotification("Report ready", "Daily report is available", data, new Uri("https://cdn.example.com/a.png"));

        var message = _factory.CreateForToken("token-1", notification);

        Assert.Equal("token-1", message.Token);
        Assert.Null(message.Topic);
        Assert.Equal("Report ready", message.Notification.Title);
        Assert.Equal("Daily report is available", message.Notification.Body);
        Assert.Equal("https://cdn.example.com/a.png", message.Notification.ImageUrl);
        Assert.Same(data, message.Data);

        Assert.Equal(Priority.High, message.Android.Priority);
        Assert.Equal("alerts", message.Android.Notification.ChannelId);
        Assert.Equal("default", message.Android.Notification.Sound);
        Assert.Equal(Now.UtcDateTime, message.Android.Notification.EventTimestamp);
        Assert.Equal(DateTimeKind.Utc, message.Android.Notification.EventTimestamp.Kind);

        Assert.Equal("alert", message.Apns.Headers["apns-push-type"]);
        Assert.Equal("10", message.Apns.Headers["apns-priority"]);
        Assert.Equal("default", message.Apns.Aps.Sound);
        Assert.True(message.Apns.Aps.MutableContent);
    }

    [Fact]
    public void Data_only_notification_is_sent_as_silent_background_push()
    {
        var notification = new PushNotification(null, null, new Dictionary<string, string> { ["sync"] = "true" });

        var message = _factory.CreateForTopic("news", notification);

        Assert.Equal("news", message.Topic);
        Assert.Null(message.Notification);
        Assert.Null(message.Android.Notification);
        Assert.Equal(Priority.Normal, message.Android.Priority);
        Assert.Equal("background", message.Apns.Headers["apns-push-type"]);
        Assert.Equal("5", message.Apns.Headers["apns-priority"]);
        Assert.True(message.Apns.Aps.ContentAvailable);
    }

    [Fact]
    public void Token_message_targets_only_that_token()
    {
        var message = _factory.CreateForToken("a", new PushNotification("t", "b"));

        Assert.Equal("a", message.Token);
        Assert.Null(message.Topic);
        Assert.Equal("t", message.Notification.Title);
        Assert.False(message.Apns.Aps.MutableContent);
    }
}
