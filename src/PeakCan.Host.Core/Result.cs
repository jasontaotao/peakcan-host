namespace PeakCan.Host.Core;

/// <summary>
/// Discriminated-union style success/failure envelope. <c>Value</c> is meaningful
/// only when <see cref="IsSuccess"/> is true; <see cref="Error"/> is meaningful
/// only when it is false.
/// <para>
/// Use <see cref="TryGetValue(out T)"/> for safe access without exceptions, or
/// <see cref="Match{TResult}(Func{T, TResult}, Func{Error, TResult})"/> for
/// forced branching.
/// </para>
/// </summary>
public readonly record struct Result<T>(bool IsSuccess, T? Value, Error? Error)
{
    /// <summary>Wrap a successful value.</summary>
    public static Result<T> Ok(T v) => new(true, v, null);

    /// <summary>Wrap a failure with code + message.</summary>
    public static Result<T> Fail(ErrorCode code, string message)
        => new(false, default, new Error(code, message));

    /// <summary>True iff the result carries a value. Always prefer this over null-checking <see cref="Value"/>.</summary>
    public bool TryGetValue(out T? value)
    {
        value = Value;
        return IsSuccess;
    }

    /// <summary>
    /// Force both branches of the union through caller-supplied functions.
    /// Equivalent to a <c>switch</c> on <see cref="IsSuccess"/> with no null checks.
    /// </summary>
    public TResult Match<TResult>(Func<T, TResult> onOk, Func<Error, TResult> onFail)
        => IsSuccess ? onOk(Value!) : onFail(Error!);

    /// <summary>Implicit conversion from a value — sugar for <c>Result&lt;T&gt; r = value;</c>.</summary>
    public static implicit operator Result<T>(T value) => Ok(value);
}