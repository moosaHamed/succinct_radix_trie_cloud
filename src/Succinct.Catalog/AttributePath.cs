using System.Buffers;

namespace Succinct.Catalog;

/// <summary>Why a candidate attribute path was rejected. Codes, not messages: hostile input is never echoed.</summary>
public enum PathError
{
    /// <summary>The path is valid.</summary>
    None = 0,

    /// <summary>Null or empty.</summary>
    Empty,

    /// <summary>Longer than <see cref="AttributePath.MaxLength"/>.</summary>
    TooLong,

    /// <summary>A character outside a-z, 0-9, '.' and '_'.</summary>
    DisallowedChar,

    /// <summary>A leading, trailing or doubled '.'.</summary>
    EmptySegment,

    /// <summary>More than <see cref="AttributePath.MaxDepth"/> segments.</summary>
    TooDeep,
}

/// <summary>
/// An attribute path that has passed validation; holding one IS the proof.
/// Paths are dot-separated segments of lowercase ASCII letters, digits and
/// underscores. That narrow alphabet is what makes ordinal sorting, segment
/// splitting and the leaf-versus-group check unambiguous downstream.
/// </summary>
public readonly struct AttributePath : IEquatable<AttributePath>
{
    /// <summary>Longest accepted path, in characters.</summary>
    public const int MaxLength = 256;

    /// <summary>Most segments a path may have; bounds the renderer's group stack.</summary>
    public const int MaxDepth = 32;

    // Vectorized character set; being pure ASCII, it also rejects every surrogate, which subsumes the Unicode check
    private static readonly SearchValues<char> s_allowed =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789._");

    // Null only in default(AttributePath), the one unvalidated state
    private readonly string? _value;

    // Private: the only ways in are TryCreate, Parse and the trusted internal factory
    private AttributePath(string value) => _value = value;

    /// <summary>The path text. Throws for <c>default(AttributePath)</c> instead of leaking an unvalidated null.</summary>
    public string Value => _value ?? throw new InvalidOperationException("Uninitialized AttributePath.");

    /// <summary>Number of '.'-separated segments.</summary>
    public int Depth => Value.AsSpan().Count('.') + 1;

    /// <summary>Validates <paramref name="candidate"/>; on failure <paramref name="error"/> says why.</summary>
    public static bool TryCreate(string? candidate, out AttributePath path, out PathError error)
    {
        // Definite assignment for the failure path
        path = default;
        // All rules live in one classifier
        error = Classify(candidate);
        if (error != PathError.None)
        {
            return false;
        }
        // Classify returned None, so candidate is non-null
        path = new AttributePath(candidate!);
        return true;
    }

    /// <summary>For literals and trusted wire data: throws with the error code, never the input text.</summary>
    public static AttributePath Parse(string candidate) =>
        TryCreate(candidate, out AttributePath path, out PathError error)
            ? path
            : throw new FormatException($"Invalid attribute path ({error}).");

    /// <summary>Wraps a string that already passed validation, such as one read back from a catalog built from paths.</summary>
    internal static AttributePath FromValidated(string value) => new(value);

    /// <summary>True when this path is a strict descendant of <paramref name="ancestor"/> ("dims.width" under "dims").</summary>
    public bool IsDescendantOf(AttributePath ancestor)
    {
        string self = Value;
        string other = ancestor.Value;
        // Longer, shares the whole ancestor as a prefix, and continues with the separator
        return self.Length > other.Length
            && self.StartsWith(other, StringComparison.Ordinal)
            && self[other.Length] == '.';
    }

    private static PathError Classify(string? candidate)
    {
        // Null and empty are the same failure
        if (string.IsNullOrEmpty(candidate))
        {
            return PathError.Empty;
        }
        // Length first: it bounds the cost of every check after it
        if (candidate.Length > MaxLength)
        {
            return PathError.TooLong;
        }
        // Implicit string-to-span conversion: no allocation
        ReadOnlySpan<char> span = candidate;
        // One vectorized scan replaces a per-character loop
        if (span.IndexOfAnyExcept(s_allowed) >= 0)
        {
            return PathError.DisallowedChar;
        }
        // Leading, trailing or doubled dots would make the derived indentation ambiguous
        if (span[0] == '.' || span[^1] == '.' || candidate.Contains("..", StringComparison.Ordinal))
        {
            return PathError.EmptySegment;
        }
        // Vectorized separator count bounds the renderer's group stack
        if (span.Count('.') + 1 > MaxDepth)
        {
            return PathError.TooDeep;
        }
        return PathError.None;
    }

    /// <inheritdoc />
    public bool Equals(AttributePath other) =>
        // Ordinal equality, consistent with every comparer downstream
        string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AttributePath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        // Ordinal hashing, matching Equals; default(AttributePath) hashes to 0
        _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    /// <summary>The path text, or empty for <c>default(AttributePath)</c>.</summary>
    public override string ToString() => _value ?? string.Empty;

    /// <summary>Ordinal equality.</summary>
    public static bool operator ==(AttributePath left, AttributePath right) => left.Equals(right);

    /// <summary>Ordinal inequality.</summary>
    public static bool operator !=(AttributePath left, AttributePath right) => !left.Equals(right);
}
