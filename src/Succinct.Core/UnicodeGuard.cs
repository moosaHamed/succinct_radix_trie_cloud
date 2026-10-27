namespace Succinct;

/// <summary>
/// The one trust-boundary gate for UTF-16 well-formedness: trie keys, attribute
/// paths, interned categoricals and free text all pass through it once, where
/// they enter. Vectorized through the BCL's range search; no unsafe code.
/// </summary>
public static class UnicodeGuard
{
    // First UTF-16 surrogate code unit (high surrogates are D800-DBFF)
    private const char SurrogateStart = '\uD800';
    // Last UTF-16 surrogate code unit (low surrogates are DC00-DFFF)
    private const char SurrogateEnd = '\uDFFF';

    /// <summary>True when every high surrogate is immediately followed by a low one, and no low surrogate stands alone.</summary>
    public static bool IsWellFormed(ReadOnlySpan<char> text) =>
        // One definition of "malformed", shared with the index-reporting overload
        IndexOfUnpairedSurrogate(text) < 0;

    /// <summary>Index of the first unpaired surrogate, or -1 when the text is well formed.</summary>
    public static int IndexOfUnpairedSurrogate(ReadOnlySpan<char> text)
    {
        // Vector128/256/512 scan inside the BCL: surrogate-free text returns -1 after one pass
        int i = text.IndexOfAnyInRange(SurrogateStart, SurrogateEnd);
        // Scalar work happens only where surrogates actually are
        while (i >= 0)
        {
            // A low surrogate first, a high surrogate at the end, or a high surrogate without a low partner is unpaired
            if (!char.IsHighSurrogate(text[i]) || (uint)(i + 1) >= (uint)text.Length || !char.IsLowSurrogate(text[i + 1]))
            {
                return i;
            }
            // Skip the validated pair and resume the vectorized scan on what remains (an empty slice is legal)
            int next = text.Slice(i + 2).IndexOfAnyInRange(SurrogateStart, SurrogateEnd);
            // Translate the slice-relative hit back to an absolute index, or finish
            i = next < 0 ? -1 : i + 2 + next;
        }
        // No offending code unit anywhere
        return -1;
    }

    /// <summary>Last code unit is a high surrogate whose low partner lies outside this span.</summary>
    public static bool EndsWithUnpairedHighSurrogate(ReadOnlySpan<char> text) =>
        // The length test guards the empty span; ^1 is the from-end index
        text.Length > 0 && char.IsHighSurrogate(text[^1]);

    /// <summary>First code unit is a low surrogate whose high partner lies outside this span.</summary>
    public static bool StartsWithUnpairedLowSurrogate(ReadOnlySpan<char> text) =>
        // The length test guards the empty span
        text.Length > 0 && char.IsLowSurrogate(text[0]);
}
