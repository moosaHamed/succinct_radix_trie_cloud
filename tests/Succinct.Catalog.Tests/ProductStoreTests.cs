using System.Collections.Immutable;
using System.Globalization;
using static Succinct.Catalog.Tests.TestData;

namespace Succinct.Catalog.Tests;

public sealed class ProductStoreTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(2026)]
    public void Store_round_trips_every_value(int seed)
    {
        // Random sparse records with deliberately repeated categoricals: the case F2 corrupted first
        ProductUpsert[] input = RandomUpserts(new Random(seed), count: 200);
        ProductStore store = ProductStoreBuilder.Build(input);
        Assert.Equal(input.Length, store.EntityCount);
        for (int row = 0; row < input.Length; row++)
        {
            // The stable key travels with the row
            Assert.Equal(input[row].Key, store.KeyAt(row));
            // AttributeValue equality covers tag, bits and text, so this is a bit-exact comparison
            Assert.Equal(ByPath(input[row].Attributes), ByPath(store.ToInputs(row)));
        }
    }

    [Fact]
    public void Repeated_categoricals_read_back_whole()
    {
        ProductStore store = ProductStoreBuilder.Build(
        [
            Upsert(1, Attr("color", AttributeValue.FromCategorical("Red"))),
            Upsert(2, Attr("color", AttributeValue.FromCategorical("Red"))),
            Upsert(3, Attr("color", AttributeValue.FromCategorical("Rose"))),
        ]);
        Assert.True(store.Catalog.TryGetId(AttributePath.Parse("color"), out int color));
        string[] expected = ["Red", "Red", "Rose"];
        for (int row = 0; row < expected.Length; row++)
        {
            // The original returned "" and "R" here: a list index read as a trie node id
            Assert.True(store.TryGetText(row, color, out ReadOnlySpan<char> text));
            Assert.Equal(expected[row], text.ToString());
        }
    }

    [Fact]
    public void Typed_reads_check_the_tag()
    {
        ProductStore store = ProductStoreBuilder.Build([Upsert(1, Attr("size", AttributeValue.FromUInt32(42)))]);
        Assert.True(store.Catalog.TryGetId(AttributePath.Parse("size"), out int size));
        Assert.True(store.TryGetUInt32(0, size, out uint value));
        Assert.Equal(42u, value);
        // A float read of a UInt32 slot reports false instead of reinterpreting bits
        Assert.False(store.TryGetFloat64(0, size, out _));
        Assert.Equal(ValueTag.UInt32, store.TagOf(0, size));
    }

    [Fact]
    public void Leaf_and_group_with_the_same_name_are_rejected()
    {
        // "dims" as a value and "dims.width" under it would render "dims" twice (F14)
        ArgumentException error = Assert.Throws<ArgumentException>(() => ProductStoreBuilder.Build(
            [Upsert(1, Attr("dims", AttributeValue.FromUInt32(1)), Attr("dims.width", AttributeValue.FromUInt32(2)))]));
        Assert.Contains("both as a value and as a group", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaf_group_check_ignores_names_that_merely_share_characters()
    {
        // "dimsx" is not under "dims"; '.' sorting lowest keeps the adjacent check exact
        ProductStore store = ProductStoreBuilder.Build(
            [Upsert(1, Attr("dims", AttributeValue.FromUInt32(1)), Attr("dimsx", AttributeValue.FromUInt32(2)), Attr("dims0", AttributeValue.FromUInt32(3)))]);
        Assert.Equal(1, store.EntityCount);
    }

    [Fact]
    public void Shape_violations_are_rejected()
    {
        // Duplicate path within a record
        Assert.Throws<ArgumentException>(() => ProductStoreBuilder.Build(
            [Upsert(1, Attr("a", AttributeValue.FromUInt32(1)), Attr("a", AttributeValue.FromUInt32(2)))]));
        // Duplicate key within a batch
        Assert.Throws<ArgumentException>(() => ProductStoreBuilder.Build([Upsert(1), Upsert(1)]));
        // Unset value
        Assert.Throws<ArgumentException>(() => ProductStoreBuilder.Build([Upsert(1, Attr("a", default))]));
        // Uninitialized attribute array
        Assert.Throws<ArgumentException>(() => ProductStoreBuilder.Build([new ProductUpsert(new ProductKey(1), default)]));
    }

    [Fact]
    public void Per_record_cap_is_enforced()
    {
        ProductAttributeInput[] attributes = Enumerable.Range(0, ProductStoreBuilder.MaxAttributesPerRecord + 1)
            .Select(static i => Attr("a" + i.ToString(CultureInfo.InvariantCulture), AttributeValue.FromUInt32(1)))
            .ToArray();
        Assert.Throws<ArgumentException>(() => ProductStoreBuilder.Build([Upsert(1, attributes)]));
    }

    [Fact]
    public void Row_views_are_bounded_by_their_own_row()
    {
        ProductStore store = ProductStoreBuilder.Build(
        [
            Upsert(1, Attr("a", AttributeValue.FromUInt32(1))),
            Upsert(2, Attr("b", AttributeValue.FromUInt32(2))),
        ]);
        Assert.Equal(1, store.AttributesOf(0).Length);
        // The original wrapper let index Length read the next entity's attribute (F10); a span slice cannot
        Assert.Throws<IndexOutOfRangeException>(() => store.AttributesOf(0).AttributeIds[1]);
        // EntityCount is a CSR sentinel, never a valid row
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AttributesOf(2).Length);
    }

    [Fact]
    public void Attribute_ids_are_stable_when_a_catalog_is_extended()
    {
        ProductStore first = ProductStoreBuilder.Build([Upsert(1, Attr("name", AttributeValue.FromFreeText("x")))]);
        // A new segment with new paths extends the previous catalog instead of renumbering it (F6)
        ProductStore second = ProductStoreBuilder.Build(
            [Upsert(2, Attr("color", AttributeValue.FromCategorical("Red")), Attr("name", AttributeValue.FromFreeText("y")))],
            first.Catalog);
        Assert.True(first.Catalog.TryGetId(AttributePath.Parse("name"), out int before));
        Assert.True(second.Catalog.TryGetId(AttributePath.Parse("name"), out int after));
        Assert.Equal(before, after);
        Assert.Equal("name", second.Catalog.PathOf(after));
    }

    [Fact]
    public void Unchanged_vocabulary_shares_the_catalog_instance()
    {
        ProductStore first = ProductStoreBuilder.Build([Upsert(1, Attr("name", AttributeValue.FromFreeText("x")))]);
        ProductStore second = ProductStoreBuilder.Build([Upsert(2, Attr("name", AttributeValue.FromFreeText("y")))], first.Catalog);
        Assert.Same(first.Catalog, second.Catalog);
    }

    [Fact]
    public void Empty_batch_builds_an_empty_segment()
    {
        ProductStore store = ProductStoreBuilder.Build(ReadOnlySpan<ProductUpsert>.Empty);
        Assert.Equal(0, store.EntityCount);
        Assert.Same(AttributeCatalog.Empty, store.Catalog);
    }

    [Fact]
    public void Inputs_keep_value_kinds()
    {
        ImmutableArray<ProductAttributeInput> attributes =
        [
            Attr("id", AttributeValue.FromInt64(12_345_678_950)),
            Attr("w", AttributeValue.FromFloat32(1.25f)),
        ];
        ProductStore store = ProductStoreBuilder.Build([new ProductUpsert(new ProductKey(9), attributes)]);
        // 12345678950 does not fit uint: the reason Int64 exists, and it survives intact
        Assert.Equal(ByPath(attributes), ByPath(store.ToInputs(0)));
    }
}
