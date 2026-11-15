using System.Collections.Generic;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Tests.Backends.Python;

/// <summary>Builds small IR modules for the Python backend's tests and emits them.</summary>
internal static class PythonIr {

  public static IrLocal L(string name, IrType type) => new(name, type);

  public static IrLocalRef R(IrLocal local) => new(local);

  public static IrConst C(IrType type, long value) => new(type, value);

  public static IrConst I(long value) => new(IrType.I32, value);

  public static IrBinary Bin(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  public static IrBlock Block(params IrStmt[] statements) => new(statements);

  /// <summary>A public function; every local its body declares is listed.</summary>
  public static IrFunction Function(string name, IrType result, IrLocal[] parameters, params IrStmt[] body) {
    var declared = new List<IrLocal>();
    void Collect(IrStmt statement) {
      switch (statement) {
        case IrDeclare declaration:
          if (!declared.Contains(declaration.Local))
            declared.Add(declaration.Local);

          break;
        case IrBlock block:
          block.Statements.ToList().ForEach(Collect);
          break;
        case IrIf conditional:
          Collect(conditional.Then);
          if (conditional.Else is { } otherwise)
            Collect(otherwise);

          break;
        case IrWhile loop:
          Collect(loop.Body);
          break;
        case IrDoWhile loop:
          Collect(loop.Body);
          break;
        case IrFor loop:
          Collect(loop.Body);
          Collect(loop.Step);
          break;
        case IrSwitch selection:
          selection.Sections.ToList().ForEach(section => Collect(section.Body));
          break;
      }
    }

    body.ToList().ForEach(Collect);
    return new IrFunction(name, true, result, parameters, declared, new IrBlock(body));
  }

  /// <summary>A function <c>f</c> that returns one expression of its parameters.</summary>
  public static IrModule Returning(IrType result, IrLocal[] parameters, IrExpr value)
    => new("M", [Function("f", result, parameters, new IrReturn(value))]);

  /// <summary>The module emitted for a profile, the default one when none is named, without the target passes.</summary>
  public static string Emit(IrModule module, string? profile = null)
    => PythonEmitter.Emit(module, PythonProfile.Parse(profile)).Single().Content;

  /// <summary>The module with the library's helpers bound in, emitted with the target passes.</summary>
  public static string Legalized(IrModule module, string? profile = null) {
    var parsed = PythonProfile.Parse(profile);
    var lowered = PythonEmitter.Legalize(module, () => PrecomputedImplementations.Module, parsed, FloatMode.Accurate);
    return PythonEmitter.Emit(lowered, parsed, FloatMode.Accurate, null, TargetPassOptions.Default).Files.Single().Content;
  }

  /// <summary>Why the module cannot be emitted, each diagnostic as text.</summary>
  public static IReadOnlyList<string> Problems(IrModule module, string? profile = null)
    => [.. PythonEmitter.Check(module, PythonProfile.Parse(profile)).Select(problem => problem.ToString())];

}
