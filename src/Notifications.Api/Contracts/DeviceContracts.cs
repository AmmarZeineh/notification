using System.ComponentModel.DataAnnotations;
using Notifications.Domain.DeviceTokens;

namespace Notifications.Api.Contracts;

public sealed record RegisterDeviceRequest(
    [Required, MaxLength(DeviceToken.TokenMaxLength)] string Token,
    [Required, EnumDataType(typeof(DevicePlatform))] DevicePlatform Platform);

/// <summary>The token travels in the body, never the URL, so it does not end up in server or proxy logs.</summary>
public sealed record UnregisterDeviceRequest(
    [Required, MaxLength(DeviceToken.TokenMaxLength)] string Token);
