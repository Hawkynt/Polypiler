using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for the floating-point operations of #1301: the base-library call
/// of each, from the first profile whose framework has it according to the "Applies to" tables of the
/// .NET API reference. An exact operation is written natively wherever the row is in the profile; an
/// operation with an <c>ulp(n)</c> contract only in the mode <see cref="FloatMode.Accurate"/> and only
/// where <see cref="NativeAccuracy"/> admits .NET's function. A <c>float</c> function is the call of
/// the <c>double</c> one with its result cast back, which <see cref="NativeAccuracy"/> bounds; .NET's
/// own <c>MathF</c> functions have not been measured. Everything else is the portable implementation,
/// which <c>FloatLegalization</c> puts in before the class is written.
/// </summary>
internal static class CSharpFloatCapabilities {

  private const string Base = "global::System.";

  private static readonly IrTypeKind[] _single = [IrTypeKind.F32];
  private static readonly IrTypeKind[] _double = [IrTypeKind.F64];

  /// <summary>The rows.</summary>
  public static IReadOnlyList<CSharpCapability> Rows { get; } = Build();

  private static List<CSharpCapability> Build() {
    var rows = new List<CSharpCapability>();

    // The functions of Math, in both formats: the float one through the double one, rounded back.
    void Math(string method, CSharpProfile since, IrIntrinsicOp op, string parameters = "double") {
      rows.Add(CSharpCapability.OfOperation($"Math.{method}({parameters})", since, op, _double, new CSharpForm(Base + "Math." + method)));
      rows.Add(CSharpCapability.OfOperation($"Math.{method}({parameters}) of a float, cast back", since, op, _single, new CSharpForm(Base + "Math." + method, "float")));
    }

    foreach (var (method, op) in new[] {
      ("Sin", IrIntrinsicOp.Sin), ("Cos", IrIntrinsicOp.Cos), ("Tan", IrIntrinsicOp.Tan), ("Asin", IrIntrinsicOp.Asin),
      ("Acos", IrIntrinsicOp.Acos), ("Atan", IrIntrinsicOp.Atan), ("Atan2", IrIntrinsicOp.Atan2), ("Sinh", IrIntrinsicOp.Sinh),
      ("Cosh", IrIntrinsicOp.Cosh), ("Tanh", IrIntrinsicOp.Tanh), ("Exp", IrIntrinsicOp.Exp), ("Log", IrIntrinsicOp.Log),
      ("Log10", IrIntrinsicOp.Log10), ("Pow", IrIntrinsicOp.Pow)
    })
      Math(method, CSharpProfile.Net20, op);

    Math("Log", CSharpProfile.Net20, IrIntrinsicOp.LogBase, "double, double");
    foreach (var (method, op) in new[] { ("Asinh", IrIntrinsicOp.Asinh), ("Acosh", IrIntrinsicOp.Acosh), ("Atanh", IrIntrinsicOp.Atanh), ("Cbrt", IrIntrinsicOp.Cbrt) })
      Math(method, CSharpProfile.NetStandard21, op);

    Math("Log2", CSharpProfile.NetCoreApp31, IrIntrinsicOp.Log2);
    rows.Add(CSharpCapability.OfOperation("Double.Hypot(double, double)", CSharpProfile.Net80, IrIntrinsicOp.Hypot, _double, new CSharpForm(Base + "Double.Hypot")));
    rows.Add(CSharpCapability.OfOperation("Double.Hypot(double, double) of floats, cast back", CSharpProfile.Net80, IrIntrinsicOp.Hypot, _single, new CSharpForm(Base + "Double.Hypot", "float")));

    // The exact operations, each in its own format.
    void Exact(string type, string method, CSharpProfile since, IrIntrinsicOp op) {
      var single = type == "Math" ? "MathF" : "Single";
      rows.Add(CSharpCapability.OfOperation($"{type}.{method}(double)", since, op, _double, new CSharpForm(Base + type + "." + method)));
      rows.Add(CSharpCapability.OfOperation($"{single}.{method}(float)", since, op, _single, new CSharpForm(Base + single + "." + method)));
    }

    foreach (var (method, op) in new[] {
      ("FusedMultiplyAdd", IrIntrinsicOp.FusedMultiplyAdd), ("CopySign", IrIntrinsicOp.CopySign),
      ("MinMagnitude", IrIntrinsicOp.MinMagnitude), ("MaxMagnitude", IrIntrinsicOp.MaxMagnitude), ("ScaleB", IrIntrinsicOp.ScaleB),
      ("ILogB", IrIntrinsicOp.ILogB), ("BitIncrement", IrIntrinsicOp.BitIncrement), ("BitDecrement", IrIntrinsicOp.BitDecrement)
    })
      Exact("Math", method, CSharpProfile.NetCoreApp31, op);

    rows.Add(CSharpCapability.OfOperation("Math.IEEERemainder(double, double)", CSharpProfile.Net20, IrIntrinsicOp.IeeeRemainder, _double, new CSharpForm(Base + "Math.IEEERemainder")));
    rows.Add(CSharpCapability.OfOperation("MathF.IEEERemainder(float, float)", CSharpProfile.NetStandard21, IrIntrinsicOp.IeeeRemainder, _single, new CSharpForm(Base + "MathF.IEEERemainder")));
    Exact("Double", "MinNumber", CSharpProfile.Net80, IrIntrinsicOp.MinNumber);
    Exact("Double", "MaxNumber", CSharpProfile.Net80, IrIntrinsicOp.MaxNumber);
    Exact("Double", "IsNaN", CSharpProfile.Net20, IrIntrinsicOp.IsNaN);
    Exact("Double", "IsInfinity", CSharpProfile.Net20, IrIntrinsicOp.IsInfinity);
    foreach (var (method, op) in new[] {
      ("IsFinite", IrIntrinsicOp.IsFinite), ("IsNegative", IrIntrinsicOp.IsNegative), ("IsNormal", IrIntrinsicOp.IsNormal), ("IsSubnormal", IrIntrinsicOp.IsSubnormal)
    })
      Exact("Double", method, CSharpProfile.NetStandard21, op);

    Exact("Double", "IsInteger", CSharpProfile.Net80, IrIntrinsicOp.IsInteger);

    // The two operations that trap: Math.Clamp, and before it a helper that throws as it does; the
    // rounding to digits always a helper, since a mode is an int in the IR and an enumeration in C#.
    rows.Add(CSharpCapability.OfOperation("Math.Clamp(double, double, double)", CSharpProfile.NetStandard21, IrIntrinsicOp.Clamp, _double,
      new CSharpForm(Base + "Math.Clamp"), new CSharpForm(null), CSharpFloatHelpers.ClampDouble));
    rows.Add(CSharpCapability.OfOperation("Math.Clamp(float, float, float)", CSharpProfile.NetStandard21, IrIntrinsicOp.Clamp, _single,
      new CSharpForm(Base + "Math.Clamp"), new CSharpForm(null), CSharpFloatHelpers.ClampSingle));
    rows.Add(CSharpCapability.OfOperation("Math.Round(double, int, MidpointRounding) of a mode given as an int", null, IrIntrinsicOp.RoundDigits, _double,
      null, new CSharpForm(null), CSharpFloatHelpers.RoundDigitsDouble));
    rows.Add(CSharpCapability.OfOperation("MathF.Round(float, int, MidpointRounding) of a mode given as an int", null, IrIntrinsicOp.RoundDigits, _single,
      null, new CSharpForm(null), CSharpFloatHelpers.RoundDigitsSingle));
    return rows;
  }

}
