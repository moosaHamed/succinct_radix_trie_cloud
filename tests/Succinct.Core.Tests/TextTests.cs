using System.Text;

namespace Succinct.Tests;

public sealed class UnicodeGuardTests
{
    // TheoryData, not InlineData: attribute strings are stored as UTF-8 metadata, which would replace lone surrogates with U+FFFD
    public static TheoryData<string, int> Cases => new()
    {
        { "", -1 },
        { "plain ascii", -1 },
        { "café", -1 },
        { "\U0001D538 pair", -1 },
        { "\uD835", 0 },
        { "x\uDD38", 1 },
        { "ok\uD835x", 2 },
        { "\U0001D538\uDD38", 2 },
    };

    // Enumerated at run time too, so the runner never round-trips the lone surrogates through UTF-8 serialization
    [Theory]
    [MemberData(nameof(Cases), DisableDiscoveryEnumeration = true)]
    public void Reports_the_first_unpaired_surrogate(string text, int expected)
    {
        Assert.Equal(expected, UnicodeGuard.IndexOfUnpairedSurrogate(text));
        Assert.Equal(expected < 0, UnicodeGuard.IsWellFormed(text));
    }

    [Fact]
    public void Long_clean_text_is_well_formed()
    {
        // Exercises the vectorized path across many vector widths
        string text = new string('a', 10_000) + "\U0001D538" + new string('b', 10_000);
        Assert.True(UnicodeGuard.IsWellFormed(text));
    }
}

public sealed class StringPoolTests
{
    [Fact]
    public void Deduplicating_pool_returns_full_values_by_id()
    {
        var builder = new StringPool.Builder(deduplicate: true);
        StringId red = builder.Add("Red");
        StringId again = builder.Add("Red");
        StringId rose = builder.Add("Rose");
        StringPool pool = builder.Freeze();
        // Equal values share one id: the deduplication the original promised but never performed (F2)
        Assert.Equal(red, again);
        Assert.Equal(2, pool.Count);
        // The full value, never a trie fragment such as "R" or ""
        Assert.Equal("Red", pool.GetString(red));
        Assert.Equal("Rose", pool.GetString(rose));
    }

    [Fact]
    public void Non_deduplicating_pool_keeps_every_value()
    {
        var builder = new StringPool.Builder(deduplicate: false);
        StringId first = builder.Add("same");
        StringId second = builder.Add("same");
        StringPool pool = builder.Freeze();
        Assert.NotEqual(first, second);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Ids_are_validated_against_the_pool()
    {
        StringPool pool = new StringPool.Builder(deduplicate: true).Freeze();
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetString(new StringId(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetString(new StringId(-1)));
    }
}

public sealed class CodePointComparerTests
{
    [Fact]
    public void Supplementary_characters_sort_after_the_end_of_the_basic_plane()
    {
        string supplementary = "\U00010000";
        string replacement = "�";
        // Ordinal (UTF-16) puts U+10000 first; code-point order puts it last
        Assert.True(string.CompareOrdinal(supplementary, replacement) < 0);
        Assert.True(CodePointComparer.Instance.Compare(supplementary, replacement) > 0);
    }

    [Fact]
    public void Ordering_matches_utf8_byte_order()
    {
        string[] values = ["b", "a", "�", "\U00010000", "", "\U0001D538", "ab", "", "퟿"];
        string[] byCodePoint = [.. values.Order(CodePointComparer.Instance)];
        // UTF-8 byte order is code-point order, which is what PostgreSQL's C collation uses
        string[] byUtf8 = [.. values.Order(Comparer<string>.Create(static (x, y) =>
            Encoding.UTF8.GetBytes(x).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y))))];
        Assert.Equal(byUtf8, byCodePoint);
    }

    [Fact]
    public void Nulls_sort_first()
    {
        Assert.True(CodePointComparer.Instance.Compare(null, "a") < 0);
        Assert.True(CodePointComparer.Instance.Compare("a", null) > 0);
        Assert.Equal(0, CodePointComparer.Instance.Compare(null, null));
    }
}
