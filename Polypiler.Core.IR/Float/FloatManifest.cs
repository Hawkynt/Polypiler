using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The record of ADR 0005 a generated file carries in its header: the floating-point mode, and every
/// native function with an <c>ulp(n)</c> contract the code calls, with its contract, the slack of the
/// runtime and the comparison bound that follows; or, where no such call is made, that the results are
/// the same bits on every target.
/// </summary>
public static class FloatManifest {

  /// <summary>The lines of the record, without comment markers; none for a module without floating-point numbers.</summary>
  /// <param name="module">The module as it is emitted, after legalization: every operation of the table left in it is a native call.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="runtime">The runtime whose slack applies (<see cref="NativeAccuracy.DotNet"/>, <see cref="NativeAccuracy.Node"/>).</param>
  /// <returns>The lines.</returns>
  public static IReadOnlyList<string> Lines(IrModule module, FloatMode mode, string runtime) {
    var intrinsics = new List<IrIntrinsic>();
    var floating = module.Constants.Any(constant => IsFloat(constant.Type))
      || module.Functions.Any(function => IsFloat(function.ReturnType) || function.Parameters.Concat(function.Locals).Any(local => IsFloat(local.Type)));
    foreach (var function in module.Functions)
      Collect(function.Body, intrinsics);

    var natives = intrinsics
      .Where(intrinsic => intrinsic.Arguments.Count > 0 && intrinsic.Arguments[0].Type.IsFloat
        && FloatOperationTable.Of(intrinsic.Op) is { Contract.Kind: FloatContractKind.Ulp })
      .Select(intrinsic => (intrinsic.Op, Format: intrinsic.Arguments[0].Type))
      .Distinct()
      .OrderBy(native => native.Op.ToString(), System.StringComparer.Ordinal).ThenBy(native => native.Format.ToString(), System.StringComparer.Ordinal)
      .ToList();
    if (mode == FloatMode.Strict)
      return floating ? ["Floating point: strict (ADR 0005). Every function is the portable implementation: the results are the same bits on every target."] : [];

    if (natives.Count == 0)
      return [];

    return [
      "Floating point: accurate (ADR 0005). Native functions, each within its bound of the correctly rounded result:",
      .. natives.Select(native => {
        var slack = NativeAccuracy.Slack(runtime, native.Op, native.Format) ?? 0;
        var contract = FloatOperationTable.Of(native.Op)!.Contract;
        return $"  {native.Op} of {native.Format}: {contract}, slack {slack} on {runtime}, bound {NativeAccuracy.Bound(native.Op, slack)}";
      })
    ];
  }

  private static bool IsFloat(IrType type) => type.IsFloat || type.Element is { IsFloat: true };

  private static void Collect(IrStmt statement, List<IrIntrinsic> found) {
    switch (statement) {
      case IrBlock block:
        foreach (var inner in block.Statements)
          Collect(inner, found);

        break;
      case IrDeclare { Initializer: { } initializer }:
        Collect(initializer, found);
        break;
      case IrAssign assign:
        Collect(assign.Value, found);
        break;
      case IrStore store:
        Collect(store.Array, found);
        Collect(store.Index, found);
        Collect(store.Value, found);
        break;
      case IrExprStmt expression:
        Collect(expression.Expression, found);
        break;
      case IrFieldStore store:
        Collect(store.Target, found);
        Collect(store.Value, found);
        break;
      case IrIf conditional:
        Collect(conditional.Condition, found);
        Collect(conditional.Then, found);
        if (conditional.Else is not null)
          Collect(conditional.Else, found);

        break;
      case IrWhile loop:
        Collect(loop.Condition, found);
        Collect(loop.Body, found);
        break;
      case IrDoWhile loop:
        Collect(loop.Body, found);
        Collect(loop.Condition, found);
        break;
      case IrFor loop:
        Collect(loop.Condition, found);
        Collect(loop.Body, found);
        Collect(loop.Step, found);
        break;
      case IrSwitch choice:
        Collect(choice.Value, found);
        foreach (var section in choice.Sections)
          Collect(section.Body, found);

        break;
      case IrReturn { Value: { } value }:
        Collect(value, found);
        break;
    }
  }

  private static void Collect(IrExpr expression, List<IrIntrinsic> found) {
    IEnumerable<IrExpr> operands = expression switch {
      IrUnary unary => [unary.Operand],
      IrBinary binary => [binary.Left, binary.Right],
      IrConvert conversion => [conversion.Operand],
      IrConditional choice => [choice.Condition, choice.WhenTrue, choice.WhenFalse],
      IrArrayLoad load => [load.Array, load.Index],
      IrArrayLength length => [length.Array],
      IrNewArray creation => [creation.Length],
      IrCall call => call.Arguments,
      IrStep step => [step.Target],
      IrIntrinsic intrinsic => intrinsic.Arguments,
      IrStructNew creation => creation.Arguments,
      IrFieldLoad load => [load.Target],
      IrStructEquals equality => [equality.Left, equality.Right],
      _ => []
    };
    if (expression is IrIntrinsic intrinsic1)
      found.Add(intrinsic1);

    foreach (var operand in operands)
      Collect(operand, found);
  }

}
