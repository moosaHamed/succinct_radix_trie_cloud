using System.Diagnostics;

namespace Succinct;

/// <summary>
/// A compact radix (Patricia) trie: topology is a LOUDS <see cref="BitVector"/>,
/// edge labels live in an Elias-Fano-compressed CSR layout, and every complete
/// key carries a (<see cref="uint"/>, <see cref="float"/>) payload indexed by
/// word rank, so payload memory tracks the number of keys, not nodes.
/// </summary>
/// <remarks>
/// <para>Safety: no pointers and no unsafe code; every access is a bounds-checked
/// array or bit-vector read; all state is private and immutable once built; read
/// APIs take <see cref="ReadOnlySpan{T}"/>, so lookups allocate nothing.</para>
/// <para>Keys are UTF-16-validated at <see cref="RadixTrieBuilder"/>. Labels are
/// split at UTF-16 code units, so a single node's <see cref="Label"/> is a
/// fragment and may hold half of a surrogate pair; full keys always reconstruct
/// valid text. Ask <see cref="IsLabelTextSafe"/> before treating one label as text.</para>
/// <para>Enumeration order is ordinal (UTF-16 code unit) order. That differs from
/// code-point order for supplementary characters; see <c>CodePointComparer</c>.</para>
/// </remarks>
public sealed class RadixTrie
{
    /// <summary>Node id of the real root. Id 0 is the virtual LOUDS super-root.</summary>
    public const int Root = 1;

    // 2n + 1 bits: the virtual root's "10", then each node's degree in unary closed by a 0
    private readonly BitVector _louds;
    // label(v) = _labelData[offsets[v] .. offsets[v + 1]); n + 2 entries including the sentinel
    private readonly EliasFano _labelOffsets;
    // Every edge label, concatenated in BFS order
    private readonly char[] _labelData;
    // First code unit of each node's label by node id: 2 bytes per node buy a select-free binary-search probe
    private readonly char[] _firstChars;
    // One bit per node id; Rank1 numbers the terminals, so it doubles as the payload index
    private readonly BitVector _isWord;
    // Payload, indexed by word rank
    private readonly uint[] _scalars;
    private readonly float[] _values;

    // Internal: only RadixTrieBuilder can produce a mutually consistent set of these arrays
    internal RadixTrie(BitVector louds, EliasFano labelOffsets, char[] labelData, char[] firstChars,
        BitVector isWord, uint[] scalars, float[] values, int nodeCount)
    {
        _louds = louds;
        _labelOffsets = labelOffsets;
        _labelData = labelData;
        _firstChars = firstChars;
        _isWord = isWord;
        _scalars = scalars;
        _values = values;
        NodeCount = nodeCount;
    }

    /// <summary>Number of real nodes; valid node ids are 1 through <see cref="NodeCount"/>.</summary>
    public int NodeCount { get; }

    /// <summary>Number of stored keys.</summary>
    public int Count => _scalars.Length;

    /// <summary>
    /// Raw UTF-16 code units of this node's edge label. A fragment of a key,
    /// not guaranteed to be well-formed text on its own.
    /// </summary>
    public ReadOnlySpan<char> Label(int node)
    {
        // Public entry: the id is validated against this trie's id space (F10)
        ThrowIfInvalidNode(node);
        return GetLabel(node);
    }

    /// <summary>True when this node's label can be treated as standalone text.</summary>
    public bool IsLabelTextSafe(int node)
    {
        ThrowIfInvalidNode(node);
        ReadOnlySpan<char> label = GetLabel(node);
        // A torn pair shows up only at the edges of a fragment, because keys were validated whole
        return !UnicodeGuard.StartsWithUnpairedLowSurrogate(label)
            && !UnicodeGuard.EndsWithUnpairedHighSurrogate(label);
    }

    /// <summary>True when a stored key ends exactly at <paramref name="node"/>.</summary>
    public bool IsWord(int node)
    {
        ThrowIfInvalidNode(node);
        return _isWord[node];
    }

    /// <summary>True when <paramref name="key"/> is stored.</summary>
    public bool Contains(ReadOnlySpan<char> key)
    {
        int node = FindExactNode(key);
        // Landing on a node is not enough: it must be terminal, or "car" would match merely because "cart" is stored
        return node > 0 && _isWord[node];
    }

    /// <summary>Allocation-free payload lookup.</summary>
    public bool TryGetPayload(ReadOnlySpan<char> key, out uint scalar, out float value)
    {
        int node = FindExactNode(key);
        if (node > 0 && _isWord[node])
        {
            // Terminals strictly before this node in BFS order = this key's payload slot
            int rank = _isWord.Rank1(node);
            scalar = _scalars[rank];
            value = _values[rank];
            return true;
        }
        // Try-pattern contract: outputs are always definitely assigned
        scalar = 0;
        value = 0f;
        return false;
    }

    /// <summary>Retrieves (key, scalar, value) as one record; allocates the key string.</summary>
    public bool TryGet(ReadOnlySpan<char> key, out TrieEntry entry)
    {
        if (TryGetPayload(key, out uint scalar, out float value))
        {
            // The one allocation: the caller asked for an owned key
            entry = new TrieEntry(key.ToString(), scalar, value);
            return true;
        }
        entry = default;
        return false;
    }

    /// <summary>True when at least one stored key starts with <paramref name="prefix"/>.</summary>
    public bool StartsWith(ReadOnlySpan<char> prefix)
    {
        int node = Root;
        while (!prefix.IsEmpty)
        {
            // One code unit identifies the child: sibling labels differ in their first code unit by construction
            int child = FindChild(node, prefix[0]);
            if (child < 0)
            {
                return false;
            }
            ReadOnlySpan<char> label = GetLabel(child);
            // Vectorized comparison inside the BCL
            int common = prefix.CommonPrefixLength(label);
            // The prefix ran out inside this edge: every key below matches
            if (common == prefix.Length)
            {
                return true;
            }
            // The prefix diverged mid-edge: nothing below can match
            if (common < label.Length)
            {
                return false;
            }
            // Re-aim the span past the consumed edge; no copy
            prefix = prefix.Slice(common);
            node = child;
        }
        // The empty prefix matches whenever the trie exists
        return true;
    }

    /// <summary>Every stored key with its payload, in ordinal key order.</summary>
    public IEnumerable<TrieEntry> Enumerate()
    {
        // One reusable buffer for the current path; grown, never re-allocated per node
        char[] buffer = new char[64];
        // Explicit stack, not recursion: depth is bounded by the heap, not by the thread's stack
        var stack = new Stack<(int Node, int PrefixLength)>();
        stack.Push((Root, 0));
        while (stack.Count > 0)
        {
            (int node, int prefixLength) = stack.Pop();
            // The span work lives in a non-iterator helper, so no span is held across a yield (F19)
            int length = AppendLabel(node, ref buffer, prefixLength);
            if (_isWord[node])
            {
                int rank = _isWord.Rank1(node);
                // The deliberate allocation: whole keys cannot be spans into a buffer that keeps changing
                yield return new TrieEntry(new string(buffer, 0, length), _scalars[rank], _values[rank]);
            }
            (int firstChild, int degree) = ChildRange(node);
            // Push in reverse so pops come out in ascending order: ordinal key order for free
            for (int c = degree - 1; c >= 0; c--)
            {
                stack.Push((firstChild + c, length));
            }
        }
    }

    private int AppendLabel(int node, ref char[] buffer, int prefixLength)
    {
        ReadOnlySpan<char> label = GetLabel(node);
        // checked: path length is bounded by key length, but say so explicitly
        int length = checked(prefixLength + label.Length);
        if (length > buffer.Length)
        {
            // Doubling growth in long math, clamped to the largest array the runtime allows
            long grown = Math.Max(length, Math.Min((long)buffer.Length * 2, Array.MaxLength));
            Array.Resize(ref buffer, (int)grown);
        }
        // Overwrite from prefixLength on; the shared prefix stays in place from the parent's visit
        label.CopyTo(buffer.AsSpan(prefixLength));
        return length;
    }

    private int FindExactNode(ReadOnlySpan<char> key)
    {
        int node = Root;
        while (!key.IsEmpty)
        {
            int child = FindChild(node, key[0]);
            if (child < 0)
            {
                return -1;
            }
            ReadOnlySpan<char> label = GetLabel(child);
            int common = key.CommonPrefixLength(label);
            // An exact match must consume the whole edge label; a partial consume means the key diverges inside it
            if (common < label.Length)
            {
                return -1;
            }
            key = key.Slice(common);
            node = child;
        }
        return node;
    }

    private (int FirstChild, int Degree) ChildRange(int v)
    {
        // v's child run begins right after the v-th 0, the 0 that closes node v - 1
        int start = _louds.Select0(v) + 1;
        // The run is all 1s up to the next 0: one TZCNT in the usual case, not a second select
        int end = _louds.NextClearBit(start);
        // Every node's run is closed by a 0, so the scan always finds one
        Debug.Assert(end >= start);
        // Exactly v zeros precede start, so start - v ones do: FirstChildId without Rank1
        return (start - v + 1, end - start);
    }

    private int FindChild(int node, char firstChar)
    {
        // One select plus one scan replaces three selects and a rank per level
        (int lo, int degree) = ChildRange(node);
        // Children occupy the contiguous id range [lo, lo + degree)
        int hi = lo + degree - 1;
        while (lo <= hi)
        {
            // Overflow-free midpoint
            int mid = lo + ((hi - lo) >> 1);
            // Plain array read instead of an Elias-Fano decode on every probe
            char c = _firstChars[mid];
            if (c == firstChar)
            {
                return mid;
            }
            if (c < firstChar)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return -1;
    }

    private ReadOnlySpan<char> GetLabel(int node)
    {
        // One select decodes both ends of the label
        (ulong start, ulong end) = _labelOffsets.GetAdjacent(node);
        // Offsets index a char[], so both fit in int by construction; AsSpan is a bounds-checked view, not a copy
        return _labelData.AsSpan((int)start, (int)(end - start));
    }

    private void ThrowIfInvalidNode(int node)
    {
        // Node ids are dense in [1, NodeCount]; one unsigned compare validates both ends
        if ((uint)(node - 1) >= (uint)NodeCount)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(node));
        }
    }
}

/// <summary>One (key, scalar, value) record read from a <see cref="RadixTrie"/>.</summary>
public readonly record struct TrieEntry(string Key, uint Scalar, float Value);
