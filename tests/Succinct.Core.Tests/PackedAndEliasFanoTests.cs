namespace Succinct.Tests;

public sealed class PackedUIntArrayTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    public void Values_round_trip_at_every_width(int width)
    {
        var random = new Random(width);
        const int Count = 300;
        // Largest value of this width; 64 bits uses the full range
        ulong max = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        ulong[] expected = new ulong[Count];
        var builder = new PackedUIntArray.Builder(Count, width);
        for (int i = 0; i < Count; i++)
        {
            // Random 64 bits masked to the width; straddling positions are covered by the odd widths
            expected[i] = (ulong)random.NextInt64() & max;
            builder[i] = expected[i];
        }
        PackedUIntArray packed = builder.Freeze();
        for (int i = 0; i < Count; i++)
        {
            Assert.Equal(expected[i], packed[i]);
        }
    }

    [Fact]
    public void Too_wide_values_are_rejected_not_masked()
    {
        var builder = new PackedUIntArray.Builder(4, 3);
        // 8 needs 4 bits; masking it to 0 would be silent truncation
        Assert.Throws<ArgumentOutOfRangeException>(() => builder[0] = 8);
    }

    [Fact]
    public void Indexes_past_the_logical_length_are_rejected()
    {
        var builder = new PackedUIntArray.Builder(3, 5);
        PackedUIntArray packed = builder.Freeze();
        // Index 3 still lies inside the first word's slack, which the original returned as data (F10)
        Assert.Throws<ArgumentOutOfRangeException>(() => packed[3]);
    }

    [Fact]
    public void A_frozen_builder_cannot_write_again()
    {
        var builder = new PackedUIntArray.Builder(2, 4);
        builder.Freeze();
        // Ownership moved to the frozen array; a later write would mutate published state
        Assert.Throws<InvalidOperationException>(() => builder[0] = 1);
        Assert.Throws<InvalidOperationException>(() => builder.Freeze());
    }
}

public sealed class EliasFanoTests
{
    public static TheoryData<ulong[]> Sequences => new()
    {
        Array.Empty<ulong>(),
        new ulong[] { 0 },
        new ulong[] { 0, 0, 0 },
        new ulong[] { 5, 5, 9, 9, 9, 100 },
        new ulong[] { 0, 0, 3, 5, 5, 9, 14, 20, 21, 27, 30 },
        new ulong[] { 1, ulong.MaxValue / 2, ulong.MaxValue },
    };

    [Theory]
    [MemberData(nameof(Sequences))]
    public void Fixed_sequences_round_trip(ulong[] values)
    {
        var encoded = new EliasFano(values);
        Assert.Equal(values.Length, encoded.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], encoded[i]);
        }
    }

    [Theory]
    [InlineData(1, 1_000, 10)]
    [InlineData(2, 5_000, 1_000_000)]
    [InlineData(3, 2_000, 1)]
    public void Random_sorted_sequences_round_trip_and_adjacent_pairs_match(int seed, int count, int maxStep)
    {
        var random = new Random(seed);
        ulong[] values = new ulong[count];
        ulong current = 0;
        for (int i = 0; i < count; i++)
        {
            // Non-decreasing by construction, with repeats whenever the step is 0
            current += (ulong)random.Next(0, maxStep + 1);
            values[i] = current;
        }
        var encoded = new EliasFano(values);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(values[i], encoded[i]);
        }
        for (int i = 0; i < count - 1; i++)
        {
            // One select plus one scan must agree with two independent selects
            Assert.Equal((values[i], values[i + 1]), encoded.GetAdjacent(i));
        }
    }

    [Fact]
    public void Unsorted_input_is_rejected_with_its_index()
    {
        // The original constructor trusted this precondition and decoded garbage (F7)
        ArgumentException error = Assert.Throws<ArgumentException>(() => new EliasFano([0, 3, 9, 5, 14]));
        Assert.Contains("index 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adjacent_pairs_need_two_elements()
    {
        var encoded = new EliasFano([4]);
        // Length 1 makes the bound Length - 1 = 0, where the (uint) idiom would wrongly accept index 0
        Assert.Throws<ArgumentOutOfRangeException>(() => encoded.GetAdjacent(0));
        var empty = new EliasFano([]);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.GetAdjacent(0));
    }
}
