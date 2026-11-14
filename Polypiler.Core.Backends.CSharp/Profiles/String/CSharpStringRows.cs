using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for the string operations of the IR. Every search, comparison
/// and replacement is written with its ordinal member, never with one that compares by culture; a
/// code unit is a <c>ushort</c> in the generated class and is cast to <c>char</c> where a member
/// takes one. What no framework has - the replacement of the first occurrence, ECMA-262's white
/// space, padding with a string, the code point of a lone surrogate - is a helper in every profile.
/// </summary>
internal static class CSharpStringRows {

  private const string Ordinal = "global::System.StringComparison.Ordinal";

  private static readonly IrTypeKind[] _text = [IrTypeKind.String];
  private static readonly IrTypeKind[] _unit = [IrTypeKind.U16];

  /// <summary>Every row of the string operations.</summary>
  public static IEnumerable<CSharpCapability> All => [
    Written("String.Length", IrIntrinsicOp.StrLength, _text, "{0}.Length"),
    Written("String.Chars[Int32]", IrIntrinsicOp.StrCodeUnitAt, _text, "(ushort){0}[{1}]"),
    CSharpCapability.OfOperation("String.Concat(String, String)", CSharpProfile.Net20, IrIntrinsicOp.StrConcat, _text, new CSharpForm("string.Concat")),
    Written("Char.ToString(Char)", IrIntrinsicOp.StrFromCodeUnit, _unit, "char.ToString((char){0})"),
    Helped("String(Char[], Int32, Int32) from code units", IrIntrinsicOp.StrFromCodeUnits, [IrTypeKind.Array], "{h}({0}, {1}, {2})", CSharpStringHelpers.FromCodeUnits),
    Helped("String.ToCharArray() as code units", IrIntrinsicOp.StrToCodeUnits, _text, "{h}({0})", CSharpStringHelpers.ToCodeUnits),
    Written("String(Char, Int32)", IrIntrinsicOp.StrRepeat, _unit, "new string((char){0}, {1})"),
    Helped("repetition of a string", IrIntrinsicOp.StrRepeat, _text, "{h}({0}, {1})", CSharpStringHelpers.Repeat),
    Written("String.Substring(Int32, Int32)", IrIntrinsicOp.StrSubstring, _text, "{0}.Substring({1}, {2})"),
    CSharpCapability.OfOperation("String.Equals(String, String)", CSharpProfile.Net20, IrIntrinsicOp.StrEquals, _text, new CSharpForm("string.Equals")),
    Written("String.CompareOrdinal(String, String)", IrIntrinsicOp.StrCompareOrdinal, _text, "global::System.Math.Sign(string.CompareOrdinal({0}, {1}))"),
    Written("String.IndexOf(String, Int32, StringComparison)", IrIntrinsicOp.StrIndexOf, _text, $"{{0}}.IndexOf({{1}}, {{2}}, {Ordinal})"),
    Written("String.IndexOf(Char, Int32)", IrIntrinsicOp.StrIndexOf, _unit, "{0}.IndexOf((char){1}, {2})"),
    Helped("last index of a string at or before a position", IrIntrinsicOp.StrLastIndexOf, _text, "{h}({0}, {1}, {2})", CSharpStringHelpers.LastIndexOf),
    Helped("last index of a code unit at or before a position", IrIntrinsicOp.StrLastIndexOf, _unit, "{h}({0}, (char){1}, {2})", CSharpStringHelpers.LastIndexOfUnit),
    Written("String.StartsWith(String, StringComparison)", IrIntrinsicOp.StrStartsWith, _text, $"{{0}}.StartsWith({{1}}, {Ordinal})"),
    Row("String.StartsWith(Char)", CSharpProfile.NetStandard21, IrIntrinsicOp.StrStartsWith, _unit, "{0}.StartsWith((char){1})", "{h}({0}, (char){1})", CSharpStringHelpers.StartsWithUnit),
    Written("String.EndsWith(String, StringComparison)", IrIntrinsicOp.StrEndsWith, _text, $"{{0}}.EndsWith({{1}}, {Ordinal})"),
    Row("String.EndsWith(Char)", CSharpProfile.NetStandard21, IrIntrinsicOp.StrEndsWith, _unit, "{0}.EndsWith((char){1})", "{h}({0}, (char){1})", CSharpStringHelpers.EndsWithUnit),
    // String.Contains(String) is ordinal in every framework.
    Written("String.Contains(String)", IrIntrinsicOp.StrContains, _text, "{0}.Contains({1})"),
    Row("String.Contains(Char)", CSharpProfile.NetStandard21, IrIntrinsicOp.StrContains, _unit, "{0}.Contains((char){1})", "({0}.IndexOf((char){1}) >= 0)"),
    // String.Replace(String, String) is ordinal in every framework and throws for an empty needle.
    Written("String.Replace(String, String)", IrIntrinsicOp.StrReplaceAll, _text, "{0}.Replace({1}, {2})"),
    Written("String.Replace(Char, Char)", IrIntrinsicOp.StrReplaceAll, _unit, "{0}.Replace((char){1}, (char){2})"),
    Helped("replacement of the first occurrence", IrIntrinsicOp.StrReplaceFirst, _text, "{h}({0}, {1}, {2})", CSharpStringHelpers.ReplaceFirst),
    Row("String.Split(Char, StringSplitOptions)", CSharpProfile.NetStandard21, IrIntrinsicOp.StrSplit, _unit, "{0}.Split((char){1})", "{0}.Split(new char[] { (char){1} })")
      .When(Unlimited),
    Helped("split of ECMA-262 at a code unit", IrIntrinsicOp.StrSplit, _unit, "{h}({0}, char.ToString((char){1}), {2})", CSharpStringHelpers.Split),
    Helped("split of ECMA-262 at a string", IrIntrinsicOp.StrSplit, _text, "{h}({0}, {1}, {2})", CSharpStringHelpers.Split),
    Written("String.Join(String, String[])", IrIntrinsicOp.StrJoin, _text, "string.Join({0}, {1})"),
    // Before .NET Framework 4 Trim kept a list of its own; from 4 on it trims what char.IsWhiteSpace accepts.
    Trimmed("String.Trim()", IrIntrinsicOp.StrTrim, "{0}.Trim()", "true, true"),
    Trimmed("String.TrimStart()", IrIntrinsicOp.StrTrimStart, "{0}.TrimStart()", "true, false"),
    Trimmed("String.TrimEnd()", IrIntrinsicOp.StrTrimEnd, "{0}.TrimEnd()", "false, true"),
    Ecma("trim of ECMA-262", IrIntrinsicOp.StrTrim, "true, true"),
    Ecma("trimStart of ECMA-262", IrIntrinsicOp.StrTrimStart, "true, false"),
    Ecma("trimEnd of ECMA-262", IrIntrinsicOp.StrTrimEnd, "false, true"),
    Helped("padStart of ECMA-262", IrIntrinsicOp.StrPadStart, _text, "{h}({0}, {1}, {2}, true)", CSharpStringHelpers.Pad),
    Helped("padEnd of ECMA-262", IrIntrinsicOp.StrPadEnd, _text, "{h}({0}, {1}, {2}, false)", CSharpStringHelpers.Pad),
    // Char.ConvertToUtf32 throws for a lone surrogate, where codePointAt returns the code unit.
    Helped("codePointAt of ECMA-262", IrIntrinsicOp.StrCodePointAt, _text, "{h}({0}, {1})", CSharpStringHelpers.CodePointAt),
    Written("Char.IsSurrogatePair(String, Int32)", IrIntrinsicOp.StrNextCodePoint, _text, "(char.IsSurrogatePair({0}, {1}) ? 2 : 1)")
  ];

  /// <summary>Whether a split has no limit: a negative constant.</summary>
  private static bool Unlimited(IrIntrinsic intrinsic) => intrinsic.Arguments[2] is IrConst { Value: < 0 };

  /// <summary>A row that C# writes with a member every profile has.</summary>
  private static CSharpCapability Written(string name, IrIntrinsicOp op, IrTypeKind[] operands, string template)
    => CSharpCapability.OfOperation(name, CSharpProfile.Net20, op, operands, new CSharpForm(null, Template: template));

  /// <summary>A row with a member from a profile on and a replacement before it.</summary>
  private static CSharpCapability Row(string name, CSharpProfile since, IrIntrinsicOp op, IrTypeKind[] operands, string native, string replacement, CSharpHelper? helper = null)
    => CSharpCapability.OfOperation(name, since, op, operands, new CSharpForm(null, Template: native), new CSharpForm(null, Template: replacement), helper);

  /// <summary>A row that is a helper in every profile.</summary>
  private static CSharpCapability Helped(string name, IrIntrinsicOp op, IrTypeKind[] operands, string template, CSharpHelper helper)
    => CSharpCapability.OfOperation(name, null, op, operands, null, new CSharpForm(null, Template: template), helper);

  /// <summary>A trim of .NET's white space: the member from .NET Framework 4 on, the helper with the table before.</summary>
  private static CSharpCapability Trimmed(string name, IrIntrinsicOp op, string native, string ends)
    => Row(name, CSharpProfile.Net46, op, _text, native, $"{{h}}({{0}}, {ends})", CSharpStringHelpers.TrimDotNet).When(intrinsic => StringOperations.SetOf(intrinsic) == WhitespaceSet.DotNet);

  /// <summary>A trim of ECMA-262's white space, which is a helper in every profile.</summary>
  private static CSharpCapability Ecma(string name, IrIntrinsicOp op, string ends)
    => Helped(name, op, _text, $"{{h}}({{0}}, {ends})", CSharpStringHelpers.TrimEcma).When(intrinsic => StringOperations.SetOf(intrinsic) == WhitespaceSet.Ecma);

}
