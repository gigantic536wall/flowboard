namespace Flowboard.Core.Services;

public enum ResultStatus
{
    Ok,
    NotFound,
    Forbidden,
    Invalid,

    /// <summary>Someone else changed the item first; reload and try again.</summary>
    Conflict,
}

/// <summary>Outcome of a board operation. Failures carry a message that can be shown to the user.</summary>
public readonly record struct Result<T>(ResultStatus Status, T? Value, string? Error)
{
    public bool IsOk => Status == ResultStatus.Ok;

    public static Result<T> Ok(T value) => new(ResultStatus.Ok, value, null);

    public static implicit operator Result<T>(Failure failure) => new(failure.Status, default, failure.Message);
}

/// <summary>A failed result of any type: <c>return Fail.NotFound("Card");</c></summary>
public readonly record struct Failure(ResultStatus Status, string Message);

public static class Fail
{
    public static Failure NotFound(string what) => new(ResultStatus.NotFound, $"{what} not found.");

    public static Failure Forbidden(string message = "You do not have permission to do that.") => new(ResultStatus.Forbidden, message);

    public static Failure Invalid(string message) => new(ResultStatus.Invalid, message);

    public static Failure Conflict(string message) => new(ResultStatus.Conflict, message);
}

/// <summary>Value for operations that return nothing on success.</summary>
public readonly record struct Done;
