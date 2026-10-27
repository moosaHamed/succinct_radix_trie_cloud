using System.Globalization;

namespace Succinct.Catalog;

/// <summary>
/// The stable identity of a product, carried from the database to the API.
/// A position inside a generation is not an identity (findings F6 and F12);
/// this is. Its own type, so it cannot be confused with a row or attribute id.
/// </summary>
public readonly record struct ProductKey(long Value) : IComparable<ProductKey>
{
    /// <inheritdoc />
    public int CompareTo(ProductKey other) =>
        // Ordered by the underlying number, so keyset paging is a plain range scan
        Value.CompareTo(other.Value);

    /// <summary>Less than, by key value.</summary>
    public static bool operator <(ProductKey left, ProductKey right) => left.Value < right.Value;

    /// <summary>Greater than, by key value.</summary>
    public static bool operator >(ProductKey left, ProductKey right) => left.Value > right.Value;

    /// <summary>Less than or equal, by key value.</summary>
    public static bool operator <=(ProductKey left, ProductKey right) => left.Value <= right.Value;

    /// <summary>Greater than or equal, by key value.</summary>
    public static bool operator >=(ProductKey left, ProductKey right) => left.Value >= right.Value;

    /// <summary>The key as invariant digits.</summary>
    public override string ToString() =>
        // Invariant: identity text must not depend on the server's culture
        Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Where a live record sits inside ONE <see cref="CatalogSnapshot"/>: a segment
/// index and a row. Meaningless in any other generation, which is why only the
/// snapshot that issued it ever dereferences it.
/// </summary>
public readonly record struct RecordLocator(int Segment, int Row);
