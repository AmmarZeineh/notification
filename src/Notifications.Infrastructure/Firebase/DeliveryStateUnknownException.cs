namespace Notifications.Infrastructure.Firebase;

/// <summary>
/// Wraps an unexpected exception thrown while a request to the provider was in progress (or while reading
/// its response). Some messages may already have been delivered, so the outcome is
/// <see cref="Application.Models.DeliveryFailure.Unknown"/>, never "not sent". An unexpected exception
/// thrown before the request starts is not wrapped: then nothing was sent.
/// </summary>
internal sealed class DeliveryStateUnknownException : Exception
{
    public DeliveryStateUnknownException()
    {
    }

    public DeliveryStateUnknownException(string message)
        : base(message)
    {
    }

    public DeliveryStateUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
