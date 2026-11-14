using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  /// <summary>
  /// The function of <c>Math</c> that computes a floating-point operation of the IR: ECMA-262 §21.3.2.
  /// <c>Math.pow</c> is ECMA-262's exponentiation, so it is the operation <c>PowEcma</c>; the operation
  /// <c>Pow</c> of IEEE 754 differs from it for <c>(±1, ±∞)</c> and <c>(1, NaN)</c> and is never native.
  /// </summary>
  private static readonly Dictionary<IrIntrinsicOp, string> _natives = new() {
    [IrIntrinsicOp.Sin] = "Math.sin", [IrIntrinsicOp.Cos] = "Math.cos", [IrIntrinsicOp.Tan] = "Math.tan",
    [IrIntrinsicOp.Asin] = "Math.asin", [IrIntrinsicOp.Acos] = "Math.acos", [IrIntrinsicOp.Atan] = "Math.atan", [IrIntrinsicOp.Atan2] = "Math.atan2",
    [IrIntrinsicOp.Sinh] = "Math.sinh", [IrIntrinsicOp.Cosh] = "Math.cosh", [IrIntrinsicOp.Tanh] = "Math.tanh",
    [IrIntrinsicOp.Asinh] = "Math.asinh", [IrIntrinsicOp.Acosh] = "Math.acosh", [IrIntrinsicOp.Atanh] = "Math.atanh",
    [IrIntrinsicOp.Exp] = "Math.exp", [IrIntrinsicOp.ExpM1] = "Math.expm1", [IrIntrinsicOp.Log] = "Math.log", [IrIntrinsicOp.Log2] = "Math.log2",
    [IrIntrinsicOp.Log10] = "Math.log10", [IrIntrinsicOp.Log1P] = "Math.log1p", [IrIntrinsicOp.PowEcma] = "Math.pow",
    [IrIntrinsicOp.Cbrt] = "Math.cbrt", [IrIntrinsicOp.Hypot] = "Math.hypot"
  };

  /// <summary>
  /// Whether the module writes a floating-point operation itself, in a profile and a mode. ECMAScript
  /// has no exact floating-point operation beyond the ones the IR had before the table, so every
  /// exact one of the table is its portable implementation; an operation with an <c>ulp(n)</c>
  /// contract is the function of <c>Math</c> (rounded with <c>Math.fround</c> for <c>f32</c>) only in
  /// <see cref="FloatMode.Accurate"/>, where the level has it and <see cref="NativeAccuracy"/> admits
  /// V8's function. <c>Clamp</c> and <c>RoundDigits</c> are helpers of the emitter.
  /// </summary>
  /// <param name="intrinsic">The operation.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="capabilities">The capability table; the standard one when <see langword="null"/>.</param>
  /// <returns><see langword="true"/> when the operation is written as a call of <c>Math</c> or a helper of the emitter.</returns>
  public static bool Writes(IrIntrinsic intrinsic, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities = null) {
    if (!FloatLegalization.IsReplaceable(intrinsic))
      return true;

    return mode == FloatMode.Accurate && _natives.TryGetValue(intrinsic.Op, out var name)
      && NativeAccuracy.Slack(NativeAccuracy.Node, intrinsic.Op, intrinsic.Arguments[0].Type) is not null
      && (capabilities ?? JavaScriptCapabilities.Standard).Has(name, profile);
  }

  /// <summary>The module with every floating-point operation the module does not write itself replaced by its portable implementation.</summary>
  /// <param name="module">The module.</param>
  /// <param name="implementations">Gives the portable implementations, as the C# frontend imports <see cref="FloatOperationSources"/>; asked only when needed.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="capabilities">The capability table; the standard one when <see langword="null"/>.</param>
  /// <returns>The module to emit.</returns>
  public static IrModule Legalize(IrModule module, System.Func<IrModule> implementations, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities = null)
    => FloatLegalization.Lower(module, implementations, intrinsic => Writes(intrinsic, profile, mode, capabilities));

  private sealed partial class Writer {

    /// <summary>
    /// A floating-point operation of the table as the call of <c>Math</c> that computes it, a
    /// <c>f32</c> one rounded back; <c>Clamp</c> and <c>RoundDigits</c> as the helpers that trap as the
    /// IR does; <see langword="null"/> for every other operation.
    /// </summary>
    private (string Text, bool Atomic)? Floating(IrIntrinsic intrinsic, string all) {
      var format = intrinsic.Arguments[0].Type;
      if (!format.IsFloat || FloatOperationTable.Of(intrinsic.Op) is not { Double: not null })
        return null;

      if (intrinsic.Op == IrIntrinsicOp.Clamp)
        return (this.Helper("$clamp", [all]), true);

      if (intrinsic.Op == IrIntrinsicOp.RoundDigits)
        return (this.Helper(format == IrType.F32 ? "$roundd32" : "$roundd", [all]), true);

      // An operation without a function of Math is the portable implementation, which legalization puts in.
      if (!_natives.TryGetValue(intrinsic.Op, out var name)) {
        if (this._refused.Add(intrinsic.Op.ToString()))
          this._problems.Add(new Diagnostic(Inexpressible, DiagnosticSeverity.Error,
            $"'{intrinsic.Op}' cannot be expressed in JavaScript profile '{this._profile.LevelName}' without its portable implementation"));

        return ("NaN", true);
      }

      this.Native(name);
      var call = $"{name}({all})";
      return (format == IrType.F32 ? this.Fround(call) : call, true);
    }

  }

}
