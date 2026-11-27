using System.Collections.Generic;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Go;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Tests.Backends.Go;

/// <summary>Builds small IR modules for the Go backend's tests and emits them.</summary>
internal static class GoIr {

  public static IrLocal L(string name, IrType type) => new(name, type);

  public static IrLocalRef R(IrLocal local) => new(local);

  public static IrConst C(IrType type, long value) => new(type, value);

  public static IrConst I(long value) => new(IrType.I32, value);

  public static IrConst F64(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  public static IrConst F32(float value) => new(IrType.F32, IrOperators.Bits(IrType.F32, value));

  public static IrBinary Bin(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  public static IrBlock Block(params IrStmt[] statements) => new(statements);

  /// <summary>A public function; every local its body declares is listed.</summary>
  public static IrFunction Function(string name, IrType result, IrLocal[] parameters, params IrStmt[] body) {
    var declared = new List<IrLocal>();
    void Collect(IrStmt statement) {
      if (statement is IrDeclare declaration && !declared.Contains(declaration.Local))
        declared.Add(declaration.Local);

      if (statement is IrBlock block) {
        block.Statements.ToList().ForEach(Collect);
        return;
      }

      foreach (var inner in IrTree.Blocks(statement))
        Collect(inner);
    }

    body.ToList().ForEach(Collect);
    return new IrFunction(name, true, result, parameters, declared, new IrBlock(body));
  }

  /// <summary>A function <c>f</c> that returns one expression of its parameters.</summary>
  public static IrModule Returning(IrType result, IrLocal[] parameters, IrExpr value)
    => new("M", [Function("f", result, parameters, new IrReturn(value))]);

  /// <summary>The package file and the helpers of a module, emitted for a profile, without the target passes.</summary>
  public static string Emit(IrModule module, string? profile = null)
    => string.Join("\n", GoEmitter.Emit(module, GoProfile.Parse(profile)).Where(file => file.Path.EndsWith(".go", System.StringComparison.Ordinal)).Select(file => file.Content));

  /// <summary>The body of the function <c>f</c> of a module that returns one expression.</summary>
  public static string Body(IrType result, IrLocal[] parameters, IrExpr value, string? profile = null) {
    var text = Emit(Returning(result, parameters, value), profile);
    var start = text.IndexOf("func F(", System.StringComparison.Ordinal);
    return text[start..];
  }

  /// <summary>The module with the library's helpers bound in, emitted with the target passes: every file.</summary>
  public static IReadOnlyList<GeneratedFile> Legalized(IrModule module, string? profile = null, GoEmitOptions? options = null) {
    var parsed = GoProfile.Parse(profile);
    var lowered = GoEmitter.Legalize(module, () => PrecomputedImplementations.Module, parsed, FloatMode.Accurate);
    return GoEmitter.Emit(lowered, parsed, FloatMode.Accurate, null, TargetPassOptions.Default, options).Files;
  }

  /// <summary>Why the module cannot be emitted, each diagnostic as text.</summary>
  public static IReadOnlyList<string> Problems(IrModule module, string? profile = null, GoEmitOptions? options = null)
    => [.. GoEmitter.Check(module, GoProfile.Parse(profile), null, options).Select(problem => problem.ToString())];

}
