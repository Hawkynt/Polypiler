using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Polypiler.Core;

namespace Polypiler.Permutation.Model;

/// <summary>
/// A pure bit mapping (PRD §20.2, §24 Q0): for every output bit, the input bit it reads, plain or
/// inverted, a constant or a don't-care. The value is immutable; its facts and masks are computed once,
/// when it is created. Bits are numbered from 0 = least significant, a width is a logical bit count of
/// 1 to 4096 with no register or endianness attached, and input and output width need not agree.
/// Nothing is evaluated and nothing is normalised: a don't-care stays a don't-care, a duplicated bit
/// stays duplicated.
/// </summary>
public sealed class BitMapping : IEquatable<BitMapping> {

  /// <summary>The largest input or output width.</summary>
  public const int MaxWidth = 4096;

  private readonly BitSource[] _outputs;
  private readonly int[] _useCounts;
  private readonly int _hash;

  private BitMapping(int inputWidth, BitSource[] outputs) {
    this.InputWidth = inputWidth;
    this._outputs = outputs;
    this.Outputs = Array.AsReadOnly(outputs);
    this._useCounts = new int[inputWidth];

    var required = new byte[(outputs.Length + 7) / 8];
    var invert = new byte[required.Length];
    var ones = new byte[required.Length];
    var zeros = new byte[required.Length];
    var plainOnly = true;
    for (var bit = 0; bit < outputs.Length; ++bit) {
      var entry = outputs[bit];
      var (index, flag) = (bit >> 3, (byte)(1 << (bit & 7)));
      if (entry.Kind != BitSourceKind.DontCare)
        required[index] |= flag;

      switch (entry.Kind) {
        case BitSourceKind.Source:
          ++this._useCounts[entry.Index];
          break;
        case BitSourceKind.InvertedSource:
          ++this._useCounts[entry.Index];
          invert[index] |= flag;
          this.HasInversion = true;
          break;
        case BitSourceKind.Zero:
          zeros[index] |= flag;
          this.HasConstants = true;
          break;
        case BitSourceKind.One:
          ones[index] |= flag;
          this.HasConstants = true;
          break;
        default:
          this.HasDontCares = true;
          break;
      }
      plainOnly &= entry.Kind == BitSourceKind.Source;
    }

    var used = new byte[(inputWidth + 7) / 8];
    var dropped = new byte[used.Length];
    var readOnce = true;
    for (var bit = 0; bit < inputWidth; ++bit) {
      var count = this._useCounts[bit];
      if (count > 1)
        this.HasDuplication = true;

      readOnce &= count == 1;
      if (count > 0)
        used[bit >> 3] |= (byte)(1 << (bit & 7));
      else
        dropped[bit >> 3] |= (byte)(1 << (bit & 7));
    }

    this.IsInjective = !this.HasDuplication;
    this.IsBijective = this.IsSquare && plainOnly && readOnce;
    this.RequiredOutputMask = Mask(required);
    this.InvertMask = Mask(invert);
    this.OnesMask = Mask(ones);
    this.ZerosMask = Mask(zeros);
    this.UsedInputMask = Mask(used);
    this.DroppedInputMask = Mask(dropped);
    this._hash = Fnv1a(inputWidth, outputs);
  }

  /// <summary>How many input bits the mapping reads from, 1 to 4096.</summary>
  public int InputWidth { get; }

  /// <summary>How many output bits the mapping has, 1 to 4096.</summary>
  public int OutputWidth => this._outputs.Length;

  /// <summary>What each output bit holds; index = output bit, 0 = least significant.</summary>
  public IReadOnlyList<BitSource> Outputs { get; }

  /// <summary>Whether input and output have the same width.</summary>
  public bool IsSquare => this.InputWidth == this._outputs.Length;

  /// <summary>Whether no input bit is read more than once, plain or inverted.</summary>
  public bool IsInjective { get; }

  /// <summary>Whether the mapping is a permutation: square, every output a plain <see cref="BitSourceKind.Source"/>, every input read exactly once.</summary>
  public bool IsBijective { get; }

  /// <summary>Whether an input bit is read more than once.</summary>
  public bool HasDuplication { get; }

  /// <summary>Whether an output bit is an inverted input bit.</summary>
  public bool HasInversion { get; }

  /// <summary>Whether an output bit is a constant.</summary>
  public bool HasConstants { get; }

  /// <summary>Whether an output bit is a don't-care.</summary>
  public bool HasDontCares { get; }

  /// <summary>The output bits whose value is fixed: every output but the don't-cares.</summary>
  public BigInteger RequiredOutputMask { get; }

  /// <summary>The output bits that are inverted input bits.</summary>
  public BigInteger InvertMask { get; }

  /// <summary>The output bits that are the constant 1.</summary>
  public BigInteger OnesMask { get; }

  /// <summary>The output bits that are the constant 0.</summary>
  public BigInteger ZerosMask { get; }

  /// <summary>The input bits some output reads.</summary>
  public BigInteger UsedInputMask { get; }

  /// <summary>The input bits no output reads.</summary>
  public BigInteger DroppedInputMask { get; }

  /// <summary>How many output bits read an input bit, plain or inverted.</summary>
  /// <param name="inputBit">The input bit.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputBit"/> lies outside the input.</exception>
  public int UseCount(int inputBit) {
    ArgumentOutOfRangeException.ThrowIfNegative(inputBit);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(inputBit, this.InputWidth);
    return this._useCounts[inputBit];
  }

  /// <summary>Creates a mapping.</summary>
  /// <param name="inputWidth">The input width, 1 to 4096.</param>
  /// <param name="outputs">What each output bit holds, least significant first; 1 to 4096 entries.</param>
  /// <returns>The mapping.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="outputs"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">An entry is not <see cref="BitSource.IsWellFormed">well formed</see>.</exception>
  /// <exception cref="PermutationModelException">A width is out of range (<c>PLY0800</c>) or an entry reads outside the input (<c>PLY0801</c>).</exception>
  public static BitMapping Create(int inputWidth, IReadOnlyList<BitSource> outputs)
    => Validate(inputWidth, outputs) is { } diagnostic ? throw new PermutationModelException(diagnostic) : new(inputWidth, [.. outputs]);

  /// <summary>Creates a mapping, or reports why it cannot exist.</summary>
  /// <param name="inputWidth">The input width, 1 to 4096.</param>
  /// <param name="outputs">What each output bit holds, least significant first; 1 to 4096 entries.</param>
  /// <param name="diagnostics">Receives <c>PLY0800</c> or <c>PLY0801</c> when the mapping is refused.</param>
  /// <param name="mapping">The mapping, or <see langword="null"/> when it is refused.</param>
  /// <returns>Whether the mapping was created.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="outputs"/> or <paramref name="diagnostics"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">An entry is not <see cref="BitSource.IsWellFormed">well formed</see>.</exception>
  public static bool TryCreate(int inputWidth, IReadOnlyList<BitSource> outputs, DiagnosticBag diagnostics, [NotNullWhen(true)] out BitMapping? mapping) {
    ArgumentNullException.ThrowIfNull(diagnostics);
    if (Validate(inputWidth, outputs) is { } diagnostic) {
      diagnostics.Report(diagnostic);
      mapping = null;
      return false;
    }
    mapping = new(inputWidth, [.. outputs]);
    return true;
  }

  /// <summary>The first reason a mapping cannot exist, or <see langword="null"/>.</summary>
  private static Diagnostic? Validate(int inputWidth, IReadOnlyList<BitSource> outputs) {
    ArgumentNullException.ThrowIfNull(outputs);
    for (var bit = 0; bit < outputs.Count; ++bit)
      if (!outputs[bit].IsWellFormed)
        throw new ArgumentException($"output bit {bit}: bit source {outputs[bit].Kind} with index {outputs[bit].Index} is not well formed", nameof(outputs));

    if (!IsWidth(inputWidth))
      return Diagnostic.Create(PermutationDiagnostics.WidthOutOfRange, null, inputWidth);
    if (!IsWidth(outputs.Count))
      return Diagnostic.Create(PermutationDiagnostics.WidthOutOfRange, null, outputs.Count);

    for (var bit = 0; bit < outputs.Count; ++bit) {
      var entry = outputs[bit];
      if (entry.ReadsInput && (entry.Index < 0 || entry.Index >= inputWidth))
        return Diagnostic.Create(PermutationDiagnostics.SourceOutOfRange, null, bit, entry.Index, inputWidth);
    }
    return null;
  }

  /// <summary>Whether a count is a valid width, 1 to 4096.</summary>
  internal static bool IsWidth(long width) => width is >= 1 and <= MaxWidth;

  /// <summary>Output bit <c>i</c> reads input bit <c>i</c>.</summary>
  /// <param name="width">The width, 1 to 4096.</param>
  /// <exception cref="PermutationModelException">The width is out of range (<c>PLY0800</c>).</exception>
  public static BitMapping Identity(int width) => Square(width, bit => BitSource.From(bit));

  /// <summary>Output bit <c>i</c> reads input bit <c>width - 1 - i</c>.</summary>
  /// <param name="width">The width, 1 to 4096.</param>
  /// <exception cref="PermutationModelException">The width is out of range (<c>PLY0800</c>).</exception>
  public static BitMapping Reverse(int width) => Square(width, bit => BitSource.From(width - 1 - bit));

  /// <summary>The two halves change places: output bit <c>i</c> reads input bit <c>(i + width / 2) mod width</c>.</summary>
  /// <param name="width">The width, even, 2 to 4096.</param>
  /// <exception cref="ArgumentException">The width is odd.</exception>
  /// <exception cref="PermutationModelException">The width is out of range (<c>PLY0800</c>).</exception>
  public static BitMapping SwapHalves(int width) {
    if (width % 2 != 0)
      throw new ArgumentException($"swap-halves needs an even width, got {width}", nameof(width));

    return Square(width, bit => BitSource.From(bit < width / 2 ? bit + width / 2 : bit - width / 2));
  }

  /// <summary>A rotation towards the most significant bit: output bit <c>(i + amount) mod width</c> reads input bit <c>i</c>.</summary>
  /// <param name="width">The width, 1 to 4096.</param>
  /// <param name="amount">The amount, any integer; it is taken modulo the width, so a negative amount rotates the other way.</param>
  /// <exception cref="PermutationModelException">The width is out of range (<c>PLY0800</c>).</exception>
  public static BitMapping Rotation(int width, int amount) {
    if (!IsWidth(width))
      return Create(width, []);

    var shift = (int)(((long)amount % width + width) % width);
    return Square(width, bit => BitSource.From((bit - shift + width) % width));
  }

  /// <summary>Every output bit is a constant: output bit <c>i</c> is bit <c>i</c> of <paramref name="value"/>.</summary>
  /// <param name="width">The width of input and output, 1 to 4096.</param>
  /// <param name="value">The constant, 0 to 2^width - 1.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative or wider than the width.</exception>
  /// <exception cref="PermutationModelException">The width is out of range (<c>PLY0800</c>).</exception>
  public static BitMapping Constant(int width, BigInteger value) {
    if (!IsWidth(width))
      return Create(width, []);
    if (value.Sign < 0 || value.GetBitLength() > width)
      throw new ArgumentOutOfRangeException(nameof(value), value, $"a constant of width {width} lies in 0..2^{width}-1");

    return Square(width, bit => ((value >> bit) & BigInteger.One).IsZero ? BitSource.Zero : BitSource.One);
  }

  /// <summary>A square mapping of a width whose output bit <c>i</c> holds <c>entry(i)</c>.</summary>
  private static BitMapping Square(int width, Func<int, BitSource> entry) {
    if (!IsWidth(width))
      return Create(width, []);

    var outputs = new BitSource[width];
    for (var bit = 0; bit < width; ++bit)
      outputs[bit] = entry(bit);
    return new(width, outputs);
  }

  /// <summary>
  /// BitPerm's view of the mapping: for each output bit the input bit it reads, plain or inverted, and
  /// -1 for a constant or a don't-care. Inversion and constants are lost; the result is for comparison
  /// with BitPerm only.
  /// </summary>
  /// <returns>The route map, one entry per output bit.</returns>
  /// <exception cref="InvalidOperationException">The mapping is not square; BitPerm only knows square mappings.</exception>
  public int[] ToRouteMap() {
    if (!this.IsSquare)
      throw new InvalidOperationException($"a route map needs a square mapping; this one maps {this.InputWidth} input bits to {this.OutputWidth} output bits");

    var map = new int[this._outputs.Length];
    for (var bit = 0; bit < map.Length; ++bit)
      map[bit] = this._outputs[bit].ReadsInput ? this._outputs[bit].Index : -1;
    return map;
  }

  /// <summary>
  /// The mapping of a BitPerm route map: a plain <see cref="BitSourceKind.Source"/> for every index, a
  /// <see cref="BitSourceKind.DontCare"/> for -1. -1 is never read as the constant 0.
  /// </summary>
  /// <param name="routeMap">For each output bit, the input bit it reads or -1; its length is input and output width.</param>
  /// <returns>The mapping.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="routeMap"/> is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The length is out of range (<c>PLY0800</c>) or an index lies outside it (<c>PLY0801</c>).</exception>
  public static BitMapping FromRouteMap(int[] routeMap) {
    ArgumentNullException.ThrowIfNull(routeMap);
    var outputs = new BitSource[routeMap.Length];
    for (var bit = 0; bit < outputs.Length; ++bit)
      outputs[bit] = routeMap[bit] == -1 ? BitSource.DontCare : BitSource.From(routeMap[bit]);
    return Create(routeMap.Length, outputs);
  }

  /// <summary>Reads the text notation <c>&lt;inputWidth&gt;:[&lt;entry&gt;,…]</c>; see <see cref="BitMappingText"/>.</summary>
  /// <param name="text">The text, for example <c>8:[7..0]</c>.</param>
  /// <returns>The mapping.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The text is malformed (<c>PLY0802</c>) or describes no valid mapping (<c>PLY0800</c>, <c>PLY0801</c>).</exception>
  public static BitMapping Parse(string text) => BitMappingText.Parse(text);

  /// <summary>Reads the text notation, or reports why it is not a mapping.</summary>
  /// <param name="text">The text.</param>
  /// <param name="diagnostics">Receives <c>PLY0800</c>–<c>PLY0802</c> when the text is refused.</param>
  /// <param name="mapping">The mapping, or <see langword="null"/> when it is refused.</param>
  /// <returns>Whether the text is a mapping.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="diagnostics"/> is <see langword="null"/>.</exception>
  public static bool TryParse(string text, DiagnosticBag diagnostics, [NotNullWhen(true)] out BitMapping? mapping) => BitMappingText.TryParse(text, diagnostics, out mapping);

  /// <summary>The mapping in the text notation, runs of four or more consecutive plain sources written as ranges.</summary>
  public override string ToString() => BitMappingText.Format(this);

  /// <inheritdoc/>
  public bool Equals(BitMapping? other) {
    if (other is null)
      return false;
    if (ReferenceEquals(this, other))
      return true;

    return this.InputWidth == other.InputWidth && this._outputs.AsSpan().SequenceEqual(other._outputs);
  }

  /// <inheritdoc/>
  public override bool Equals(object? obj) => this.Equals(obj as BitMapping);

  /// <summary>A hash that is the same in every process: FNV-1a over the input width and the entries.</summary>
  public override int GetHashCode() => this._hash;

  /// <summary>Whether two mappings are equal: same widths and the same entry at every output bit.</summary>
  public static bool operator ==(BitMapping? left, BitMapping? right) => left?.Equals(right) ?? right is null;

  /// <summary>Whether two mappings differ.</summary>
  public static bool operator !=(BitMapping? left, BitMapping? right) => !(left == right);

  private static BigInteger Mask(byte[] bytes) => new(bytes, isUnsigned: true);

  private static int Fnv1a(int inputWidth, BitSource[] outputs) {
    const uint offset = 2166136261;
    const uint prime = 16777619;
    var hash = offset;
    void Add(int value) {
      for (var shift = 0; shift < 32; shift += 8) {
        hash ^= (byte)(value >> shift);
        hash *= prime;
      }
    }

    Add(inputWidth);
    Add(outputs.Length);
    foreach (var entry in outputs) {
      Add((int)entry.Kind);
      Add(entry.Index);
    }
    return unchecked((int)hash);
  }

}
