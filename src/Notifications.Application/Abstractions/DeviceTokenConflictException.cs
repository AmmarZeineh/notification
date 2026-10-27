namespace Notifications.Application.Abstractions;

/// <summary>
/// Thrown by <see cref="IDeviceTokenRepository.SaveChangesAsync"/> when a concurrent request changed the
/// same token first: it inserted a token being added, or deleted one being updated.
/// The rejected changes are discarded, so the operation can be retried from a fresh read.
/// </summary>
public sealed class DeviceTokenConflictException : Exception
{
    public DeviceTokenConflictException()
        : base("The device token was changed by a concurrent request.")
    {
    }

    public DeviceTokenConflictException(string message)
        : base(message)
    {
    }

    public DeviceTokenConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
