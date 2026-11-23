namespace HandloomFactory.Core.Common;

/// <summary>
/// Why a use case refused. The app maps each code to a translated message, so the core never contains UI text.
/// </summary>
public enum ErrorCode
{
    None = 0,
    NotAllowed,
    NotFound,
    NameRequired,
    PinMustBeFourDigits,
    PinsDoNotMatch,
    WrongPin,
    LockedOut,
    RateInvalid,
    CountOutOfRange,
    FutureDate,
    NoRateOnDate,
    AlreadySetUp,
    PhoneInvalid,
    ImportInvalid,
    ImportTooNew,
    AlreadyPaid,
    NotPaid,
    NothingToPay,
    PaidDateInvalid,
    PinAlreadySet,
}

public sealed record Error(ErrorCode Code, string? Detail = null);

/// <summary>Success, or an <see cref="Error"/>. Expected failures (validation, wrong PIN) are results, not exceptions.</summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    public bool Ok => Error is null;

    public static Result Success() => new(null);

    public static Result Fail(ErrorCode code, string? detail = null) => new(new Error(code, detail));

    public static Result<T> Success<T>(T value) => new(value, null);

    public static Result<T> Fail<T>(ErrorCode code, string? detail = null) => new(default, new Error(code, detail));
}

public sealed class Result<T> : Result
{
    internal Result(T? value, Error? error)
        : base(error) => Value = value;

    /// <summary>The value; only meaningful when <see cref="Result.Ok"/>.</summary>
    public T? Value { get; }
}
