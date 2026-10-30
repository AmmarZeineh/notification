using System.Diagnostics;
using Notifications.Application.Common;
using Notifications.Application.Models;

namespace Notifications.Application.Services;

/// <summary>Decides which provider outcomes become an error for the caller.</summary>
internal static class DeliveryErrorPolicy
{
    /// <summary>
    /// The error for a multi-device send that delivered nothing and has no unknown outcome (so a retry can never
    /// send a duplicate). Null means the summary is more useful to the caller than an error. The rules apply in order.
    /// </summary>
    /// <param name="results">The per-device results of the send.</param>
    /// <param name="validation">The dry-run result, present only when some device reported an invalid request.</param>
    public static Error? WhenNothingDelivered(
        IReadOnlyList<TokenSendResult> results, SendResult? validation)
    {
        var failures = results.Select(r => r.Result.Failure).ToList();

        // 1. The provider is down or throttling, possibly only during the dry run (which delivers nothing,
        //    so one that got no answer is an outage too): retry everything later.
        if (validation?.Failure is DeliveryFailure.Unavailable or DeliveryFailure.Unknown ||
            failures.All(f => f == DeliveryFailure.Unavailable))
        {
            return NotificationErrors.ProviderUnavailable;
        }

        // 2. The provider rejected the message on its own too, so the message is what must change.
        if (validation is { IsSuccess: false })
        {
            var deviceReason = results.First(r => r.Result.Failure == DeliveryFailure.InvalidRequest).Result.FailureReason;
            return ForFailure(validation.Failure, validation.FailureReason ?? deviceReason);
        }

        // 3. A configuration problem (credentials, APNs key) that callers must not mistake for a partial failure.
        //    It wins even when other tokens were removed or are retryable: a retry cannot reach the rejected
        //    devices until the configuration is fixed, and the removed tokens are already gone either way.
        return failures.Contains(DeliveryFailure.Rejected) ? NotificationErrors.ProviderRejected : null;
    }

    /// <summary>The error for a single failed provider call (a topic send or a whole subscription request).</summary>
    public static Error ForFailure(DeliveryFailure? failure, string? reason) => failure switch
    {
        DeliveryFailure.InvalidToken => NotificationErrors.InvalidToken,
        DeliveryFailure.Unavailable => NotificationErrors.ProviderUnavailable,
        // Unknown outcomes are reported as unconfirmed results, never as an error: an error invites a retry.
        DeliveryFailure.Unknown => throw new UnreachableException("An unknown delivery outcome must not become an error."),
        DeliveryFailure.InvalidRequest => NotificationErrors.InvalidRequest(reason ?? "unknown"),
        _ => NotificationErrors.ProviderRejected,
    };
}
