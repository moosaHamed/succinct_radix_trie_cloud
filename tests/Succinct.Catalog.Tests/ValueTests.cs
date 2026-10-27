namespace Succinct.Catalog.Tests;

public sealed class AttributePathTests
{
    [Theory]
    [InlineData("name", PathError.None)]
    [InlineData("dims.width", PathError.None)]
    [InlineData("a_1.b_2", PathError.None)]
    [InlineData("", PathError.Empty)]
    [InlineData("Name", PathError.DisallowedChar)]
    [InlineData("a-b", PathError.DisallowedChar)]
    [InlineData("a b", PathError.DisallowedChar)]
    [InlineData(".a", PathError.EmptySegment)]
    [InlineData("a.", PathError.EmptySegment)]
    [InlineData("a..b", PathError.EmptySegment)]
    public void Classifies_candidates(string candidate, PathError expected)
    {
        bool ok = AttributePath.TryCreate(candidate, out _, out PathError error);
        Assert.Equal(expected, error);
        Assert.Equal(expected == PathError.None, ok);
    }

    [Fact]
    public void Enforces_length_and_depth_caps()
    {
        Assert.False(AttributePath.TryCreate(new string('a', AttributePath.MaxLength + 1), out _, out PathError tooLong));
        Assert.Equal(PathError.TooLong, tooLong);
        // 33 segments of one letter: short enough, too deep
        string deep = string.Join('.', Enumerable.Repeat("a", AttributePath.MaxDepth + 1));
        Assert.False(AttributePath.TryCreate(deep, out _, out PathError tooDeep));
        Assert.Equal(PathError.TooDeep, tooDeep);
    }

    [Fact]
    public void Null_and_surrogates_are_rejected()
    {
        Assert.False(AttributePath.TryCreate(null, out _, out PathError empty));
        Assert.Equal(PathError.Empty, empty);
        // The ASCII allow-list rejects every surrogate, so no separate Unicode check is needed
        Assert.False(AttributePath.TryCreate("a\uD835", out _, out PathError surrogate));
        Assert.Equal(PathError.DisallowedChar, surrogate);
    }

    [Fact]
    public void Default_path_cannot_leak_an_unvalidated_value()
    {
        AttributePath path = default;
        Assert.Throws<InvalidOperationException>(() => path.Value);
        Assert.Equal(string.Empty, path.ToString());
    }

    [Fact]
    public void Parse_reports_the_error_code_not_the_input()
    {
        FormatException error = Assert.Throws<FormatException>(() => AttributePath.Parse("Secret-Value"));
        Assert.Contains(nameof(PathError.DisallowedChar), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Descendant_check_respects_segment_boundaries()
    {
        AttributePath dims = AttributePath.Parse("dims");
        Assert.True(AttributePath.Parse("dims.width").IsDescendantOf(dims));
        // Shares the characters but not the segment
        Assert.False(AttributePath.Parse("dimsx").IsDescendantOf(dims));
        Assert.False(dims.IsDescendantOf(dims));
    }
}

public sealed class AttributeValueTests
{
    [Fact]
    public void Negative_zero_is_normalized()
    {
        // One bit pattern per number, so bit equality is numeric equality
        Assert.Equal(AttributeValue.FromFloat64(0.0), AttributeValue.FromFloat64(-0.0));
        Assert.Equal(AttributeValue.FromFloat32(0f), AttributeValue.FromFloat32(-0f));
        Assert.Equal("0", AttributeValue.FromFloat64(-0.0).FormatInvariant());
    }

    [Fact]
    public void Non_finite_numbers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => AttributeValue.FromFloat64(double.NaN));
        Assert.Throws<ArgumentException>(() => AttributeValue.FromFloat64(double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => AttributeValue.FromFloat32(float.NegativeInfinity));
    }

    [Fact]
    public void Text_is_capped_and_validated()
    {
        Assert.Throws<ArgumentException>(() => AttributeValue.FromCategorical(new string('x', AttributeValue.MaxCategoricalLength + 1)));
        Assert.Throws<ArgumentException>(() => AttributeValue.FromFreeText("bad\uD835"));
        Assert.Throws<ArgumentNullException>(() => AttributeValue.FromFreeText(null!));
    }

    [Fact]
    public void Reads_of_another_kind_return_false()
    {
        AttributeValue value = AttributeValue.FromInt64(5);
        // A mismatched read reports false instead of reinterpreting bits
        Assert.False(value.TryGetUInt32(out _));
        Assert.False(value.TryGetFloat64(out _));
        Assert.False(value.TryGetText(out _));
        Assert.True(value.TryGetInt64(out long read));
        Assert.Equal(5, read);
    }

    [Fact]
    public void Same_digits_of_different_kinds_are_different_values()
    {
        Assert.NotEqual(AttributeValue.FromUInt32(5), AttributeValue.FromInt64(5));
        Assert.NotEqual(AttributeValue.FromInt64(5), AttributeValue.FromCategorical("5"));
        Assert.NotEqual(AttributeValue.FromCategorical("5"), AttributeValue.FromFreeText("5"));
    }

    [Fact]
    public void Default_value_is_unset()
    {
        AttributeValue value = default;
        Assert.True(value.IsUnset);
        Assert.Equal(ValueTag.Unset, value.Tag);
    }

    [Fact]
    public void Identity_format_round_trips_floats()
    {
        // "R" keeps every distinguishing digit; the original "0.##" collapsed 1.004 to "1"
        Assert.Equal("1.004", AttributeValue.FromFloat64(1.004).FormatInvariant());
        Assert.NotEqual(AttributeValue.FromFloat64(1.004).FormatInvariant(), AttributeValue.FromFloat64(1.0).FormatInvariant());
        Assert.Equal("1", AttributeValue.FromFloat64(1.004).FormatDisplay(System.Globalization.CultureInfo.InvariantCulture));
    }
}
