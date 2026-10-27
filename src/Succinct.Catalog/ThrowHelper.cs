using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Succinct.Catalog;

/// <summary>Cold-path throws for this assembly, kept out of line so hot callers stay inlineable.</summary>
internal static class ThrowHelper
{
    // Out-of-range index or id; the value itself is not echoed
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowArgumentOutOfRange(string paramName) =>
        throw new ArgumentOutOfRangeException(paramName);

    // Argument shape error with a fixed message that never echoes untrusted content
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowArgument(string paramName, string message) =>
        throw new ArgumentException(message, paramName);

    // Valid call, invalid state (a cap reached, an invariant broken)
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowInvalidOperation(string message) =>
        throw new InvalidOperationException(message);
}
