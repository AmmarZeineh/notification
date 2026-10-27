using Notifications.Domain.DeviceTokens;

namespace Notifications.Application.Tests.Domain;

public sealed class DeviceTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "token", DevicePlatform.Android, DeviceTokenField.UserId)]
    [InlineData("user", " ", DevicePlatform.Android, DeviceTokenField.Token)]
    [InlineData("user", "token", (DevicePlatform)99, DeviceTokenField.Platform)]
    public void Reports_the_first_invalid_field(
        string? userId, string? token, DevicePlatform platform, DeviceTokenField expected) =>
        Assert.Equal(expected, DeviceToken.FindInvalidField(userId, token, platform));

    [Fact]
    public void Accepts_valid_values() =>
        Assert.Null(DeviceToken.FindInvalidField("user", "token", DevicePlatform.Ios));

    [Theory]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("tab\t")]
    [InlineData("non-ascii-é")]
    public void Rejects_tokens_that_cannot_be_fcm_tokens(string token) =>
        Assert.Equal(DeviceTokenField.Token, DeviceToken.FindInvalidField("user", token, DevicePlatform.Android));

    [Fact]
    public void Accepts_the_fcm_token_shape() =>
        Assert.Null(DeviceToken.FindInvalidField(
            "user", "dXk3Yz:APA91bH-x_Yq9ZtVb2Lw8sKp4RfN0mJ6uC1eG7hT3aQ5oW", DevicePlatform.Android));

    [Fact]
    public void Refresh_keeps_the_owner()
    {
        var token = DeviceToken.Create("user", "token", DevicePlatform.Android, Now);

        token.Refresh(DevicePlatform.Ios, Now.AddDays(1));

        Assert.True(token.IsOwnedBy("user"));
        Assert.Equal(DevicePlatform.Ios, token.Platform);
        Assert.Equal(Now.AddDays(1), token.LastSeenAt);
    }

    [Fact]
    public void Create_enforces_the_same_rules()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DeviceToken.Create("user", new string('x', DeviceToken.TokenMaxLength + 1), DevicePlatform.Android, Now));

        Assert.Equal("token", exception.ParamName);
    }
}
