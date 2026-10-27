using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Notifications.Application.Models;

namespace Notifications.Infrastructure.Firebase;

internal static class FcmErrorMapper
{
    /// <summary>
    /// Classifies an exception thrown by a provider call. Firebase errors are mapped by their code; a timeout
    /// or dropped connection is <see cref="DeliveryFailure.Unknown"/>, since the provider may already have
    /// accepted the message. Returns null for anything else, including local I/O errors such as an unreadable
    /// credentials file: those are configuration faults, not outages, and callers let them surface.
    /// </summary>
    public static DeliveryFailure? Classify(Exception exception) => exception switch
    {
        FirebaseException firebaseException => Map(firebaseException),
        DeliveryStateUnknownException => DeliveryFailure.Unknown,
        _ when IsNoAnswer(exception) => DeliveryFailure.Unknown,
        _ => null,
    };

    /// <summary>Maps any Firebase error, including whole-request failures that are not messaging specific.</summary>
    public static DeliveryFailure Map(FirebaseException exception)
    {
        // The SDK wraps transport failures in its own exception; the request may still have been processed.
        if (exception.InnerException is { } inner && IsNoAnswer(inner))
        {
            return DeliveryFailure.Unknown;
        }

        return exception is FirebaseMessagingException messagingException
            ? Map(messagingException.MessagingErrorCode, exception.ErrorCode)
            : Map(null, exception.ErrorCode);
    }

    private static bool IsNoAnswer(Exception exception) =>
        exception is HttpRequestException or TimeoutException or TaskCanceledException;

    /// <remarks>
    /// Only UNREGISTERED marks a token as invalid, because invalid tokens are deleted.
    /// SENDER_ID_MISMATCH means the server uses credentials of another Firebase project: a deployment
    /// problem that would otherwise delete every valid token, so it is reported as rejected.
    /// INVALID_ARGUMENT covers both bad payloads (e.g. over 4 KB) and malformed tokens, so it never deletes
    /// a token; the application validates the message on its own only to tell the caller which it was.
    /// </remarks>
    public static DeliveryFailure Map(MessagingErrorCode? messagingErrorCode, ErrorCode errorCode) =>
        messagingErrorCode switch
        {
            MessagingErrorCode.Unregistered => DeliveryFailure.InvalidToken,
            MessagingErrorCode.QuotaExceeded or MessagingErrorCode.Unavailable or MessagingErrorCode.Internal
                => DeliveryFailure.Unavailable,
            MessagingErrorCode.InvalidArgument => DeliveryFailure.InvalidRequest,
            MessagingErrorCode.ThirdPartyAuthError => DeliveryFailure.Rejected,
            _ => errorCode switch
            {
                ErrorCode.Unavailable or ErrorCode.Internal or ErrorCode.ResourceExhausted
                    => DeliveryFailure.Unavailable,

                // The deadline passed while waiting; the message may already have been accepted.
                ErrorCode.DeadlineExceeded => DeliveryFailure.Unknown,
                ErrorCode.InvalidArgument => DeliveryFailure.InvalidRequest,
                _ => DeliveryFailure.Rejected,
            },
        };

    /// <summary>Maps a per-token topic management error reason (e.g. "registration-token-not-registered").</summary>
    /// <remarks>
    /// Only "registration-token-not-registered" marks the token for deletion. "invalid-argument" is
    /// ambiguous (malformed token, or something about the request), so the token is kept.
    /// </remarks>
    public static DeliveryFailure MapTopicReason(string reason) => reason switch
    {
        "registration-token-not-registered" => DeliveryFailure.InvalidToken,
        "invalid-argument" => DeliveryFailure.InvalidRequest,
        // "unknown-error" is also how throttling is reported, so it is treated as temporary.
        "internal-error" or "unknown-error" => DeliveryFailure.Unavailable,
        _ => DeliveryFailure.Rejected,
    };
}
