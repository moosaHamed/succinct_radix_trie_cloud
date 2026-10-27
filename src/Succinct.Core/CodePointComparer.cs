namespace Succinct;

/// <summary>
/// Orders strings by Unicode code point, matching UTF-8 byte order (and so
/// PostgreSQL's C collation). <see cref="StringComparer.Ordinal"/> orders by
/// UTF-16 code unit instead, which puts U+10000 before U+FFFD; keyset
/// pagination that sorts in one system and filters in the other skips rows.
/// </summary>
public sealed class CodePointComparer : IComparer<string>
{
    /// <summary>The stateless shared instance.</summary>
    public static readonly CodePointComparer Instance = new();

    // Stateless: one shared instance is enough
    private CodePointComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        // Same reference, or both null
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        // Null sorts first, matching StringComparer.Ordinal
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }
        // Vectorized scan to the first differing code unit
        int i = x.AsSpan().CommonPrefixLength(y.AsSpan());
        // One string is a prefix of the other: the shorter sorts first
        if (i == x.Length || i == y.Length)
        {
            return x.Length - y.Length;
        }
        // Only the first difference decides, so only it needs the fix-up
        return Fixup(x[i]) - Fixup(y[i]);
    }

    // Lifts surrogates (D800-DFFF) above U+E000-U+FFFF, which turns UTF-16 order into code-point order
    private static int Fixup(char c) =>
        c < 0xD800 ? c : c >= 0xE000 ? c - 0x800 : c + 0x2000;
}
