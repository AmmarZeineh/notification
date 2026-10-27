using System.Diagnostics.CodeAnalysis;

namespace Notifications.Application.Common;

public enum ErrorType
{
    Validation = 1,
    NotFound = 2,
    Forbidden = 3,
    Unavailable = 4,
    Failure = 5,
    Conflict = 6,
}

/// <summary>An expected, non-exceptional failure that callers are meant to handle.</summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Conventional Result-pattern name; the codebase is C# only.")]
public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
}
