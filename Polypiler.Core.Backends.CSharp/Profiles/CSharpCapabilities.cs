using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>A feature of the C# language the emitter decides on by the language version.</summary>
public enum CSharpFeature {

  /// <summary><c>var</c> for a local whose initializer has its type (C# 3).</summary>
  ImplicitlyTypedLocal,

  /// <summary>A member whose body is <c>=&gt; expression;</c> (C# 6).</summary>
  ExpressionBody,

  /// <summary>A function declared inside a method (C# 7).</summary>
  LocalFunction,

  /// <summary>The discard <c>_ = expression;</c> (C# 7).</summary>
  Discard,

  /// <summary>A literal of binary digits, <c>0b1010</c> (C# 7).</summary>
  BinaryLiteral,

  /// <summary>The underscore between the digits of a literal (C# 7).</summary>
  DigitSeparator,

  /// <summary>The literal <c>default</c> without a type (C# 7.1).</summary>
  DefaultLiteral,

  /// <summary>A local function that captures nothing, marked <c>static</c> (C# 8).</summary>
  StaticLocalFunction,

  /// <summary>The integers of the size of a pointer, <c>nint</c> and <c>nuint</c> (C# 9).</summary>
  NativeInteger,

  /// <summary>A namespace that ends in a semicolon and holds the rest of the file (C# 10).</summary>
  FileScopedNamespace,

  /// <summary>The logical shift <c>&gt;&gt;&gt;</c> of a signed integer (C# 11).</summary>
  UnsignedRightShift

}

/// <summary>
/// How a call is written: an optional cast, a method, the arguments, an optional last argument - or
/// a template, which says the whole expression.
/// </summary>
/// <param name="Method">The method by its full name, or <see langword="null"/> for the helper of the row or for a template that calls no method.</param>
/// <param name="Cast">The type the result is cast to, or <see langword="null"/> for no cast.</param>
/// <param name="Further">What follows the arguments of the IR in the argument list, or <see langword="null"/>.</param>
/// <param name="Template">
/// The expression, with <c>{0}</c>, <c>{1}</c> and so on for the arguments in their order, each once,
/// and <c>{h}</c> for the helper of the row; or <see langword="null"/> for a call. It is a primary
/// expression and stands where overflow is not checked, so its casts keep the low bits.
/// </param>
public sealed record CSharpForm(string? Method, string? Cast = null, string? Further = null, string? Template = null);

/// <summary>
/// One row of the capability table: something the emitter may want to write, the first profile or
/// the first language version that has it, how it is written there and what replaces it elsewhere.
/// </summary>
public sealed class CSharpCapability {

  private CSharpCapability(string name) => this.Name = name;

  /// <summary>What the row is about, as the documentation of .NET or of C# names it.</summary>
  public string Name { get; }

  /// <summary>The first profile whose framework has the API, or <see langword="null"/> for a language feature and for what no framework has.</summary>
  public CSharpProfile? Since { get; private init; }

  /// <summary>The first language version that has the feature, or <see langword="null"/> for an API.</summary>
  public Version? Language { get; private init; }

  /// <summary>The language feature the row is about, or <see langword="null"/>.</summary>
  public CSharpFeature? Feature { get; private init; }

  /// <summary>The intrinsic operation of the IR the row writes, or <see langword="null"/>.</summary>
  public IrIntrinsicOp? Operation { get; private init; }

  /// <summary>The types of the first argument the row is for; empty for every type.</summary>
  public IReadOnlyList<IrTypeKind> Operands { get; private init; } = [];

  /// <summary>The integer type a floating-point number is converted to with saturation, or <see langword="null"/>.</summary>
  public IrTypeKind? Saturates { get; private init; }

  /// <summary>How the framework's own API is called, or <see langword="null"/> where the emitter knows the form itself.</summary>
  public CSharpForm? Native { get; private init; }

  /// <summary>What is written where the profile lacks the row, or <see langword="null"/> when nothing can be.</summary>
  public CSharpForm? Replacement { get; private init; }

  /// <summary>The helper the replacement calls, or <see langword="null"/> when it calls the base library.</summary>
  public CSharpHelper? Helper { get; private init; }

  /// <summary>A feature of the language.</summary>
  /// <param name="name">The feature as the language reference names it.</param>
  /// <param name="feature">The feature.</param>
  /// <param name="language">The first language version that has it.</param>
  /// <param name="helper">The helper that is called where the language lacks the feature, or <see langword="null"/>.</param>
  /// <returns>The row.</returns>
  public static CSharpCapability OfLanguage(string name, CSharpFeature feature, Version language, CSharpHelper? helper = null)
    => new(name) { Feature = feature, Language = language, Helper = helper, Replacement = helper is null ? null : new CSharpForm(null) };

  /// <summary>An API the table records and no operation of the IR uses yet.</summary>
  /// <param name="name">The API as the .NET API reference names it.</param>
  /// <param name="since">The first profile whose framework has it.</param>
  /// <param name="helper">The helper that stands in for it elsewhere, or <see langword="null"/>.</param>
  /// <returns>The row.</returns>
  public static CSharpCapability OfApi(string name, CSharpProfile since, CSharpHelper? helper = null)
    => new(name) { Since = since, Helper = helper, Replacement = helper is null ? null : new CSharpForm(null) };

  /// <summary>An intrinsic operation of the IR that is a call of the base library.</summary>
  /// <param name="name">The API as the .NET API reference names it.</param>
  /// <param name="since">The first profile whose framework has it, or <see langword="null"/> when none has.</param>
  /// <param name="operation">The operation.</param>
  /// <param name="operands">The types of the first argument the row is for; empty for every type.</param>
  /// <param name="native">How the API is called, or <see langword="null"/> when no framework has it.</param>
  /// <param name="replacement">What is written in an earlier profile, or <see langword="null"/> when nothing can be.</param>
  /// <param name="helper">The helper the replacement calls, when its method is <see langword="null"/>.</param>
  /// <returns>The row.</returns>
  public static CSharpCapability OfOperation(
    string name, CSharpProfile? since, IrIntrinsicOp operation, IReadOnlyList<IrTypeKind> operands, CSharpForm? native,
    CSharpForm? replacement = null, CSharpHelper? helper = null)
    => new(name) { Since = since, Operation = operation, Operands = operands, Native = native, Replacement = replacement, Helper = helper };

  /// <summary>The conversion of a floating-point number to an integer type that saturates.</summary>
  /// <param name="name">What the row is about.</param>
  /// <param name="since">The first profile in which a cast saturates, or <see langword="null"/> when none does.</param>
  /// <param name="target">The integer type.</param>
  /// <param name="replacement">The call that saturates elsewhere.</param>
  /// <param name="helper">The helper the replacement calls.</param>
  /// <returns>The row.</returns>
  public static CSharpCapability OfSaturation(string name, CSharpProfile? since, IrTypeKind target, CSharpForm replacement, CSharpHelper helper)
    => new(name) { Since = since, Saturates = target, Replacement = replacement, Helper = helper };

  /// <summary>Whether a profile has what the row is about.</summary>
  /// <param name="profile">The profile.</param>
  /// <returns><see langword="true"/> when the profile's language version or framework is at or above the row's.</returns>
  public bool IsIn(CSharpProfile profile)
    => this.Language is { } language ? profile.LanguageVersion >= language : this.Since is { } since && profile.Rank >= since.Rank;

  /// <summary>The row as a diagnostic names it.</summary>
  public override string ToString() => this.Name;

}

/// <summary>
/// The capability table of the C# backend: for every API of the base library and every feature of
/// the language the emitter may write, the first profile that has it and what replaces it elsewhere.
/// Every decision of the emitter that differs between two profiles is a row here, so a new operation
/// is one new row. The first profiles follow the "Applies to" tables of the .NET API reference and
/// the C# language version history; a profile is a floor, so an API counts from the first profile in
/// which every runtime that implements the profile has it.
/// </summary>
public sealed class CSharpCapabilities {

  private const string Base = "global::System.";
  private const string Bits = Base + "Numerics.BitOperations.";

  private static readonly IrTypeKind[] _any = [];
  private static readonly IrTypeKind[] _single = [IrTypeKind.F32];
  private static readonly IrTypeKind[] _double = [IrTypeKind.F64];
  private static readonly IrTypeKind[] _word = [IrTypeKind.U32];
  private static readonly IrTypeKind[] _long = [IrTypeKind.U64];

  /// <summary>The table the emitter uses unless it is given another.</summary>
  public static CSharpCapabilities Default { get; } = new([
    // The language, by the version that introduced each feature.
    CSharpCapability.OfLanguage("implicitly typed local variable (var)", CSharpFeature.ImplicitlyTypedLocal, new Version(3, 0)),
    CSharpCapability.OfLanguage("expression-bodied member", CSharpFeature.ExpressionBody, new Version(6, 0)),
    CSharpCapability.OfLanguage("local function", CSharpFeature.LocalFunction, new Version(7, 0)),
    CSharpCapability.OfLanguage("discard", CSharpFeature.Discard, new Version(7, 0), CSharpHelpers.Ignore),
    CSharpCapability.OfLanguage("binary literal", CSharpFeature.BinaryLiteral, new Version(7, 0)),
    CSharpCapability.OfLanguage("digit separator", CSharpFeature.DigitSeparator, new Version(7, 0)),
    CSharpCapability.OfLanguage("default literal", CSharpFeature.DefaultLiteral, new Version(7, 1)),
    CSharpCapability.OfLanguage("static local function", CSharpFeature.StaticLocalFunction, new Version(8, 0)),
    CSharpCapability.OfLanguage("native-sized integer (nint)", CSharpFeature.NativeInteger, new Version(9, 0)),
    CSharpCapability.OfLanguage("file-scoped namespace", CSharpFeature.FileScopedNamespace, new Version(10, 0)),
    CSharpCapability.OfLanguage("unsigned right shift (>>>)", CSharpFeature.UnsignedRightShift, new Version(11, 0)),

    // Arrays.
    Call("Array.Copy(Array, int, Array, int, int)", CSharpProfile.Net20, IrIntrinsicOp.ArrayCopy, _any, "Array.Copy"),
    Call("Array.Fill<T>(T[], T)", CSharpProfile.NetStandard21, IrIntrinsicOp.ArrayFill, _any, "Array.Fill", CSharpHelpers.Fill),
    Call("Array.Clear(Array)", CSharpProfile.Net80, IrIntrinsicOp.ArrayClear, _any, "Array.Clear", CSharpHelpers.Clear),
    CSharpCapability.OfApi("Array.Empty<T>()", CSharpProfile.Net46),

    // Math. Before .NET Core 3.0 the smaller and the larger of two floating-point numbers did not order the zeros.
    Call("Math.Abs", CSharpProfile.Net20, IrIntrinsicOp.Abs, _any, "Math.Abs"),
    Call("Math.Sign", CSharpProfile.Net20, IrIntrinsicOp.Sign, _any, "Math.Sign"),
    Call("Math.Min(float, float) as the minimum of IEEE 754", CSharpProfile.NetCoreApp31, IrIntrinsicOp.Min, _single, "Math.Min", CSharpHelpers.MinSingle),
    Call("Math.Min(double, double) as the minimum of IEEE 754", CSharpProfile.NetCoreApp31, IrIntrinsicOp.Min, _double, "Math.Min", CSharpHelpers.MinDouble),
    Call("Math.Min", CSharpProfile.Net20, IrIntrinsicOp.Min, _any, "Math.Min"),
    Call("Math.Max(float, float) as the maximum of IEEE 754", CSharpProfile.NetCoreApp31, IrIntrinsicOp.Max, _single, "Math.Max", CSharpHelpers.MaxSingle),
    Call("Math.Max(double, double) as the maximum of IEEE 754", CSharpProfile.NetCoreApp31, IrIntrinsicOp.Max, _double, "Math.Max", CSharpHelpers.MaxDouble),
    Call("Math.Max", CSharpProfile.Net20, IrIntrinsicOp.Max, _any, "Math.Max"),
    CSharpCapability.OfApi("Math.Clamp", CSharpProfile.NetStandard21),
    Call("Math.Sqrt(double)", CSharpProfile.Net20, IrIntrinsicOp.Sqrt, _double, "Math.Sqrt"),
    Call("Math.Floor(double)", CSharpProfile.Net20, IrIntrinsicOp.Floor, _double, "Math.Floor"),
    Call("Math.Ceiling(double)", CSharpProfile.Net20, IrIntrinsicOp.Ceiling, _double, "Math.Ceiling"),
    Call("Math.Truncate(double)", CSharpProfile.Net20, IrIntrinsicOp.Truncate, _double, "Math.Truncate"),
    Call("Math.Round(double)", CSharpProfile.Net20, IrIntrinsicOp.RoundEven, _double, "Math.Round"),
    CSharpCapability.OfOperation("Math.Round(double, MidpointRounding)", CSharpProfile.Net20, IrIntrinsicOp.RoundAway, _double,
      new CSharpForm(Base + "Math.Round", Further: Base + "MidpointRounding.AwayFromZero")),

    // ECMA-262's rounding and conversions, which no framework has: a helper in every profile.
    Ecma("Math.round of ECMA-262 on float", IrIntrinsicOp.RoundHalfUp, _single, CSharpHelpers.RoundHalfUp, "float"),
    Ecma("Math.round of ECMA-262", IrIntrinsicOp.RoundHalfUp, _double, CSharpHelpers.RoundHalfUp),
    Ecma("ToInt32 of ECMA-262", IrIntrinsicOp.ToInt32, _double, CSharpHelpers.ToInt32),
    Ecma("ToUint32 of ECMA-262", IrIntrinsicOp.ToUint32, _double, CSharpHelpers.ToUint32),

    // MathF. Each of these is exact, so the operation on the double the float is, rounded back, has the same bits.
    Single("MathF.Sqrt(float)", IrIntrinsicOp.Sqrt, "Sqrt"),
    Single("MathF.Floor(float)", IrIntrinsicOp.Floor, "Floor"),
    Single("MathF.Ceiling(float)", IrIntrinsicOp.Ceiling, "Ceiling"),
    Single("MathF.Truncate(float)", IrIntrinsicOp.Truncate, "Truncate"),
    Single("MathF.Round(float)", IrIntrinsicOp.RoundEven, "Round"),
    Single("MathF.Round(float, MidpointRounding)", IrIntrinsicOp.RoundAway, "Round", Base + "MidpointRounding.AwayFromZero"),

    // The bit operations.
    Bit("BitOperations.RotateLeft(uint, int)", IrIntrinsicOp.RotateLeft, _word, CSharpHelpers.RotateLeft32),
    Bit("BitOperations.RotateLeft(ulong, int)", IrIntrinsicOp.RotateLeft, _long, CSharpHelpers.RotateLeft64),
    Bit("BitOperations.RotateRight(uint, int)", IrIntrinsicOp.RotateRight, _word, CSharpHelpers.RotateRight32),
    Bit("BitOperations.RotateRight(ulong, int)", IrIntrinsicOp.RotateRight, _long, CSharpHelpers.RotateRight64),
    Bit("BitOperations.PopCount(uint)", IrIntrinsicOp.PopCount, _word, CSharpHelpers.PopCount32),
    Bit("BitOperations.PopCount(ulong)", IrIntrinsicOp.PopCount, _long, CSharpHelpers.PopCount64),
    Bit("BitOperations.LeadingZeroCount(uint)", IrIntrinsicOp.LeadingZeroCount, _word, CSharpHelpers.LeadingZeroCount32),
    Bit("BitOperations.LeadingZeroCount(ulong)", IrIntrinsicOp.LeadingZeroCount, _long, CSharpHelpers.LeadingZeroCount64),
    Bit("BitOperations.TrailingZeroCount(uint)", IrIntrinsicOp.TrailingZeroCount, _word, CSharpHelpers.TrailingZeroCount32),
    Bit("BitOperations.TrailingZeroCount(ulong)", IrIntrinsicOp.TrailingZeroCount, _long, CSharpHelpers.TrailingZeroCount64),
    Bit("BitOperations.Log2(uint)", IrIntrinsicOp.Log2, _word, CSharpHelpers.Log232),
    Bit("BitOperations.Log2(ulong)", IrIntrinsicOp.Log2, _long, CSharpHelpers.Log264),
    Call("Math.BigMul(uint, uint)", CSharpProfile.Net100, IrIntrinsicOp.BigMul, _word, "Math.BigMul", CSharpHelpers.BigMul32),
    CSharpCapability.OfApi("Math.BigMul(ulong, ulong, out ulong)", CSharpProfile.Net80, CSharpHelpers.BigMul64),

    // The bits of a floating-point number.
    Call("BitConverter.DoubleToInt64Bits(double)", CSharpProfile.Net20, IrIntrinsicOp.FloatBits, _double, "BitConverter.DoubleToInt64Bits"),
    Call("BitConverter.SingleToInt32Bits(float)", CSharpProfile.NetStandard21, IrIntrinsicOp.FloatBits, _single, "BitConverter.SingleToInt32Bits", CSharpHelpers.SingleToInt32Bits),
    Call("BitConverter.Int64BitsToDouble(long)", CSharpProfile.Net20, IrIntrinsicOp.BitsFloat, [IrTypeKind.I64], "BitConverter.Int64BitsToDouble"),
    Call("BitConverter.Int32BitsToSingle(int)", CSharpProfile.NetStandard21, IrIntrinsicOp.BitsFloat, [IrTypeKind.I32], "BitConverter.Int32BitsToSingle", CSharpHelpers.Int32BitsToSingle),

    // A cast of a floating-point number to an integer saturates from .NET 9 on (ADR 0002), and to a type narrower than int never.
    Narrow("sbyte", IrTypeKind.I8, "-128, 127"),
    Narrow("byte", IrTypeKind.U8, "0, 255"),
    Narrow("short", IrTypeKind.I16, "-32768, 32767"),
    Narrow("ushort", IrTypeKind.U16, "0, 65535"),
    CSharpCapability.OfSaturation("saturating cast to int", CSharpProfile.Net100, IrTypeKind.I32, new CSharpForm(null, Further: "-2147483648, 2147483647"), CSharpHelpers.Saturated),
    CSharpCapability.OfSaturation("saturating cast to uint", CSharpProfile.Net100, IrTypeKind.U32, new CSharpForm(null), CSharpHelpers.SaturatedUInt32),
    CSharpCapability.OfSaturation("saturating cast to long", CSharpProfile.Net100, IrTypeKind.I64, new CSharpForm(null), CSharpHelpers.SaturatedInt64),
    CSharpCapability.OfSaturation("saturating cast to ulong", CSharpProfile.Net100, IrTypeKind.U64, new CSharpForm(null), CSharpHelpers.SaturatedUInt64),

    // The integer and bit operations of every width, one family after the other.
    .. CSharpIntegerRows.All,

    // The floating-point operations of #1301.
    .. CSharpFloatCapabilities.Rows
  ]);

  /// <summary>Makes a table of rows.</summary>
  /// <param name="rows">The rows; of two that answer one question the earlier one wins.</param>
  public CSharpCapabilities(IEnumerable<CSharpCapability> rows) => this.Rows = [.. rows];

  /// <summary>The rows, in the order the helpers are written in.</summary>
  public IReadOnlyList<CSharpCapability> Rows { get; }

  /// <summary>Whether a profile has a feature of the language.</summary>
  /// <param name="feature">The feature.</param>
  /// <param name="profile">The profile.</param>
  /// <returns><see langword="true"/> when the table has a row for the feature and the profile's language version reaches it.</returns>
  public bool Has(CSharpFeature feature, CSharpProfile profile) => this.Of(feature) is { } row && row.IsIn(profile);

  /// <summary>The row of a feature of the language.</summary>
  /// <param name="feature">The feature.</param>
  /// <returns>The row, or <see langword="null"/> when the table has none.</returns>
  public CSharpCapability? Of(CSharpFeature feature) => this.Rows.FirstOrDefault(row => row.Feature == feature);

  /// <summary>The row of an intrinsic operation: the one for the type of its first argument, or else the one for every type.</summary>
  /// <param name="operation">The operation.</param>
  /// <param name="operand">The type of the first argument.</param>
  /// <returns>The row, or <see langword="null"/> when the table has none.</returns>
  public CSharpCapability? Of(IrIntrinsicOp operation, IrTypeKind operand)
    => this.Rows.FirstOrDefault(row => row.Operation == operation && row.Operands.Contains(operand))
      ?? this.Rows.FirstOrDefault(row => row.Operation == operation && row.Operands.Count == 0);

  /// <summary>
  /// The type an intrinsic's row is chosen by: that of its first argument, but the type of the node
  /// for a mask and a load, whose first argument is the same for every width, and the type of the
  /// value for a store.
  /// </summary>
  /// <param name="intrinsic">The intrinsic.</param>
  /// <returns>The type of operand to look the row up with.</returns>
  public static IrTypeKind OperandOf(IrIntrinsic intrinsic) => intrinsic.Op switch {
    IrIntrinsicOp.LowMask or IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig => intrinsic.Type.Kind,
    IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig => intrinsic.Arguments[2].Type.Kind,
    _ => intrinsic.Arguments[0].Type.Kind
  };

  /// <summary>The row of the saturating conversion of a floating-point number to an integer type.</summary>
  /// <param name="target">The integer type.</param>
  /// <returns>The row, or <see langword="null"/> when the table has none.</returns>
  public CSharpCapability? OfSaturation(IrTypeKind target) => this.Rows.FirstOrDefault(row => row.Saturates == target);

  /// <summary>A method of the base library, replaced by a helper where there is one.</summary>
  private static CSharpCapability Call(string name, CSharpProfile since, IrIntrinsicOp operation, IrTypeKind[] operands, string method, CSharpHelper? helper = null)
    => CSharpCapability.OfOperation(name, since, operation, operands, new CSharpForm(Base + method), helper is null ? null : new CSharpForm(null), helper);

  /// <summary>A method of <c>MathF</c>, replaced by the one of <c>Math</c> with its result cast back.</summary>
  private static CSharpCapability Single(string name, IrIntrinsicOp operation, string method, string? further = null)
    => CSharpCapability.OfOperation(name, CSharpProfile.NetStandard21, operation, _single,
      new CSharpForm($"{Base}MathF.{method}", Further: further), new CSharpForm($"{Base}Math.{method}", "float", further));

  /// <summary>A method of <c>BitOperations</c>, replaced by a helper.</summary>
  private static CSharpCapability Bit(string name, IrIntrinsicOp operation, IrTypeKind[] operands, CSharpHelper helper)
    => CSharpCapability.OfOperation(name, CSharpProfile.NetCoreApp31, operation, operands, new CSharpForm(Bits + helper.Name), new CSharpForm(null), helper);

  /// <summary>An operation of ECMA-262 that no framework has, which is its helper in every profile.</summary>
  private static CSharpCapability Ecma(string name, IrIntrinsicOp operation, IrTypeKind[] operands, CSharpHelper helper, string? cast = null)
    => CSharpCapability.OfOperation(name, null, operation, operands, null, new CSharpForm(null, cast), helper);

  /// <summary>The saturating conversion to a type narrower than <c>int</c>, which no cast is in any profile.</summary>
  private static CSharpCapability Narrow(string type, IrTypeKind target, string bounds)
    => CSharpCapability.OfSaturation($"saturating cast to {type}", null, target, new CSharpForm(null, type, bounds), CSharpHelpers.Saturated);

}
