using System.Text;

namespace Succinct.Tests;

public sealed class RadixTrieTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2026)]
    public void Trie_agrees_with_a_reference_model(int seed)
    {
        // Seeded: any failure reproduces exactly from the seed in the test name
        var random = new Random(seed);
        // Reference: slow, obviously correct, ordinal-ordered, last-write-wins like the builder's default policy
        var model = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        var entries = new List<(string Key, uint Scalar, float Value)>();
        for (uint i = 0; i < 400; i++)
        {
            string key = RandomKey(random);
            model[key] = i;
            entries.Add((key, i, i * 0.5f));
        }

        RadixTrie trie = RadixTrieBuilder.Build(entries);
        Assert.Equal(model.Count, trie.Count);

        foreach ((string key, uint expected) in model)
        {
            // Every stored key resolves to its last payload (the original threw here on small tries)
            Assert.True(trie.TryGetPayload(key, out uint actual, out _));
            Assert.Equal(expected, actual);
            // Probing past a stored key must answer, not throw (the original threw on BFS-tail nodes)
            string extended = key + "a";
            Assert.Equal(model.ContainsKey(extended), trie.Contains(extended));
            // Every stored key is its own prefix
            Assert.True(trie.StartsWith(key));
        }

        // Enumerate visits every node, so the original threw here on every trie
        Assert.Equal(model.Keys, trie.Enumerate().Select(static e => e.Key));
    }

    [Fact]
    public void Single_key_trie_answers_every_question()
    {
        // The smallest failing case of finding F1: 5 LOUDS bits and 59 padding ones
        RadixTrie trie = RadixTrieBuilder.Build([("a", 7u, 1.5f)]);
        Assert.True(trie.Contains("a"));
        Assert.False(trie.Contains("b"));
        Assert.False(trie.Contains(""));
        Assert.True(trie.TryGet("a", out TrieEntry entry));
        Assert.Equal(new TrieEntry("a", 7u, 1.5f), entry);
        Assert.Equal(new[] { "a" }, trie.Enumerate().Select(static e => e.Key));
    }

    [Fact]
    public void Prefix_of_a_word_is_not_a_word()
    {
        RadixTrie trie = RadixTrieBuilder.Build([("cart", 1u, 0f), ("care", 2u, 0f)]);
        // "car" exists only as an internal node
        Assert.False(trie.Contains("car"));
        Assert.True(trie.StartsWith("car"));
        Assert.False(trie.StartsWith("cat"));
        Assert.True(trie.StartsWith(""));
    }

    [Fact]
    public void Empty_key_marks_the_root()
    {
        RadixTrie trie = RadixTrieBuilder.Build([("", 3u, 0f), ("x", 4u, 0f)]);
        Assert.True(trie.TryGetPayload("", out uint scalar, out _));
        Assert.Equal(3u, scalar);
        Assert.True(trie.IsWord(RadixTrie.Root));
    }

    [Fact]
    public void Shared_high_surrogate_splits_a_pair_but_keys_survive()
    {
        // 𝔸 is D835 DD38 and 𝔹 is D835 DD39: two WELL-FORMED keys whose split lands mid-pair (F15)
        RadixTrie trie = RadixTrieBuilder.Build([("\U0001D538x", 1u, 0f), ("\U0001D539y", 2u, 0f)]);
        // Full keys still reconstruct valid text
        Assert.True(trie.Contains("\U0001D538x"));
        Assert.True(trie.Contains("\U0001D539y"));
        Assert.All(trie.Enumerate(), static e => Assert.True(UnicodeGuard.IsWellFormed(e.Key)));
        // But at least one label is a fragment holding half a pair, and the trie says so
        bool anyUnsafe = Enumerable.Range(1, trie.NodeCount).Any(node => !trie.IsLabelTextSafe(node));
        Assert.True(anyUnsafe);
    }

    [Fact]
    public void Malformed_keys_are_rejected_without_echoing_them()
    {
        // A lone high surrogate at index 2
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => RadixTrieBuilder.Build([("ab\uD835", 1u, 0f)]));
        Assert.Contains("index 2", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ab", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_policy_is_explicit()
    {
        (string, uint, float)[] entries = [("k", 1u, 0f), ("k", 2u, 0f)];
        // Default: documented last-write-wins
        RadixTrie trie = RadixTrieBuilder.Build(entries);
        Assert.True(trie.TryGetPayload("k", out uint scalar, out _));
        Assert.Equal(2u, scalar);
        // Opt-in: a repeated key is an error
        Assert.Throws<ArgumentException>(() => RadixTrieBuilder.Build(entries, DuplicateKeyPolicy.Throw));
    }

    [Fact]
    public void Node_ids_are_validated()
    {
        RadixTrie trie = RadixTrieBuilder.Build([("a", 1u, 0f)]);
        // Id 0 is the virtual super-root and NodeCount + 1 is past the end (F10)
        Assert.Throws<ArgumentOutOfRangeException>(() => trie.IsWord(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => trie.IsWord(trie.NodeCount + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => trie.Label(trie.NodeCount + 1).Length);
    }

    [Fact]
    public void Large_trie_finds_keys_in_the_breadth_first_tail()
    {
        // Many leaves pushes the deepest nodes to the end of BFS order, where padding broke the original
        var entries = new List<(string Key, uint Scalar, float Value)>();
        for (uint i = 0; i < 5_000; i++)
        {
            entries.Add(("item/" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), i, 0f));
        }
        RadixTrie trie = RadixTrieBuilder.Build(entries);
        foreach ((string key, uint scalar, float _) in entries)
        {
            Assert.True(trie.TryGetPayload(key, out uint actual, out _));
            Assert.Equal(scalar, actual);
            Assert.False(trie.Contains(key + "0"));
        }
    }

    private static string RandomKey(Random random)
    {
        // Tiny alphabet forces deep shared prefixes; the astral characters force surrogate splits
        string[] alphabet = ["a", "b", "ab", "é", "\U0001D538", "\U0001D539"];
        var builder = new StringBuilder();
        // Zero parts yields the empty key, which marks the root itself as terminal
        int parts = random.Next(0, 6);
        for (int i = 0; i < parts; i++)
        {
            builder.Append(alphabet[random.Next(alphabet.Length)]);
        }
        return builder.ToString();
    }
}
