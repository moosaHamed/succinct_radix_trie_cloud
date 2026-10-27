using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Succinct.Catalog;

/// <summary>
/// Which typed column a value lives in. One byte per stored attribute.
/// <see cref="Unset"/> is 0, so <c>default(AttributeValue)</c> is detectably
/// empty instead of a plausible UInt32 zero. Numeric codes here are internal
/// layout; the database maps its own codes explicitly (see TagCodes).
/// </summary>
public enum ValueTag : byte
{
    /// <summary>No value; never stored.</summary>
    Unset = 0,

    /// <summary>Unsigned 32-bit integer.</summary>
    UInt32 = 1,

    /// <summary>Signed 64-bit integer, for identifiers wider than uint (the HVAC Id 12345678950 case).</summary>
    Int64 = 2,

    /// <summary>Finite single-precision float.</summary>
    Float32 = 3,

    /// <summary>Finite double-precision float.</summary>
    Float64 = 4,

    /// <summary>Short categorical text, deduplicated in a string pool.</summary>
    InternedString = 5,

    /// <summary>Longer per-row text, stored without deduplication.</summary>
    FreeText = 6,
}

/// <summary>
/// A boxing-free tagged union of every value kind the store holds. The tag can
/// never disagree with the payload, because the only constructors are the
/// validating factories. Replaces the original <c>object BoxedValue</c>, where a
/// mismatched tag threw <see cref="InvalidCastException"/> deep in the builder.
/// </summary>
public readonly record struct AttributeValue
{
    /// <summary>Longest categorical value, in characters.</summary>
    public const int MaxCategoricalLength = 128;

    /// <summary>Longest free-text value, in characters.</summary>
    public const int MaxFreeTextLength = 8192;

    // 64 raw bits carry every numeric kind; floats round-trip exactly through their bit patterns
    private readonly long _bits;
    // Text payload for the two string kinds; null otherwise
    private readonly string? _text;

    // Internal: the store rebuilds values from its typed columns without re-validating them
    internal AttributeValue(ValueTag tag, long bits, string? text)
    {
        Tag = tag;
        _bits = bits;
        _text = text;
    }

    /// <summary>Which kind of value this is.</summary>
    public ValueTag Tag { get; }

    /// <summary>True for <c>default(AttributeValue)</c>.</summary>
    public bool IsUnset => Tag == ValueTag.Unset;

    // Raw payload views for the builder in this assembly; each is only meaningful for its own tag.
    // unchecked: these reinterpret stored bits, so Debug's checked arithmetic must not treat them as conversions.
    internal uint UInt32Payload => unchecked((uint)_bits);

    internal long Int64Payload => _bits;

    internal float Float32Payload => BitConverter.Int32BitsToSingle(unchecked((int)_bits));

    internal double Float64Payload => BitConverter.Int64BitsToDouble(_bits);

    internal string TextPayload => _text ?? string.Empty;

    /// <summary>An unsigned 32-bit value.</summary>
    public static AttributeValue FromUInt32(uint value) =>
        // uint widens to long losslessly
        new(ValueTag.UInt32, value, null);

    /// <summary>A signed 64-bit value.</summary>
    public static AttributeValue FromInt64(long value) =>
        new(ValueTag.Int64, value, null);

    /// <summary>A finite single-precision value; NaN and infinities are rejected, -0 becomes +0.</summary>
    public static AttributeValue FromFloat32(float value)
    {
        // NaN and infinities break equality, ordering and canonical form, so they never enter
        if (!float.IsFinite(value))
        {
            ThrowHelper.ThrowArgument(nameof(value), "Value must be finite.");
        }
        // -0f == 0f is true, so this maps negative zero to positive zero: one bit pattern per number
        float normalized = value == 0f ? 0f : value;
        return new(ValueTag.Float32, BitConverter.SingleToInt32Bits(normalized), null);
    }

    /// <summary>A finite double-precision value; NaN and infinities are rejected, -0 becomes +0.</summary>
    public static AttributeValue FromFloat64(double value)
    {
        if (!double.IsFinite(value))
        {
            ThrowHelper.ThrowArgument(nameof(value), "Value must be finite.");
        }
        double normalized = value == 0d ? 0d : value;
        return new(ValueTag.Float64, BitConverter.DoubleToInt64Bits(normalized), null);
    }

    /// <summary>A categorical value, deduplicated when stored.</summary>
    public static AttributeValue FromCategorical(string value) =>
        // Length and Unicode gates applied once, here
        new(ValueTag.InternedString, 0, ValidateText(value, MaxCategoricalLength));

    /// <summary>A free-text value, stored per row.</summary>
    public static AttributeValue FromFreeText(string value) =>
        new(ValueTag.FreeText, 0, ValidateText(value, MaxFreeTextLength));

    /// <summary>Reads an unsigned 32-bit value; false for any other tag.</summary>
    public bool TryGetUInt32(out uint value)
    {
        // Tag first: a mismatched read reports false instead of reinterpreting bits
        bool ok = Tag == ValueTag.UInt32;
        value = ok ? UInt32Payload : 0;
        return ok;
    }

    /// <summary>Reads a signed 64-bit value; false for any other tag.</summary>
    public bool TryGetInt64(out long value)
    {
        bool ok = Tag == ValueTag.Int64;
        value = ok ? _bits : 0;
        return ok;
    }

    /// <summary>Reads a single-precision value; false for any other tag.</summary>
    public bool TryGetFloat32(out float value)
    {
        bool ok = Tag == ValueTag.Float32;
        value = ok ? Float32Payload : 0f;
        return ok;
    }

    /// <summary>Reads a double-precision value; false for any other tag.</summary>
    public bool TryGetFloat64(out double value)
    {
        bool ok = Tag == ValueTag.Float64;
        value = ok ? Float64Payload : 0d;
        return ok;
    }

    /// <summary>Reads either text kind; false for numeric tags.</summary>
    public bool TryGetText([NotNullWhen(true)] out string? value)
    {
        // Both string kinds carry their text in the same field
        value = Tag is ValueTag.InternedString or ValueTag.FreeText ? _text : null;
        return value is not null;
    }

    /// <summary>
    /// Identity text: invariant culture and shortest round-trip precision, so two
    /// values produce the same text exactly when they are equal. Used for
    /// canonical forms; never reuse a display format here (finding F5).
    /// </summary>
    public string FormatInvariant() => Tag switch
    {
        // Invariant culture: ASCII digits, and the minus sign is always U+002D
        ValueTag.UInt32 => UInt32Payload.ToString(CultureInfo.InvariantCulture),
        ValueTag.Int64 => _bits.ToString(CultureInfo.InvariantCulture),
        // "R": the shortest text that parses back to the same value, so distinct floats never share text
        ValueTag.Float32 => Float32Payload.ToString("R", CultureInfo.InvariantCulture),
        ValueTag.Float64 => Float64Payload.ToString("R", CultureInfo.InvariantCulture),
        // Text is already its own identity
        ValueTag.InternedString or ValueTag.FreeText => TextPayload,
        // Unset has no text
        _ => string.Empty,
    };

    /// <summary>Display text for people: culture-aware and rounded, never used for identity.</summary>
    public string FormatDisplay(IFormatProvider? provider) => Tag switch
    {
        ValueTag.UInt32 => UInt32Payload.ToString(provider),
        ValueTag.Int64 => _bits.ToString(provider),
        // Two decimals reads well on a label; it is lossy, which is exactly why identity uses FormatInvariant
        ValueTag.Float32 => Float32Payload.ToString("0.##", provider),
        ValueTag.Float64 => Float64Payload.ToString("0.##", provider),
        ValueTag.InternedString or ValueTag.FreeText => TextPayload,
        _ => string.Empty,
    };

    /// <summary>The identity text; see <see cref="FormatInvariant"/>.</summary>
    public override string ToString() => FormatInvariant();

    private static string ValidateText(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Length first: it bounds the Unicode scan
        if (value.Length > maxLength)
        {
            ThrowHelper.ThrowArgument(nameof(value), "Value exceeds the length limit.");
        }
        // Same gate as every other entry point
        if (!UnicodeGuard.IsWellFormed(value))
        {
            ThrowHelper.ThrowArgument(nameof(value), "Value contains an unpaired UTF-16 surrogate.");
        }
        return value;
    }
}

/// <summary>One staged (path, value) pair: a validated path and a typed value, no boxing.</summary>
public readonly record struct ProductAttributeInput(AttributePath Path, AttributeValue Value);
