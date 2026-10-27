using System.Diagnostics;

namespace Succinct.Catalog;

/// <summary>
/// Zero-allocation view of one entity's attribute row. Each column is exposed as
/// a span sliced to THIS row, so the CLR bounds-checks every index against the
/// row rather than against the whole column (finding F10: the original wrapper
/// let an index past the row read the next entity's attributes).
/// </summary>
public readonly ref struct AttributeRow
{
    internal AttributeRow(ReadOnlySpan<int> attributeIds, ReadOnlySpan<ValueTag> tags, ReadOnlySpan<int> slots)
    {
        // Parallel columns must agree; a mismatch is a builder bug, caught in Debug
        Debug.Assert(attributeIds.Length == tags.Length && tags.Length == slots.Length);
        AttributeIds = attributeIds;
        Tags = tags;
        Slots = slots;
    }

    /// <summary>Attribute ids, ascending.</summary>
    public ReadOnlySpan<int> AttributeIds { get; }

    /// <summary>Value tags, parallel to <see cref="AttributeIds"/>.</summary>
    public ReadOnlySpan<ValueTag> Tags { get; }

    /// <summary>Slots into each tag's typed column, parallel to <see cref="AttributeIds"/>.</summary>
    public ReadOnlySpan<int> Slots { get; }

    /// <summary>Number of attributes in the row.</summary>
    public int Length => AttributeIds.Length;
}
