using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Tests.String;

/// <summary>
/// Hand-built modules of the string operations: one public function per operation and per form a
/// backend tells apart - a needle that is a string or a code unit, the two white-space sets, a split
/// with and without a limit - and the inputs every one of them is called with.
/// </summary>
internal static class StringModules {

  private static readonly IrType _str = IrType.String;

  /// <summary>A parameter of each kind the functions take.</summary>
  private static readonly IrLocal _a = new("a", _str), _b = new("b", _str), _i = new("i", IrType.I32), _j = new("j", IrType.I32), _c = new("c", IrType.U16), _d = new("d", IrType.U16);
  private static readonly IrLocal _units = new("units", IrType.Array(IrType.U16)), _parts = new("parts", IrType.Array(_str));

  private static IrExpr Ref(IrLocal local) => new IrLocalRef(local);

  /// <summary>The constants of <see cref="Operations"/>: a string and a table of strings.</summary>
  private static readonly IrModuleConst _greeting = IrModuleConst.OfStrings("Greeting", _str, ["héllo \"\\\u0001\uD800"]);
  private static readonly IrModuleConst _names = IrModuleConst.OfStrings("Names", IrType.Array(_str), ["zero", "", "\U0001F600"]);

  /// <summary>Every function of <see cref="Operations"/> with its parameters.</summary>
  public static IReadOnlyList<(string Name, IrLocal[] Parameters)> Functions { get; } = [
    ("Length", [_a]), ("CodeUnitAt", [_a, _i]), ("Concat", [_a, _b]), ("FromCodeUnit", [_c]), ("FromCodeUnits", [_units, _i, _j]), ("ToCodeUnits", [_a]),
    ("RepeatUnit", [_c, _i]), ("Repeat", [_a, _i]), ("Substring", [_a, _i, _j]), ("Equal", [_a, _b]), ("Compare", [_a, _b]), ("Less", [_a, _b]),
    ("IndexOf", [_a, _b, _i]), ("IndexOfUnit", [_a, _c, _i]), ("LastIndexOf", [_a, _b, _i]), ("LastIndexOfUnit", [_a, _c, _i]),
    ("StartsWith", [_a, _b]), ("StartsWithUnit", [_a, _c]), ("EndsWith", [_a, _b]), ("EndsWithUnit", [_a, _c]), ("Contains", [_a, _b]), ("ContainsUnit", [_a, _c]),
    ("ReplaceAll", [_a, _b]), ("ReplaceAllUnit", [_a, _c, _d]), ("ReplaceFirst", [_a, _b]),
    ("Split", [_a, _b, _i]), ("SplitUnit", [_a, _c]), ("SplitUnitLimit", [_a, _c, _i]), ("Join", [_a, _parts]),
    ("TrimNet", [_a]), ("TrimStartNet", [_a]), ("TrimEndNet", [_a]), ("TrimEcma", [_a]), ("TrimStartEcma", [_a]), ("TrimEndEcma", [_a]),
    ("PadStart", [_a, _i, _b]), ("PadEnd", [_a, _i, _b]), ("CodePointAt", [_a, _i]), ("NextCodePoint", [_a, _i]), ("Constants", [_i])
  ];

  /// <summary>One public function per operation and form, each returning what the operation computes from its parameters.</summary>
  public static IrModule Operations { get; } = Build();

  private static IrModule Build() {
    IrIntrinsic Op(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);
    IrExpr Body(string name) => name switch {
      "Length" => Op(IrIntrinsicOp.StrLength, IrType.I32, Ref(_a)),
      "CodeUnitAt" => Op(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, Ref(_a), Ref(_i)),
      "Concat" => Op(IrIntrinsicOp.StrConcat, _str, Ref(_a), Ref(_b), Op(IrIntrinsicOp.StrSubstring, _str, Ref(_a), new IrConst(IrType.I32, 0), new IrConst(IrType.I32, 0)), new IrStrConst("!")),
      "FromCodeUnit" => Op(IrIntrinsicOp.StrFromCodeUnit, _str, Ref(_c)),
      "FromCodeUnits" => Op(IrIntrinsicOp.StrFromCodeUnits, _str, Ref(_units), Ref(_i), Ref(_j)),
      "ToCodeUnits" => Op(IrIntrinsicOp.StrToCodeUnits, IrType.Array(IrType.U16), Ref(_a)),
      "RepeatUnit" => Op(IrIntrinsicOp.StrRepeat, _str, Ref(_c), Ref(_i)),
      "Repeat" => Op(IrIntrinsicOp.StrRepeat, _str, Ref(_a), Ref(_i)),
      "Substring" => Op(IrIntrinsicOp.StrSubstring, _str, Ref(_a), Ref(_i), Ref(_j)),
      "Equal" => Op(IrIntrinsicOp.StrEquals, IrType.Bool, Ref(_a), Ref(_b)),
      "Compare" => Op(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, Ref(_a), Ref(_b)),
      "Less" => new IrBinary(IrBinaryOp.Less, Op(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, Ref(_a), Ref(_b)), new IrConst(IrType.I32, 0)),
      "IndexOf" => Op(IrIntrinsicOp.StrIndexOf, IrType.I32, Ref(_a), Ref(_b), Ref(_i)),
      "IndexOfUnit" => Op(IrIntrinsicOp.StrIndexOf, IrType.I32, Ref(_a), Ref(_c), Ref(_i)),
      "LastIndexOf" => Op(IrIntrinsicOp.StrLastIndexOf, IrType.I32, Ref(_a), Ref(_b), Ref(_i)),
      "LastIndexOfUnit" => Op(IrIntrinsicOp.StrLastIndexOf, IrType.I32, Ref(_a), Ref(_c), Ref(_i)),
      "StartsWith" => Op(IrIntrinsicOp.StrStartsWith, IrType.Bool, Ref(_a), Ref(_b)),
      "StartsWithUnit" => Op(IrIntrinsicOp.StrStartsWith, IrType.Bool, Ref(_a), Ref(_c)),
      "EndsWith" => Op(IrIntrinsicOp.StrEndsWith, IrType.Bool, Ref(_a), Ref(_b)),
      "EndsWithUnit" => Op(IrIntrinsicOp.StrEndsWith, IrType.Bool, Ref(_a), Ref(_c)),
      "Contains" => Op(IrIntrinsicOp.StrContains, IrType.Bool, Ref(_a), Ref(_b)),
      "ContainsUnit" => Op(IrIntrinsicOp.StrContains, IrType.Bool, Ref(_a), Ref(_c)),
      "ReplaceAll" => Op(IrIntrinsicOp.StrReplaceAll, _str, Ref(_a), Ref(_b), new IrStrConst("$&$$")),
      "ReplaceAllUnit" => Op(IrIntrinsicOp.StrReplaceAll, _str, Ref(_a), Ref(_c), Ref(_d)),
      "ReplaceFirst" => Op(IrIntrinsicOp.StrReplaceFirst, _str, Ref(_a), Ref(_b), new IrStrConst("[$&$1$`]")),
      "Split" => Op(IrIntrinsicOp.StrSplit, IrType.Array(_str), Ref(_a), Ref(_b), Ref(_i)),
      "SplitUnit" => Op(IrIntrinsicOp.StrSplit, IrType.Array(_str), Ref(_a), Ref(_c), new IrConst(IrType.I32, -1)),
      "SplitUnitLimit" => Op(IrIntrinsicOp.StrSplit, IrType.Array(_str), Ref(_a), Ref(_c), Ref(_i)),
      "Join" => Op(IrIntrinsicOp.StrJoin, _str, Ref(_a), Ref(_parts)),
      "TrimNet" => Op(IrIntrinsicOp.StrTrim, _str, Ref(_a), StringOperations.Set(WhitespaceSet.DotNet)),
      "TrimStartNet" => Op(IrIntrinsicOp.StrTrimStart, _str, Ref(_a), StringOperations.Set(WhitespaceSet.DotNet)),
      "TrimEndNet" => Op(IrIntrinsicOp.StrTrimEnd, _str, Ref(_a), StringOperations.Set(WhitespaceSet.DotNet)),
      "TrimEcma" => Op(IrIntrinsicOp.StrTrim, _str, Ref(_a), StringOperations.Set(WhitespaceSet.Ecma)),
      "TrimStartEcma" => Op(IrIntrinsicOp.StrTrimStart, _str, Ref(_a), StringOperations.Set(WhitespaceSet.Ecma)),
      "TrimEndEcma" => Op(IrIntrinsicOp.StrTrimEnd, _str, Ref(_a), StringOperations.Set(WhitespaceSet.Ecma)),
      "PadStart" => Op(IrIntrinsicOp.StrPadStart, _str, Ref(_a), Ref(_i), Ref(_b)),
      "PadEnd" => Op(IrIntrinsicOp.StrPadEnd, _str, Ref(_a), Ref(_i), Ref(_b)),
      "CodePointAt" => Op(IrIntrinsicOp.StrCodePointAt, IrType.I32, Ref(_a), Ref(_i)),
      "NextCodePoint" => Op(IrIntrinsicOp.StrNextCodePoint, IrType.I32, Ref(_a), Ref(_i)),
      _ => Op(IrIntrinsicOp.StrConcat, _str, new IrConstRef(_greeting), new IrArrayLoad(new IrConstRef(_names), Ref(_i)))
    };

    var functions = Functions.Select(function => {
      var body = Body(function.Name);
      return new IrFunction(function.Name, true, body.Type, function.Parameters, [], new IrBlock([new IrReturn(body)]));
    }).ToList();
    return new IrModule("Strings", functions, [_greeting, _names]);
  }

  /// <summary>
  /// The arguments of 200 calls of a function, seed 2026: strings of four classes (ASCII, Latin-1,
  /// the BMP, surrogates and the white space the two sets disagree on), a second string that is often
  /// a part of the first, positions from one before the string to one past it, and code units of it.
  /// </summary>
  public static IEnumerable<object[]> Calls(string function) {
    var parameters = Functions.Single(candidate => candidate.Name == function).Parameters;
    var random = new Random(2026);
    for (var round = 0; round < 200; ++round) {
      var a = IrStringTests.RandomText(random, round % 4);
      var b = random.Next(4) switch {
        0 => "",
        1 when a.Length > 1 => a.Substring(random.Next(a.Length - 1), 1),
        2 when a.Length > 2 => a.Substring(random.Next(a.Length - 2), 2),
        _ => IrStringTests.RandomText(random, round % 4)
      };
      var limit = Math.Max(a.Length, 2) + 2;
      object Argument(IrLocal parameter) => parameter.Name switch {
        "a" => a,
        "b" => b,
        "i" when function == "Constants" => random.Next(3),
        "i" or "j" when function is "Repeat" or "RepeatUnit" or "PadStart" or "PadEnd" => random.Next(-1, 6),
        "i" or "j" when function is "Split" or "SplitUnitLimit" => random.Next(-1, 4),
        "i" or "j" => random.Next(-1, limit),
        "c" or "d" => a.Length > 0 && random.Next(2) == 0 ? (ushort)a[random.Next(a.Length)] : (ushort)IrStringTests.RandomText(random, round % 4).FirstOrDefault('x'),
        "units" => a.Select(unit => (ushort)unit).ToArray(),
        _ => a.Split(b.Length == 0 ? "," : b)
      };
      yield return [.. parameters.Select(Argument)];
    }
  }

}
