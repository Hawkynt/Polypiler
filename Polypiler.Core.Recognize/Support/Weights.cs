using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Polypiler.Core.Recognize;

/// <summary>
/// An integer of a width as a sum of weighted bits: a constant plus, for each bit of each atom, the
/// bit times a weight, modulo 2^width. Addition, subtraction, negation, multiplication by a constant,
/// shifts to the left and truncation are exact on this form for any weights. An and with a constant,
/// a shift to the right and a widening need the bits of the value, which the form shows only when it
/// <see cref="Decompose">decomposes</see>: when, written out in binary, the weighted bits fall into
/// fields that cannot carry into each other.
/// </summary>
internal sealed class Weights {

  /// <summary>The key of the constant's bits among the occurrences of a decomposition.</summary>
  public const int One = -1;

  private Weights(int width, ulong constant, Dictionary<int, ulong> terms) {
    this.Width = width;
    this.Constant = constant & MaskOf(width);
    this.Terms = terms;
  }

  /// <summary>The width in bits, 1 to 64.</summary>
  public int Width { get; }

  /// <summary>The constant part.</summary>
  public ulong Constant { get; }

  /// <summary>The weight of each atom bit, by the key <c>atom * 64 + bit</c>; no weight is zero.</summary>
  public IReadOnlyDictionary<int, ulong> Terms { get; }

  /// <summary>Whether the value is a constant.</summary>
  public bool IsConstant => this.Terms.Count == 0;

  /// <summary>The bits of the width set.</summary>
  public ulong Mask => MaskOf(this.Width);

  public static ulong MaskOf(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

  /// <summary>A constant.</summary>
  public static Weights Of(int width, ulong constant) => new(width, constant, []);

  /// <summary>An atom of a width: each of its bits with the weight of its position.</summary>
  public static Weights Atom(int atom, int width)
    => new(width, 0, Enumerable.Range(0, width).ToDictionary(bit => atom * 64 + bit, bit => 1UL << bit));

  private static Weights Make(int width, ulong constant, IEnumerable<KeyValuePair<int, ulong>> terms) {
    var mask = MaskOf(width);
    var sums = new Dictionary<int, ulong>();
    foreach (var (key, weight) in terms)
      sums[key] = (sums.GetValueOrDefault(key) + weight) & mask;

    return new(width, constant, sums.Where(term => term.Value != 0).ToDictionary());
  }

  public Weights Add(Weights other) => Make(this.Width, this.Constant + other.Constant, this.Terms.Concat(other.Terms));

  public Weights Scale(ulong factor)
    => Make(this.Width, this.Constant * factor, this.Terms.Select(term => KeyValuePair.Create(term.Key, term.Value * factor)));

  public Weights Negate() => this.Scale(ulong.MaxValue);

  public Weights Subtract(Weights other) => this.Add(other.Negate());

  public Weights Complement() => this.Negate().Add(Of(this.Width, this.Mask));

  public Weights ShiftLeft(int count) => this.Scale(1UL << count);

  /// <summary>The low bits of a narrower width.</summary>
  public Weights Truncate(int width) => Make(width, this.Constant, this.Terms);

  /// <summary>Whether two values are the same function of the atom bits.</summary>
  public bool SameAs(Weights other)
    => this.Width == other.Width && this.Constant == other.Constant && this.Terms.Count == other.Terms.Count
      && this.Terms.All(term => other.Terms.TryGetValue(term.Key, out var weight) && weight == term.Value);

  /// <summary>
  /// The fields of the value, by position, lowest first: at each position the keys of the bits whose
  /// weight has that position set, the constant's bit as <see cref="One"/>. The field occupies its
  /// position and the bits above that the count of its keys needs. <see langword="null"/> when two
  /// fields overlap or one reaches beyond the width - then a carry may cross a field.
  /// </summary>
  public IReadOnlyList<Field>? Decompose() {
    var positions = new SortedDictionary<int, List<int>>();
    void Place(int key, ulong weight) {
      for (var bits = weight; bits != 0; bits &= bits - 1) {
        var position = BitOperations.TrailingZeroCount(bits);
        if (!positions.TryGetValue(position, out var keys))
          positions.Add(position, keys = []);

        keys.Add(key);
      }
    }

    Place(One, this.Constant);
    foreach (var (key, weight) in this.Terms)
      Place(key, weight);

    var fields = new List<Field>();
    var end = 0;
    foreach (var (position, keys) in positions) {
      var field = new Field(position, keys);
      if (position < end || field.End > this.Width)
        return null;

      end = field.End;
      fields.Add(field);
    }

    return fields;
  }

  /// <summary>Rebuilds a value from fields at new positions.</summary>
  public static Weights FromFields(int width, IEnumerable<Field> fields) {
    var constant = 0UL;
    var terms = new List<KeyValuePair<int, ulong>>();
    foreach (var field in fields)
      foreach (var key in field.Keys)
        if (key == One)
          constant += 1UL << field.Position;
        else
          terms.Add(KeyValuePair.Create(key, 1UL << field.Position));

    return Make(width, constant, terms);
  }

  /// <summary>The value and a constant mask, or <see langword="null"/> when a field is cut by the mask.</summary>
  public Weights? And(ulong mask) {
    if (this.Decompose() is not { } fields)
      return null;

    var kept = new List<Field>();
    foreach (var field in fields) {
      var covered = mask & field.Bits;
      if (covered == field.Bits)
        kept.Add(field);
      else if (covered != 0)
        return null;
    }

    return FromFields(this.Width, kept);
  }

  /// <summary>The value shifted right with zeros, or <see langword="null"/> when a field is cut by the shift.</summary>
  public Weights? ShiftRightLogical(int count) {
    if (this.Decompose() is not { } fields)
      return null;

    var kept = new List<Field>();
    foreach (var field in fields)
      if (field.Position >= count)
        kept.Add(field with { Position = field.Position - count });
      else if (field.End > count)
        return null;

    return FromFields(this.Width, kept);
  }

  /// <summary>
  /// The single key that alone makes the top bit, when the top bit of the value is one bit of an
  /// atom or of the constant; <see cref="One"/> for the constant; <see langword="null"/> when the
  /// top bit is free; and <see langword="false"/> through <paramref name="known"/> when it is a sum.
  /// </summary>
  private int? TopKey(IReadOnlyList<Field> fields, out bool known) {
    known = true;
    var top = fields.Count == 0 ? null : fields[^1];
    if (top is null || top.End < this.Width)
      return null;

    known = top.Position == this.Width - 1 && top.Keys.Count == 1;
    return known ? top.Keys[0] : null;
  }

  /// <summary>The value shifted right with copies of its top bit, or <see langword="null"/>.</summary>
  public Weights? ShiftRightArithmetic(int count) {
    if (this.Decompose() is not { } fields || this.ShiftRightLogical(count) is not { } shifted)
      return null;

    var top = this.TopKey(fields, out var known);
    if (!known)
      return null;

    return top is not { } key ? shifted : shifted.Add(Bit(this.Width, key, this.Mask & ~(this.Mask >> count)));
  }

  /// <summary>The value in a wider width as an unsigned number, or <see langword="null"/> when its bits are not known.</summary>
  public Weights? ZeroExtend(int width) => this.Decompose() is null ? null : Make(width, this.Constant, this.Terms);

  /// <summary>The value in a wider width with its top bit copied, or <see langword="null"/>.</summary>
  public Weights? SignExtend(int width) {
    if (this.Decompose() is not { } fields)
      return null;

    var top = this.TopKey(fields, out var known);
    if (!known)
      return null;

    var widened = Make(width, this.Constant, this.Terms);
    return top is not { } key ? widened : widened.Add(Bit(width, key, MaskOf(width) & ~this.Mask));
  }

  /// <summary>One bit of an atom, or of the constant, with a weight.</summary>
  private static Weights Bit(int width, int key, ulong weight)
    => key == One ? Of(width, weight) : Make(width, 0, [KeyValuePair.Create(key, weight)]);

  /// <summary>
  /// The or, or the exclusive or, of two values whose fields do not meet - which is then their sum -
  /// or of a value with itself; <see langword="null"/> otherwise.
  /// </summary>
  public Weights? Combine(Weights other, bool exclusive) {
    if (this.SameAs(other))
      return exclusive ? Of(this.Width, 0) : this;

    if (this.Decompose() is not { } mine || other.Decompose() is not { } theirs)
      return null;

    var end = 0;
    foreach (var field in mine.Concat(theirs).OrderBy(field => field.Position)) {
      if (field.Position < end)
        return null;

      end = field.End;
    }

    return this.Add(other);
  }

  /// <summary>
  /// The bits of the value moved to new positions, when every field is a single bit; <see langword="null"/> otherwise.
  /// </summary>
  public Weights? Permute(System.Func<int, int> position) {
    if (this.Decompose() is not { } fields || fields.Any(field => field.Keys.Count != 1))
      return null;

    return FromFields(this.Width, fields.Select(field => field with { Position = position(field.Position) }));
  }

}

/// <summary>A field of a decomposed value: the bits that stand at one position, whose count needs the bits above it.</summary>
/// <param name="Position">The lowest bit of the field.</param>
/// <param name="Keys">The bits counted, each a key of <see cref="Weights.Terms"/> or <see cref="Weights.One"/>.</param>
internal sealed record Field(int Position, IReadOnlyList<int> Keys) {

  /// <summary>The bit above the field.</summary>
  public int End => this.Position + (64 - BitOperations.LeadingZeroCount((ulong)this.Keys.Count));

  /// <summary>The bits the field occupies.</summary>
  public ulong Bits => ((1UL << (this.End - this.Position)) - 1) << this.Position;

}
