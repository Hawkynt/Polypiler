using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.IR;

/// <summary>Which code units count as white space when a string is trimmed.</summary>
public enum WhitespaceSet {

  /// <summary>.NET's <c>char.IsWhiteSpace</c>: Unicode's <c>White_Space</c>, U+0085 included, U+FEFF not.</summary>
  DotNet,

  /// <summary>ECMA-262's <c>WhiteSpace</c> and <c>LineTerminator</c> (§12.2, §12.3): U+FEFF included, U+0085 not.</summary>
  Ecma

}

/// <summary>
/// The meaning of the string operations of the IR: a string is a sequence of UTF-16 code units, and
/// every comparison, search and split is by code unit - ordinal, never by culture. One method per
/// operation, which the interpreter calls and every backend has to reproduce. An operation that has no
/// result for its operands throws the <see cref="IrTrapException"/> the IR names for it.
/// </summary>
public static class StringOperations {

  /// <summary>
  /// The code units <c>char.IsWhiteSpace</c> of .NET Core 3.0 and later accepts: the characters of
  /// Unicode's <c>White_Space</c> property within the Basic Multilingual Plane. Copied from the
  /// .NET reference source (<c>Char.cs</c>, the Latin-1 table and <c>CharUnicodeInfo</c>, MIT), as
  /// the API reference of <c>Char.IsWhiteSpace</c> lists them.
  /// </summary>
  public static IReadOnlyList<char> DotNetWhitespace { get; } = [
    '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u0020', '\u0085', '\u00A0',
    '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006',
    '\u2007', '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F',
    '\u3000'
  ];

  /// <summary>
  /// The code units ECMA-262 2022 trims: <c>WhiteSpace</c> (§12.2: TAB, VT, FF, ZWNBSP and the
  /// category Zs) and <c>LineTerminator</c> (§12.3: LF, CR, LS, PS).
  /// </summary>
  public static IReadOnlyList<char> EcmaWhitespace { get; } = [
    '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u0020', '\u00A0', '\u1680',
    '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007',
    '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000',
    '\uFEFF'
  ];

  private static readonly HashSet<char> _dotNet = [.. DotNetWhitespace];
  private static readonly HashSet<char> _ecma = [.. EcmaWhitespace];

  private static readonly IrType _str = IrType.String;
  private static readonly IrType _strings = IrType.Array(IrType.String);
  private static readonly IrType _units = IrType.Array(IrType.U16);

  /// <summary>Every signature of every string operation: the type of the node and the types of its arguments.</summary>
  private static readonly Dictionary<IrIntrinsicOp, (IrType Result, IrType[] Arguments)[]> _signatures = new() {
    [IrIntrinsicOp.StrLength] = [(IrType.I32, [_str])],
    [IrIntrinsicOp.StrCodeUnitAt] = [(IrType.U16, [_str, IrType.I32])],
    [IrIntrinsicOp.StrFromCodeUnit] = [(_str, [IrType.U16])],
    [IrIntrinsicOp.StrFromCodeUnits] = [(_str, [_units, IrType.I32, IrType.I32]), (_str, [_units])],
    [IrIntrinsicOp.StrToCodeUnits] = [(_units, [_str])],
    [IrIntrinsicOp.StrRepeat] = [(_str, [IrType.U16, IrType.I32]), (_str, [_str, IrType.I32])],
    [IrIntrinsicOp.StrSubstring] = [(_str, [_str, IrType.I32, IrType.I32]), (_str, [_str, IrType.I32])],
    [IrIntrinsicOp.StrEquals] = [(IrType.Bool, [_str, _str])],
    [IrIntrinsicOp.StrCompareOrdinal] = [(IrType.I32, [_str, _str])],
    [IrIntrinsicOp.StrIndexOf] = [(IrType.I32, [_str, _str, IrType.I32]), (IrType.I32, [_str, IrType.U16, IrType.I32]), (IrType.I32, [_str, _str]), (IrType.I32, [_str, IrType.U16])],
    [IrIntrinsicOp.StrLastIndexOf] = [(IrType.I32, [_str, _str, IrType.I32]), (IrType.I32, [_str, IrType.U16, IrType.I32]), (IrType.I32, [_str, _str]), (IrType.I32, [_str, IrType.U16])],
    [IrIntrinsicOp.StrStartsWith] = [(IrType.Bool, [_str, _str]), (IrType.Bool, [_str, IrType.U16])],
    [IrIntrinsicOp.StrEndsWith] = [(IrType.Bool, [_str, _str]), (IrType.Bool, [_str, IrType.U16])],
    [IrIntrinsicOp.StrContains] = [(IrType.Bool, [_str, _str]), (IrType.Bool, [_str, IrType.U16])],
    [IrIntrinsicOp.StrReplaceAll] = [(_str, [_str, _str, _str]), (_str, [_str, IrType.U16, IrType.U16])],
    [IrIntrinsicOp.StrReplaceFirst] = [(_str, [_str, _str, _str])],
    [IrIntrinsicOp.StrSplit] = [(_strings, [_str, _str, IrType.I32]), (_strings, [_str, IrType.U16, IrType.I32])],
    [IrIntrinsicOp.StrJoin] = [(_str, [_str, _strings])],
    [IrIntrinsicOp.StrTrim] = [(_str, [_str, IrType.I32])],
    [IrIntrinsicOp.StrTrimStart] = [(_str, [_str, IrType.I32])],
    [IrIntrinsicOp.StrTrimEnd] = [(_str, [_str, IrType.I32])],
    [IrIntrinsicOp.StrPadStart] = [(_str, [_str, IrType.I32, _str])],
    [IrIntrinsicOp.StrPadEnd] = [(_str, [_str, IrType.I32, _str])],
    [IrIntrinsicOp.StrCodePointAt] = [(IrType.I32, [_str, IrType.I32])],
    [IrIntrinsicOp.StrNextCodePoint] = [(IrType.I32, [_str, IrType.I32])]
  };

  /// <summary>The string operations of the IR.</summary>
  public static IReadOnlyCollection<IrIntrinsicOp> Operations { get; } = [.. _signatures.Keys, IrIntrinsicOp.StrConcat];

  /// <summary>Whether an operation is one of the string operations.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for the members of <see cref="Operations"/>.</returns>
  public static bool IsString(IrIntrinsicOp op) => op == IrIntrinsicOp.StrConcat || _signatures.ContainsKey(op);

  /// <summary>
  /// Every fixed signature of an operation: the type of the node and the types of its arguments. The
  /// concatenation takes any number of strings from two on; its entry is the one of two.
  /// </summary>
  /// <param name="op">An operation of <see cref="Operations"/>.</param>
  /// <returns>The signatures.</returns>
  /// <exception cref="ArgumentException">The operation is not a string operation.</exception>
  public static IReadOnlyList<(IrType Result, IReadOnlyList<IrType> Arguments)> Signatures(IrIntrinsicOp op)
    => op == IrIntrinsicOp.StrConcat ? [(_str, [_str, _str])]
      : _signatures.TryGetValue(op, out var signatures) ? [.. signatures.Select(signature => (signature.Result, (IReadOnlyList<IrType>)signature.Arguments))]
      : throw new ArgumentException($"{op} is not a string operation.", nameof(op));

  /// <summary>Whether a string operation takes the types, or <see langword="null"/> for an operation that is none.</summary>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (op == IrIntrinsicOp.StrConcat)
      return result == _str && arguments.Count >= 2 && arguments.All(argument => argument == _str);

    return _signatures.TryGetValue(op, out var signatures)
      ? signatures.Any(signature => signature.Result == result && signature.Arguments.SequenceEqual(arguments))
      : null;
  }

  /// <summary>
  /// What is wrong with a well-typed node of a string operation beyond its types, or <see langword="null"/>:
  /// the white-space set of a trim has to be a constant that names one.
  /// </summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The problem, or <see langword="null"/>.</returns>
  public static string? Problem(IrIntrinsic intrinsic)
    => intrinsic.Op is IrIntrinsicOp.StrTrim or IrIntrinsicOp.StrTrimStart or IrIntrinsicOp.StrTrimEnd
      && intrinsic.Arguments.Count == 2 && SetOf(intrinsic) is null
        ? $"the white-space set of {intrinsic.Op} is not the constant 0 (DotNet) or 1 (Ecma)"
        : null;

  /// <summary>The white-space set a trim names, or <see langword="null"/> when its argument is not a constant naming one.</summary>
  /// <param name="intrinsic">A node of <see cref="IrIntrinsicOp.StrTrim"/>, <see cref="IrIntrinsicOp.StrTrimStart"/> or <see cref="IrIntrinsicOp.StrTrimEnd"/>.</param>
  /// <returns>The set.</returns>
  public static WhitespaceSet? SetOf(IrIntrinsic intrinsic)
    => intrinsic.Arguments[1] is IrConst { Value: (long)WhitespaceSet.DotNet or (long)WhitespaceSet.Ecma } set ? (WhitespaceSet)set.Value : null;

  /// <summary>The constant argument of a trim that names a white-space set.</summary>
  /// <param name="set">The set.</param>
  /// <returns>The <c>i32</c> constant.</returns>
  public static IrConst Set(WhitespaceSet set) => new(IrType.I32, (long)set);

  /// <summary>Evaluates a node of a well-typed module on the values of its arguments, as the interpreter carries them.</summary>
  internal static object Evaluate(IrIntrinsic intrinsic, object[] arguments) {
    string Text(int index) => (string)arguments[index];
    int Int(int index) => (int)(long)arguments[index];
    string Needle(int index) => arguments[index] as string ?? ((char)(long)arguments[index]).ToString();
    switch (intrinsic.Op) {
      case IrIntrinsicOp.StrLength:
        return (long)Text(0).Length;
      case IrIntrinsicOp.StrCodeUnitAt:
        return (long)CodeUnitAt(Text(0), Int(1));
      case IrIntrinsicOp.StrConcat:
        return string.Concat(arguments.Cast<string>());
      case IrIntrinsicOp.StrFromCodeUnit:
        return ((char)(long)arguments[0]).ToString();
      case IrIntrinsicOp.StrFromCodeUnits:
        // An array of u16 the caller handed over as a char[] stays one.
        var units = arguments[0] as ushort[] ?? Array.ConvertAll((char[])arguments[0], unit => (ushort)unit);
        return arguments.Length == 1 ? FromCodeUnits(units, 0, units.Length) : FromCodeUnits(units, Int(1), Int(2));
      case IrIntrinsicOp.StrToCodeUnits:
        return Text(0).Select(unit => (ushort)unit).ToArray();
      case IrIntrinsicOp.StrRepeat:
        return Repeat(Needle(0), Int(1));
      case IrIntrinsicOp.StrSubstring:
        return arguments.Length == 2 ? Substring(Text(0), Int(1), Text(0).Length - Int(1)) : Substring(Text(0), Int(1), Int(2));
      case IrIntrinsicOp.StrEquals:
        return string.Equals(Text(0), Text(1), StringComparison.Ordinal);
      case IrIntrinsicOp.StrCompareOrdinal:
        return (long)CompareOrdinal(Text(0), Text(1));
      case IrIntrinsicOp.StrIndexOf:
        return (long)IndexOf(Text(0), Needle(1), arguments.Length == 2 ? 0 : Int(2));
      case IrIntrinsicOp.StrLastIndexOf:
        return (long)LastIndexOf(Text(0), Needle(1), arguments.Length == 2 ? Text(0).Length : Int(2));
      case IrIntrinsicOp.StrStartsWith:
        return Text(0).StartsWith(Needle(1), StringComparison.Ordinal);
      case IrIntrinsicOp.StrEndsWith:
        return Text(0).EndsWith(Needle(1), StringComparison.Ordinal);
      case IrIntrinsicOp.StrContains:
        return Text(0).Contains(Needle(1), StringComparison.Ordinal);
      case IrIntrinsicOp.StrReplaceAll:
        return ReplaceAll(Text(0), Needle(1), Needle(2));
      case IrIntrinsicOp.StrReplaceFirst:
        return ReplaceFirst(Text(0), Text(1), Text(2));
      case IrIntrinsicOp.StrSplit:
        return Split(Text(0), Needle(1), Int(2));
      case IrIntrinsicOp.StrJoin:
        return string.Join(Text(0), (string[])arguments[1]);
      case IrIntrinsicOp.StrTrim:
        return Trim(Text(0), (WhitespaceSet)Int(1), true, true);
      case IrIntrinsicOp.StrTrimStart:
        return Trim(Text(0), (WhitespaceSet)Int(1), true, false);
      case IrIntrinsicOp.StrTrimEnd:
        return Trim(Text(0), (WhitespaceSet)Int(1), false, true);
      case IrIntrinsicOp.StrPadStart:
        return Pad(Text(0), Int(1), Text(2), true);
      case IrIntrinsicOp.StrPadEnd:
        return Pad(Text(0), Int(1), Text(2), false);
      case IrIntrinsicOp.StrCodePointAt:
        return (long)CodePointAt(Text(0), Int(1));
      default:
        return (long)NextCodePoint(Text(0), Int(1));
    }
  }

  /// <summary>The code unit at an index.</summary>
  /// <param name="text">The string.</param>
  /// <param name="index">The index.</param>
  /// <returns>The code unit.</returns>
  /// <exception cref="IrTrapException">The index is outside the string.</exception>
  public static char CodeUnitAt(string text, int index) => text[Within(text, index)];

  /// <summary>The string of a range of code units.</summary>
  /// <param name="units">The code units.</param>
  /// <param name="start">The first index.</param>
  /// <param name="length">The number of code units.</param>
  /// <returns>The string.</returns>
  /// <exception cref="IrTrapException">The range is not inside the array.</exception>
  public static string FromCodeUnits(ushort[] units, int start, int length) {
    Range(units.Length, start, length);
    var builder = new StringBuilder(length);
    for (var index = start; index < start + length; ++index)
      builder.Append((char)units[index]);

    return builder.ToString();
  }

  /// <summary>A string the given number of times.</summary>
  /// <param name="text">The string.</param>
  /// <param name="count">How often.</param>
  /// <returns>The repeated string.</returns>
  /// <exception cref="IrTrapException">The count is negative.</exception>
  public static string Repeat(string text, int count) {
    if (count < 0)
      throw new IrTrapException(IrTrap.IntegerOverflow);

    var builder = new StringBuilder();
    for (var pass = 0; pass < count; ++pass)
      builder.Append(text);

    return builder.ToString();
  }

  /// <summary>The code units of a range.</summary>
  /// <param name="text">The string.</param>
  /// <param name="start">The first index.</param>
  /// <param name="length">The number of code units.</param>
  /// <returns>The part.</returns>
  /// <exception cref="IrTrapException">The range is not inside the string.</exception>
  public static string Substring(string text, int start, int length) {
    Range(text.Length, start, length);
    return text.Substring(start, length);
  }

  /// <summary>The sign of the ordinal comparison: by the first code unit that differs, a proper prefix first.</summary>
  /// <param name="left">The first string.</param>
  /// <param name="right">The second string.</param>
  /// <returns>-1, 0 or 1.</returns>
  public static int CompareOrdinal(string left, string right) => Math.Sign(string.CompareOrdinal(left, right));

  /// <summary>The first start at or after a position of a needle.</summary>
  /// <param name="text">The string searched.</param>
  /// <param name="needle">The needle; an empty one stands at every position.</param>
  /// <param name="from">The first position tried, within <c>[0, length]</c>.</param>
  /// <returns>The position, or -1.</returns>
  /// <exception cref="IrTrapException">The position is outside <c>[0, length]</c>.</exception>
  public static int IndexOf(string text, string needle, int from) {
    Position(text, from);
    return text.IndexOf(needle, from, StringComparison.Ordinal);
  }

  /// <summary>The last start at or before a position of a needle, as ECMA-262's <c>lastIndexOf</c> counts it.</summary>
  /// <param name="text">The string searched.</param>
  /// <param name="needle">The needle; an empty one stands at every position.</param>
  /// <param name="from">The last position tried, within <c>[0, length]</c>.</param>
  /// <returns>The position, or -1.</returns>
  /// <exception cref="IrTrapException">The position is outside <c>[0, length]</c>.</exception>
  public static int LastIndexOf(string text, string needle, int from) {
    Position(text, from);
    for (var start = Math.Min(from, text.Length - needle.Length); start >= 0; --start)
      if (string.CompareOrdinal(text, start, needle, 0, needle.Length) == 0)
        return start;

    return -1;
  }

  /// <summary>Every occurrence of a needle replaced, left to right, each search going on behind the last replacement.</summary>
  /// <param name="text">The string.</param>
  /// <param name="needle">The needle.</param>
  /// <param name="replacement">What replaces it.</param>
  /// <returns>The string with the replacements.</returns>
  /// <exception cref="IrTrapException">The needle is empty.</exception>
  public static string ReplaceAll(string text, string needle, string replacement)
    => needle.Length == 0 ? throw new IrTrapException(IrTrap.InvalidOperation) : text.Replace(needle, replacement, StringComparison.Ordinal);

  /// <summary>The first occurrence of a needle replaced; an empty needle stands at 0.</summary>
  /// <param name="text">The string.</param>
  /// <param name="needle">The needle.</param>
  /// <param name="replacement">What replaces it.</param>
  /// <returns>The string with the replacement, or the string itself when the needle is absent.</returns>
  public static string ReplaceFirst(string text, string needle, string replacement) {
    var at = text.IndexOf(needle, StringComparison.Ordinal);
    return at < 0 ? text : string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + needle.Length));
  }

  /// <summary>
  /// The parts between the occurrences of a separator, as ECMA-262's <c>split</c> with a string makes
  /// them: an empty string is one empty part, an empty separator splits between every two code units
  /// (and an empty string into no part), and a limit that is not negative keeps the first parts only.
  /// </summary>
  /// <param name="text">The string.</param>
  /// <param name="separator">The separator.</param>
  /// <param name="limit">The largest number of parts, or a negative number for any.</param>
  /// <returns>The parts.</returns>
  public static string[] Split(string text, string separator, int limit) {
    var parts = new List<string>();
    if (separator.Length == 0) {
      foreach (var unit in text)
        parts.Add(unit.ToString());
    } else {
      var start = 0;
      for (var at = text.IndexOf(separator, StringComparison.Ordinal); at >= 0; at = text.IndexOf(separator, start, StringComparison.Ordinal)) {
        parts.Add(text[start..at]);
        start = at + separator.Length;
      }

      parts.Add(text[start..]);
    }

    return [.. limit < 0 ? parts : parts.Take(limit)];
  }

  /// <summary>Whether a code unit is white space in a set.</summary>
  /// <param name="unit">The code unit.</param>
  /// <param name="set">The set.</param>
  /// <returns><see langword="true"/> when the set has it.</returns>
  public static bool IsWhitespace(char unit, WhitespaceSet set) => (set == WhitespaceSet.Ecma ? _ecma : _dotNet).Contains(unit);

  /// <summary>A string without the white space of a set at its start, its end or both.</summary>
  /// <param name="text">The string.</param>
  /// <param name="set">The set.</param>
  /// <param name="start">Whether the start is trimmed.</param>
  /// <param name="end">Whether the end is trimmed.</param>
  /// <returns>The trimmed string.</returns>
  public static string Trim(string text, WhitespaceSet set, bool start, bool end) {
    var first = 0;
    var last = text.Length;
    while (start && first < last && IsWhitespace(text[first], set))
      ++first;

    while (end && last > first && IsWhitespace(text[last - 1], set))
      --last;

    return text[first..last];
  }

  /// <summary>
  /// ECMA-262's <c>padStart</c> and <c>padEnd</c>: the filler repeated up to the length, the last copy
  /// cut to fit; the string itself when it is at least that long or the filler is empty.
  /// </summary>
  /// <param name="text">The string.</param>
  /// <param name="length">The length wanted.</param>
  /// <param name="filler">The filler.</param>
  /// <param name="atStart">Whether the filler goes in front.</param>
  /// <returns>The padded string.</returns>
  public static string Pad(string text, int length, string filler, bool atStart) {
    if (length <= text.Length || filler.Length == 0)
      return text;

    var builder = new StringBuilder(length);
    while (builder.Length < length - text.Length)
      builder.Append(filler);

    builder.Length = length - text.Length;
    var padding = builder.ToString();
    return atStart ? padding + text : text + padding;
  }

  /// <summary>ECMA-262's <c>codePointAt</c>: a high surrogate followed by a low one as their code point, any other code unit as itself.</summary>
  /// <param name="text">The string.</param>
  /// <param name="index">The index.</param>
  /// <returns>The code point.</returns>
  /// <exception cref="IrTrapException">The index is outside the string.</exception>
  public static int CodePointAt(string text, int index)
    => NextCodePoint(text, index) == 2 ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];

  /// <summary>The number of code units of the code point at an index: 2 for a pair of surrogates, 1 otherwise.</summary>
  /// <param name="text">The string.</param>
  /// <param name="index">The index.</param>
  /// <returns>1 or 2.</returns>
  /// <exception cref="IrTrapException">The index is outside the string.</exception>
  public static int NextCodePoint(string text, int index)
    => char.IsSurrogatePair(text, Within(text, index)) ? 2 : 1;

  private static int Within(string text, int index)
    => index < 0 || index >= text.Length ? throw new IrTrapException(IrTrap.IndexOutOfRange) : index;

  private static void Position(string text, int position) {
    if (position < 0 || position > text.Length)
      throw new IrTrapException(IrTrap.IndexOutOfRange);
  }

  private static void Range(int count, int start, int length) {
    if (start < 0 || length < 0 || (long)start + length > count)
      throw new IrTrapException(IrTrap.IndexOutOfRange);
  }

}
