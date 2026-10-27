using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Succinct.Catalog;

/// <summary>
/// Category 1 ownership: exclusive, scoped, never escaping. The stack owns the
/// buffer, and the method's return is the provable "everyone is done" moment.
/// </summary>
public static class PosLabelFormatter
{
    /// <summary>Longest label produced; a constant, so the stack cost is fixed whatever the input.</summary>
    public const int MaxLength = 64;

    /// <summary>Formats "{category}-{index}"; false, with no exception, when it would not fit.</summary>
    public static bool TryFormat(ReadOnlySpan<char> category, int index, [NotNullWhen(true)] out string? result)
    {
        // Constant-sized stack buffer, never input-sized: a caller-sized stackalloc is a stack-overflow primitive
        Span<char> buffer = stackalloc char[MaxLength];
        // TryWrite checks every segment against the remaining space: a 64-char category fails cleanly (finding F8)
        if (buffer.TryWrite(CultureInfo.InvariantCulture, $"{category}-{index}", out int written))
        {
            // The single allocation, made only after formatting succeeded; the span itself cannot escape
            result = new string(buffer[..written]);
            return true;
        }
        // Explicit failure instead of string.Empty, which a caller cannot tell from a real empty label
        result = null;
        return false;
    }
}

/// <summary>
/// Category 2 ownership: exclusive and scoped, but the scope crosses an await,
/// so the stack cannot own the buffer. The lease type states the release
/// protocol that a bare rented array cannot.
/// </summary>
public static class PooledStaging
{
    /// <summary>
    /// Rents <paramref name="byteCount"/> bytes, lets <paramref name="fill"/> write them, sends exactly
    /// those bytes, and returns the lease. With <paramref name="clearOnReturn"/>, the bytes are wiped
    /// first, because the next renter could be any code in the process.
    /// </summary>
    public static async Task<int> StageAndSendAsync<TState>(
        Stream destination,
        int byteCount,
        SpanAction<byte, TState> fill,
        TState state,
        bool clearOnReturn = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        // The lease, not a byte[]: ownership and its release are in the type
        IMemoryOwner<byte> lease = MemoryPool<byte>.Shared.Rent(byteCount);
        try
        {
            // Rent may hand back MORE than requested, holding a previous renter's bytes: slice to the exact length
            Memory<byte> buffer = lease.Memory[..byteCount];
            // Synchronous, span-scoped fill, entirely before the await
            fill(buffer.Span, state);
            // Memory<T>, not Span<T>, because this await outlives the stack frame's span
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return byteCount;
        }
        finally
        {
            // Wipe before returning when the bytes were sensitive
            if (clearOnReturn)
            {
                lease.Memory.Span.Clear();
            }
            // The provable "everyone is done" moment pooling requires
            lease.Dispose();
        }
    }
}
