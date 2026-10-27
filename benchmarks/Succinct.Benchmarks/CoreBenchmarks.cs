using System.Globalization;
using BenchmarkDotNet.Attributes;
using Succinct.Catalog;

namespace Succinct.Benchmarks;

/// <summary>
/// Rank and select on a LOUDS-shaped vector. Every "one instruction" claim in
/// the design is a hypothesis until measured on the CPU you deploy to: PDEP is
/// one fast instruction on Intel and on AMD Zen 3 and later, but microcoded on
/// Zen 1 and Zen 2, where Bmi2.X64.IsSupported is still true.
/// </summary>
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
public class BitVectorBenchmarks
{
    private BitVector _bits = null!;
    private int[] _ranks = null!;

    [GlobalSetup]
    public void Setup()
    {
        // One million bits at 50% density
        var random = new Random(1);
        const int Length = 1 << 20;
        ulong[] words = new ulong[Length / 64];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 32);
        }
        _bits = new BitVector(words, Length);
        // Pre-generated query ranks, so the benchmark measures select, not the random generator
        _ranks = new int[1024];
        for (int i = 0; i < _ranks.Length; i++)
        {
            _ranks[i] = random.Next(1, _bits.TotalOnes + 1);
        }
    }

    [Benchmark]
    public int Select1()
    {
        int sum = 0;
        foreach (int rank in _ranks)
        {
            sum += _bits.Select1(rank);
        }
        return sum;
    }

    [Benchmark]
    public int Select0()
    {
        int sum = 0;
        foreach (int rank in _ranks)
        {
            // Zeros and ones are balanced here, so the same ranks are valid for both
            sum += _bits.Select0(Math.Min(rank, _bits.TotalZeros));
        }
        return sum;
    }

    [Benchmark(Baseline = true)]
    public int Rank1()
    {
        int sum = 0;
        foreach (int rank in _ranks)
        {
            sum += _bits.Rank1(rank);
        }
        return sum;
    }
}

/// <summary>Trie lookups, hits and misses, against a dictionary baseline.</summary>
[MemoryDiagnoser]
public class RadixTrieBenchmarks
{
    private RadixTrie _trie = null!;
    private Dictionary<string, uint> _dictionary = null!;
    private string[] _hits = null!;
    private string[] _misses = null!;

    [Params(10_000, 200_000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Product-code-like keys with long shared prefixes, the shape a radix trie compresses well
        string[] keys = Enumerable.Range(0, KeyCount)
            .Select(static i => "sku/region-" + (i % 17).ToString(CultureInfo.InvariantCulture) + "/item-" + i.ToString("D7", CultureInfo.InvariantCulture))
            .ToArray();
        _trie = RadixTrieBuilder.Build(keys.Select(static (k, i) => (k, (uint)i, 0f)));
        _dictionary = keys.Select(static (k, i) => (k, (uint)i)).ToDictionary(static p => p.k, static p => p.Item2, StringComparer.Ordinal);
        _hits = keys.Where(static (_, i) => i % 97 == 0).ToArray();
        _misses = _hits.Select(static k => k + "x").ToArray();
    }

    [Benchmark]
    public int TrieHits()
    {
        int found = 0;
        foreach (string key in _hits)
        {
            found += _trie.TryGetPayload(key, out _, out _) ? 1 : 0;
        }
        return found;
    }

    [Benchmark]
    public int TrieMisses()
    {
        int found = 0;
        foreach (string key in _misses)
        {
            found += _trie.TryGetPayload(key, out _, out _) ? 1 : 0;
        }
        return found;
    }

    [Benchmark(Baseline = true)]
    public int DictionaryHits()
    {
        int found = 0;
        foreach (string key in _hits)
        {
            found += _dictionary.TryGetValue(key, out _) ? 1 : 0;
        }
        return found;
    }
}

/// <summary>The cost a merge pays: one segment per batch versus rebuilding everything.</summary>
[MemoryDiagnoser]
public class MergeBenchmarks
{
    private CatalogSnapshot _snapshot = null!;
    private ProductDelta _delta = null!;

    [Params(10_000)]
    public int Existing { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        AttributePath name = AttributePath.Parse("name");
        AttributePath price = AttributePath.Parse("price");
        ProductUpsert Make(long key) => new(new ProductKey(key),
        [
            new ProductAttributeInput(name, AttributeValue.FromFreeText("Item " + key.ToString(CultureInfo.InvariantCulture))),
            new ProductAttributeInput(price, AttributeValue.FromFloat64(key * 0.01)),
        ]);
        _snapshot = CatalogMerge.Apply(CatalogSnapshot.Empty,
            new ProductDelta([.. Enumerable.Range(0, Existing).Select(i => Make(i))], []));
        // A small batch of updates, the common case
        _delta = new ProductDelta([.. Enumerable.Range(0, 100).Select(i => Make(i * 37))], []);
    }

    [Benchmark]
    public CatalogSnapshot SegmentedMerge() => CatalogMerge.Apply(_snapshot, _delta);

    [Benchmark(Baseline = true)]
    public CatalogSnapshot FullRebuild() => CatalogMerge.Compact(CatalogMerge.Apply(_snapshot, _delta));
}
