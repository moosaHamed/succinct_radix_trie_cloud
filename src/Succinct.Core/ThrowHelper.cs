using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Succinct;

/// <summary>
/// Cold-path throws, kept out of line as in dotnet/runtime. A hot method that
/// only calls one of these stays small enough for RyuJIT to inline, and the
/// message formatting happens here, never in the caller.
/// </summary>
internal static class ThrowHelper
{
    // [DoesNotReturn] informs nullable flow analysis; [StackTraceHidden] keeps this frame out of user-facing traces
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowArgumentOutOfRange(string paramName) =>
        throw new ArgumentOutOfRangeException(paramName);

    // Argument shape error with a fixed message that never echoes the argument's content
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowArgument(string paramName, string message) =>
        throw new ArgumentException(message, paramName);

    // Object-state error: the call is valid in general but not in the current state
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowInvalidOperation(string message) =>
        throw new InvalidOperationException(message);

    // Elias-Fano's only precondition failed; the index is reported, the values are not
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowArgument_NotNonDecreasing(string paramName, int index) =>
        throw new ArgumentException($"Sequence decreases at index {index}.", paramName);

    // A malformed UTF-16 key: report its shape (length, offset), never its text
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowArgument_UnpairedSurrogate(string paramName, int length, int index) =>
        throw new ArgumentException($"Key of length {length} has an unpaired UTF-16 surrogate at index {index}.", paramName);

    // A duplicate key under the Throw policy: report the position in the input, never the key
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowArgument_DuplicateKey(string paramName, int entryIndex) =>
        throw new ArgumentException($"Entry {entryIndex} repeats an earlier key.", paramName);
}
