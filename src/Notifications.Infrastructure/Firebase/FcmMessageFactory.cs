using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Options;
using Notifications.Application.Models;
using FcmNotification = FirebaseAdmin.Messaging.Notification;

namespace Notifications.Infrastructure.Firebase;

/// <summary>Maps the provider-neutral <see cref="PushNotification"/> to FCM messages with Android and iOS settings.</summary>
internal sealed class FcmMessageFactory(IOptions<FirebaseOptions> options, TimeProvider timeProvider)
{
    private readonly FirebaseOptions _options = options.Value;

    public Message CreateForToken(string token, PushNotification notification) =>
        WithTarget(Create(notification), token: token);

    /// <summary>One message per token. The content and platform settings are built once and shared.</summary>
    public IReadOnlyList<Message> CreateForTokens(IEnumerable<string> tokens, PushNotification notification)
    {
        var template = Create(notification);
        return tokens.Select(token => WithTarget(template, token: token)).ToList();
    }

    public Message CreateForTopic(string topic, PushNotification notification) =>
        WithTarget(Create(notification), topic: topic);

    private Message Create(PushNotification notification)
    {
        var dataOnly = !notification.HasVisibleContent;
        return new Message
        {
            Notification = CreateNotification(notification, dataOnly),
            Data = notification.Data,
            Android = CreateAndroidConfig(dataOnly, timeProvider.GetUtcNow()),
            Apns = CreateApnsConfig(notification, dataOnly),
        };
    }

    private static Message WithTarget(Message template, string? token = null, string? topic = null) => new()
    {
        Token = token,
        Topic = topic,
        Notification = template.Notification,
        Data = template.Data,
        Android = template.Android,
        Apns = template.Apns,
    };

    private static FcmNotification? CreateNotification(PushNotification notification, bool dataOnly) =>
        dataOnly
            ? null
            : new FcmNotification
            {
                Title = notification.Title,
                Body = notification.Body,
                ImageUrl = notification.ImageUrl?.AbsoluteUri,
            };

    private AndroidConfig CreateAndroidConfig(bool dataOnly, DateTimeOffset sentAt) => new()
    {
        // High priority wakes the device so a visible notification is shown immediately. Silent data messages
        // use normal priority: Android demotes apps whose high-priority messages show nothing, which would
        // delay their later visible notifications.
        Priority = dataOnly ? Priority.Normal : Priority.High,
        Notification = dataOnly
            ? null
            : new AndroidNotification
            {
                ChannelId = _options.AndroidChannelId,
                Sound = _options.Sound,
                // The SDK always sends event_time, as 0001-01-01 when unset, and Android shows it as the
                // notification's date. Must be a UTC DateTime: the SDK converts any other kind as local time.
                EventTimestamp = sentAt.UtcDateTime,
            },
    };

    private ApnsConfig CreateApnsConfig(PushNotification notification, bool dataOnly)
    {
        if (dataOnly)
        {
            // Silent background update: APNs requires the background push type and priority 5.
            return new ApnsConfig
            {
                Headers = new Dictionary<string, string>
                {
                    ["apns-push-type"] = "background",
                    ["apns-priority"] = "5",
                },
                Aps = new Aps { ContentAvailable = true },
            };
        }

        return new ApnsConfig
        {
            Headers = new Dictionary<string, string>
            {
                ["apns-push-type"] = "alert",
                ["apns-priority"] = "10",
            },
            Aps = new Aps
            {
                Sound = _options.Sound,
                // Lets a Notification Service Extension download and attach the image.
                MutableContent = notification.ImageUrl is not null,
            },
        };
    }
}
