using System.Globalization;
using static Succinct.Catalog.Tests.TestData;

namespace Succinct.Catalog.Tests;

public sealed class CanonicalFormTests
{
    [Fact]
    public void Delimiters_inside_values_cannot_forge_fields()
    {
        // Under the original "path=value" lines these two records produced the same string (F5)
        string forged = CanonicalForm.Build([Attr("name", AttributeValue.FromFreeText("x\nprice=1"))]);
        string honest = CanonicalForm.Build(
            [Attr("name", AttributeValue.FromFreeText("x")), Attr("price", AttributeValue.FromCategorical("1"))]);
        Assert.NotEqual(forged, honest);
    }

    [Fact]
    public void Value_kinds_are_part_of_identity()
    {
        string asNumber = CanonicalForm.Build([Attr("code", AttributeValue.FromUInt32(5))]);
        string asText = CanonicalForm.Build([Attr("code", AttributeValue.FromCategorical("5"))]);
        Assert.NotEqual(asNumber, asText);
    }

    [Fact]
    public void Floats_keep_every_distinguishing_digit()
    {
        // "0.##" mapped both to "1"
        string a = CanonicalForm.Build([Attr("weight", AttributeValue.FromFloat64(1.004))]);
        string b = CanonicalForm.Build([Attr("weight", AttributeValue.FromFloat64(1.0))]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Canonical_form_does_not_depend_on_culture_or_attribute_order()
    {
        ProductAttributeInput[] record =
        [
            Attr("price", AttributeValue.FromFloat64(3.5)),
            Attr("name", AttributeValue.FromFreeText("x")),
            Attr("count", AttributeValue.FromInt64(-12)),
        ];
        string baseline = CanonicalForm.Build(record);
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            // Comma decimals and a different minus convention would leak into culture-sensitive formatting
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(baseline, CanonicalForm.Build(record));
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(baseline, CanonicalForm.Build(record));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
        // Attribute order in the input does not matter either
        Assert.Equal(baseline, CanonicalForm.Build([record[2], record[0], record[1]]));
    }

    [Fact]
    public void Stored_rows_and_staged_inputs_agree()
    {
        ProductUpsert upsert = Upsert(1, Attr("b", AttributeValue.FromFloat32(0.1f)), Attr("a", AttributeValue.FromCategorical("Red")));
        ProductStore store = ProductStoreBuilder.Build([upsert]);
        Assert.Equal(CanonicalForm.Build(upsert.Attributes.AsSpan()), CanonicalForm.Build(store, 0));
    }
}

public sealed class ProductSearchIndexTests
{
    [Fact]
    public void Identical_records_are_grouped_not_collapsed()
    {
        CatalogSnapshot snapshot = CatalogMerge.Apply(CatalogSnapshot.Empty, ProductDelta.ForUpserts(
            Upsert(1, Attr("color", AttributeValue.FromCategorical("Red"))),
            Upsert(2, Attr("color", AttributeValue.FromCategorical("Red"))),
            Upsert(3, Attr("color", AttributeValue.FromCategorical("Blue")))));
        ProductSearchIndex index = ProductSearchIndex.Build(snapshot);
        Assert.Equal(2, index.DistinctContentCount);
        string red = CanonicalForm.Build([Attr("color", AttributeValue.FromCategorical("Red"))]);
        // Both keys come back; the original's last-write-wins kept only one
        Assert.Equal(new long[] { 1, 2 }, index.FindIdentical(red).ToArray().Select(static k => k.Value));
        Assert.True(index.FindIdentical("no such content").IsEmpty);
        Assert.True(index.HasPrefix("5:color|5|"));
    }
}

public sealed class RecordTreeRendererTests
{
    [Fact]
    public void Groups_are_derived_from_sorted_paths()
    {
        TreeRow[] rows =
        [
            .. RecordTreeRenderer.Render(
            [
                Attr("name", AttributeValue.FromFreeText("Motor")),
                Attr("dims.width", AttributeValue.FromFloat64(1.5)),
                Attr("dims.height", AttributeValue.FromUInt32(2)),
            ]),
        ];
        TreeRow[] expected =
        [
            new(0, "dims", null),
            new(1, "height", "2"),
            new(1, "width", "1.5"),
            new(0, "name", "Motor"),
        ];
        Assert.Equal(expected, rows);
    }
}

public sealed class BufferTests
{
    [Theory]
    [InlineData(61, true)]
    [InlineData(62, true)]
    [InlineData(63, false)]
    [InlineData(64, false)]
    [InlineData(65, false)]
    public void Formatter_fails_cleanly_at_the_boundary(int categoryLength, bool fits)
    {
        // "{category}-1" needs categoryLength + 2 characters; the original threw at 64 (F8)
        string category = new('c', categoryLength);
        bool ok = PosLabelFormatter.TryFormat(category, 1, out string? label);
        Assert.Equal(fits, ok);
        if (fits)
        {
            Assert.Equal(category + "-1", label);
        }
        else
        {
            Assert.Null(label);
        }
    }

    [Fact]
    public async Task Pooled_staging_sends_exactly_the_requested_bytes()
    {
        using var destination = new MemoryStream();
        // The pool may hand back more than 10 bytes; only the 10 written ones may leave
        int sent = await PooledStaging.StageAndSendAsync(
            destination,
            10,
            static (span, value) => span.Fill(value),
            (byte)7,
            clearOnReturn: true);
        Assert.Equal(10, sent);
        Assert.Equal(Enumerable.Repeat((byte)7, 10), destination.ToArray());
    }
}
