using System.Text.RegularExpressions;

namespace Notifications.Application.Models;

public static partial class TopicName
{
    /// <summary>The character set and length FCM accepts for topic names.</summary>
    public static bool IsValid(string? topic) => topic is not null && Pattern().IsMatch(topic);

    // \z, not $: in .NET "$" also matches before a trailing newline, which would accept "news\n".
    [GeneratedRegex(@"\A[a-zA-Z0-9\-_.~%]{1,900}\z")]
    private static partial Regex Pattern();
}
