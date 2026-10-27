using System.Diagnostics;

namespace Succinct;

/// <summary>What <see cref="RadixTrieBuilder"/> does when two entries share a key.</summary>
public enum DuplicateKeyPolicy
{
    /// <summary>The later entry's payload replaces the earlier one's; documented, never silent.</summary>
    LastWriteWins = 0,

    /// <summary>A repeated key is rejected with an <see cref="ArgumentException"/>.</summary>
    Throw = 1,
}

/// <summary>
/// Builds an immutable <see cref="RadixTrie"/>. Mutable node objects exist only
/// inside <see cref="Build"/> and become garbage when it returns (Category 3).
/// This is the one trust boundary for trie keys: every key is validated here.
/// </summary>
public static class RadixTrieBuilder
{
    /// <summary>Longest accepted key, in UTF-16 code units: a generous ceiling, never unbounded.</summary>
    public const int MaxKeyLength = 1 << 24;

    /// <summary>Builds a trie from (key, scalar, value) entries.</summary>
    public static RadixTrie Build(
        IEnumerable<(string Key, uint Scalar, float Value)> entries,
        DuplicateKeyPolicy duplicates = DuplicateKeyPolicy.LastWriteWins)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // The root always exists, so even an empty trie has one node with an empty label
        var root = new TempNode(string.Empty);
        int entryIndex = 0;
        foreach ((string key, uint scalar, float value) in entries)
        {
            // A null key is a caller bug, reported against the collection that carried it
            ArgumentNullException.ThrowIfNull(key, nameof(entries));
            // Length cap first: it bounds every check after it
            if (key.Length > MaxKeyLength)
            {
                ThrowHelper.ThrowArgument(nameof(entries), "Key exceeds the maximum length.");
            }
            // TRUST BOUNDARY: malformed keys are rejected here, before anything is stored
            int bad = UnicodeGuard.IndexOfUnpairedSurrogate(key);
            if (bad >= 0)
            {
                ThrowHelper.ThrowArgument_UnpairedSurrogate(nameof(entries), key.Length, bad);
            }
            // Insert reports whether the key already existed, so the policy is applied, never guessed
            bool existed = Insert(root, key, scalar, value);
            if (existed && duplicates == DuplicateKeyPolicy.Throw)
            {
                ThrowHelper.ThrowArgument_DuplicateKey(nameof(entries), entryIndex);
            }
            entryIndex++;
        }
        return Freeze(root);
    }

    private static RadixTrie Freeze(TempNode root)
    {
        // Breadth-first order gives LOUDS its contiguous-children property: order[i] receives node id i + 1
        var order = new List<TempNode> { root };
        for (int i = 0; i < order.Count; i++)
        {
            // SortedDictionary yields children by ordinal first code unit, exactly what binary search assumes
            foreach (TempNode child in order[i].Children.Values)
            {
                order.Add(child);
            }
        }
        int nodeCount = order.Count;

        // Every size below is overflow-checked: a silent wrap would size an array wrongly, not throw
        checked
        {
            // 2 bits for the virtual root, one 1 per edge (n - 1 edges) and one 0 per node: 2n + 1
            int loudsLength = (2 * nodeCount) + 1;
            ulong[] loudsWords = new ulong[(loudsLength + 63) >> 6];
            // Bit cursor; zeros are written by simply skipping a position
            int bit = 0;
            // Virtual super-root "10": one child, the real root
            loudsWords[bit >> 6] |= 1UL << (bit & 63);
            bit += 2;
            foreach (TempNode node in order)
            {
                // Degree in unary
                for (int c = 0; c < node.Children.Count; c++)
                {
                    loudsWords[bit >> 6] |= 1UL << (bit & 63);
                    bit++;
                }
                // Closing 0
                bit++;
            }
            // The arithmetic above and the walk must agree exactly
            Debug.Assert(bit == loudsLength);

            // Totals for exact-size allocation
            long totalChars = 0;
            int wordCount = 0;
            foreach (TempNode node in order)
            {
                totalChars += node.Label.Length;
                if (node.IsWord)
                {
                    wordCount++;
                }
            }

            // Offsets by node id, plus slot 0 for the virtual root and a sentinel: n + 2 entries
            ulong[] rawOffsets = new ulong[nodeCount + 2];
            // RS0030 allowed here: the loop below writes every element before any read (reviewed with this file)
#pragma warning disable RS0030
            // Skipping zero-init is safe ONLY because of that proof; the suppression makes the proof reviewable
            char[] labelData = GC.AllocateUninitializedArray<char>((int)totalChars);
#pragma warning restore RS0030
            // First code unit per node id; slot 0 and the root's empty label stay '\0' and are never probed
            char[] firstChars = new char[nodeCount + 1];
            // One bit per node id, 0 through n
            ulong[] isWordBits = new ulong[(nodeCount + 1 + 63) >> 6];
            // Payload sized by word count, not node count: internal nodes cost nothing here
            uint[] scalars = new uint[wordCount];
            float[] values = new float[wordCount];

            int cursor = 0;
            int rank = 0;
            for (int i = 0; i < nodeCount; i++)
            {
                // Id 0 is the virtual super-root
                int nodeId = i + 1;
                TempNode node = order[i];
                rawOffsets[nodeId] = (ulong)cursor;
                // Writes exactly the chars this node owns, so labelData ends fully written
                node.Label.CopyTo(labelData.AsSpan(cursor));
                if (node.Label.Length > 0)
                {
                    firstChars[nodeId] = node.Label[0];
                }
                cursor += node.Label.Length;
                if (node.IsWord)
                {
                    isWordBits[nodeId >> 6] |= 1UL << (nodeId & 63);
                    // rank grows in BFS order, so Rank1(node) later reproduces exactly this slot
                    scalars[rank] = node.Scalar;
                    values[rank] = node.Value;
                    rank++;
                }
            }
            // The sentinel makes label(v) = data[offsets[v] .. offsets[v + 1]) valid for the last node too
            rawOffsets[nodeCount + 1] = (ulong)cursor;

            // The TempNode graph is unreachable after this return; only the flat arrays survive
            return new RadixTrie(
                BitVector.WrapOwned(loudsWords, loudsLength),
                new EliasFano(rawOffsets),
                labelData,
                firstChars,
                BitVector.WrapOwned(isWordBits, nodeCount + 1),
                scalars,
                values,
                nodeCount);
        }
    }

    private static bool Insert(TempNode root, string key, uint scalar, float value)
    {
        TempNode node = root;
        // Remaining, unconsumed part of the key
        ReadOnlySpan<char> rest = key;
        // Iterative, not recursive: descent depth is key length, which the caller controls
        while (true)
        {
            // The key ended exactly on a node: mark it terminal and attach the payload
            if (rest.IsEmpty)
            {
                bool existed = node.IsWord;
                node.SetPayload(scalar, value);
                return existed;
            }
            // No child starts with this code unit: the whole remaining key becomes one new edge
            if (!node.Children.TryGetValue(rest[0], out TempNode? child))
            {
                node.Children.Add(rest[0], TempNode.Leaf(rest.ToString(), scalar, value));
                return false;
            }
            // How far the key agrees with the child's label; at least 1, since the first code units match
            int common = rest.CommonPrefixLength(child.Label.AsSpan());
            // The key covers the entire label: descend and continue
            if (common == child.Label.Length)
            {
                node = child;
                rest = rest.Slice(common);
                continue;
            }
            // SPLIT: the shared head becomes a new internal node and the old child keeps the divergent tail.
            // Two well-formed keys can still split between the halves of a surrogate pair (F15); that is why
            // labels are documented as fragments and IsLabelTextSafe exists, not something validation prevents.
            var split = new TempNode(child.Label[..common]);
            child.Label = child.Label[common..];
            // Re-key the old child under its new first code unit
            split.Children.Add(child.Label[0], child);
            // The split node replaces the old child under the same first code unit
            node.Children[rest[0]] = split;
            ReadOnlySpan<char> tail = rest.Slice(common);
            // The key ended exactly at the split: the new internal node is itself terminal
            if (tail.IsEmpty)
            {
                split.SetPayload(scalar, value);
                return false;
            }
            split.Children.Add(tail[0], TempNode.Leaf(tail.ToString(), scalar, value));
            return false;
        }
    }

    /// <summary>Build-phase node: exclusive to one Build call, plain Gen0 garbage afterwards.</summary>
    private sealed class TempNode
    {
        public TempNode(string label) => Label = label;

        // Edge label from the parent; replaced, never edited, when an edge is split
        public string Label { get; set; }

        // Ordered by first code unit, which is what LOUDS sibling order and the binary search assume
        public SortedDictionary<char, TempNode> Children { get; } = new();

        public bool IsWord { get; private set; }

        public uint Scalar { get; private set; }

        public float Value { get; private set; }

        public static TempNode Leaf(string label, uint scalar, float value)
        {
            var leaf = new TempNode(label);
            leaf.SetPayload(scalar, value);
            return leaf;
        }

        public void SetPayload(uint scalar, float value)
        {
            // Last write wins at this level; Build applies the caller's policy using Insert's return value
            IsWord = true;
            Scalar = scalar;
            Value = value;
        }
    }
}
