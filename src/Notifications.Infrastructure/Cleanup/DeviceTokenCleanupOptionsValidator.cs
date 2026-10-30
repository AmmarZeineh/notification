using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Cleanup;

internal sealed class DeviceTokenCleanupOptionsValidator : IValidateOptions<DeviceTokenCleanupOptions>
{
    public ValidateOptionsResult Validate(string? name, DeviceTokenCleanupOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (options.StaleAfter < DeviceTokenCleanupOptions.MinimumStaleAfter)
        {
            return ValidateOptionsResult.Fail(
                $"'{DeviceTokenCleanupOptions.SectionName}:{nameof(DeviceTokenCleanupOptions.StaleAfter)}' must be at least " +
                $"{DeviceTokenCleanupOptions.MinimumStaleAfter.TotalDays} days.");
        }

        return options.Interval > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"'{DeviceTokenCleanupOptions.SectionName}:{nameof(DeviceTokenCleanupOptions.Interval)}' must be positive.");
    }
}
