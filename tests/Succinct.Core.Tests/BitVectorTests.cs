namespace Succinct.Tests;

public sealed class BitVectorTests
{
    // Lengths straddling every word-boundary case: empty, inside one word, exactly full, one past, many words
    public static TheoryData<int> Lengths => new() { 0, 1, 5, 63, 64, 65, 127, 128, 1000, 5000 };

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Totals_exclude_tail_padding(int length)
    {
        // Every real bit set: the original counted padding ones on top of these (F1)
        bool[] bits = Enumerable.Repeat(true, length).ToArray();
        BitVector vector = Build(bits);
        Assert.Equal(length, vector.TotalOnes);
        Assert.Equal(0, vector.TotalZeros);
    }

    [Fact]
    public void Five_bit_louds_vector_selects_every_zero()
    {
        // The LOUDS bits of a trie holding only "a": 10 10 0. The original reported TotalZeros = -56 here
        BitVector vector = Build([true, false, true, false, false]);
        Assert.Equal(3, vector.TotalZeros);
        Assert.Equal(1, vector.Select0(1));
        Assert.Equal(3, vector.Select0(2));
        Assert.Equal(4, vector.Select0(3));
    }

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(2, 0.05)]
    [InlineData(3, 0.95)]
    [InlineData(4, 0.5)]
    public void Rank_and_select_agree_with_a_naive_model(int seed, double density)
    {
        // Large enough to cross several sample groups (one per 512 matching bits)
        bool[] bits = RandomBits(new Random(seed), 20_000, density);
        BitVector vector = Build(bits);
        int ones = 0;
        int zeros = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            // Rank before position i equals the running count
            Assert.Equal(ones, vector.Rank1(i));
            Assert.Equal(zeros, vector.Rank0(i));
            Assert.Equal(bits[i], vector[i]);
            if (bits[i])
            {
                ones++;
                // The k-th one is exactly here
                Assert.Equal(i, vector.Select1(ones));
            }
            else
            {
                zeros++;
                Assert.Equal(i, vector.Select0(zeros));
            }
        }
        // Rank at Length covers the whole vector
        Assert.Equal(ones, vector.Rank1(bits.Length));
        Assert.Equal(ones, vector.TotalOnes);
        Assert.Equal(zeros, vector.TotalZeros);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Next_set_and_clear_bits_agree_with_a_naive_scan(int seed)
    {
        bool[] bits = RandomBits(new Random(seed), 777, 0.3);
        BitVector vector = Build(bits);
        for (int pos = 0; pos <= bits.Length; pos++)
        {
            Assert.Equal(NaiveNext(bits, pos, true), vector.NextSetBit(pos));
            Assert.Equal(NaiveNext(bits, pos, false), vector.NextClearBit(pos));
        }
    }

    [Fact]
    public void Constructor_copies_its_input()
    {
        ulong[] words = [0b1011UL];
        var vector = new BitVector(words, 4);
        // Mutating the caller's array afterwards must not reach the vector or desynchronize its caches (F7)
        words[0] = 0;
        Assert.Equal(3, vector.TotalOnes);
        Assert.True(vector[0]);
        Assert.Equal(3, vector.Select1(3));
    }

    [Fact]
    public void Indexer_rejects_padding_positions()
    {
        BitVector vector = Build([true, true, true]);
        // Position 3 lies in padding, which the original read as a 1 (F10)
        Assert.Throws<ArgumentOutOfRangeException>(() => vector[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => vector[-1]);
    }

    [Fact]
    public void Select_rejects_ranks_outside_the_population()
    {
        BitVector vector = Build([true, false, true]);
        Assert.Throws<ArgumentOutOfRangeException>(() => vector.Select1(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => vector.Select1(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => vector.Select0(2));
    }

    [Fact]
    public void Constructor_rejects_a_word_count_that_does_not_match_the_length()
    {
        Assert.Throws<ArgumentException>(() => new BitVector(new ulong[2], 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitVector([], -1));
    }

    internal static BitVector Build(bool[] bits)
    {
        // Pack the booleans into words exactly as the builders in the library do
        ulong[] words = new ulong[(bits.Length + 63) / 64];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                words[i >> 6] |= 1UL << (i & 63);
            }
        }
        return new BitVector(words, bits.Length);
    }

    internal static bool[] RandomBits(Random random, int length, double density)
    {
        bool[] bits = new bool[length];
        for (int i = 0; i < length; i++)
        {
            bits[i] = random.NextDouble() < density;
        }
        return bits;
    }

    private static int NaiveNext(bool[] bits, int pos, bool wanted)
    {
        for (int i = pos; i < bits.Length; i++)
        {
            if (bits[i] == wanted)
            {
                return i;
            }
        }
        return -1;
    }
}
