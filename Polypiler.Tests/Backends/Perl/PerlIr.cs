using System.Collections.Generic;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>Builds verified IR modules for the Perl backend's unit tests and emits them.</summary>
internal static class PerlIr {

  public static IrLocal L(string name, IrType type) => new(name, type);

  public static IrLocalRef R(IrLocal local) => new(local);

  public static IrConst C(IrType type, long value) => new(type, value);

  public static IrConst I(long value) => new(IrType.I32, value);

  public static IrConst F64(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  public static IrConst F32(float value) => new(IrType.F32, IrOperators.Bits(IrType.F32, value));

  public static IrConst B(bool value) => new(IrType.Bool, value ? 1 : 0);

  public static IrBinary Bin(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  public static IrBlock Block(params IrStmt[] statements) => new(statements);

  /// <summary>Builds a public IR function collecting every declared local automatically.</summary>
  public static IrFunction Function(string name, IrType result, IrLocal[] parameters, params IrStmt[] body)
    => Function(name, true, result, parameters, body);

  /// <summary>Builds an IR function with explicit visibility collecting every declared local automatically.</summary>
  public static IrFunction Function(string name, bool isPublic, IrType result, IrLocal[] parameters, params IrStmt[] body) {
    var declared = new List<IrLocal>();
    void AddLocal(IrLocal? local) {
      if (local is not null && !declared.Contains(local))
        declared.Add(local);
    }

    void Collect(IrStmt statement) {
      switch (statement) {
        case IrDeclare declaration:
          AddLocal(declaration.Local);
          break;
        case IrBlock block:
          foreach (var child in block.Statements)
            Collect(child);
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
        case IrForEachCollection loop:
          AddLocal(loop.Element);
          AddLocal(loop.Value);
          Collect(loop.Body);
          break;
        case IrSwitch selection:
          foreach (var section in selection.Sections)
            Collect(section.Body);
          break;
        case IrTry trying:
          Collect(trying.Body);
          foreach (var handler in trying.Catches) {
            AddLocal(handler.Local);
            Collect(handler.Body);
          }
          if (trying.Finally is { } cleanup)
            Collect(cleanup);
          break;
        case IrUsing @using:
          AddLocal(@using.Resource);
          Collect(@using.Body);
          break;
      }
    }

    foreach (var stmt in body)
      Collect(stmt);
    return new IrFunction(name, isPublic, result, parameters, declared, new IrBlock(body));
  }

  /// <summary>A module with a single public function <c>f</c> returning one expression.</summary>
  public static IrModule Returning(IrType result, IrLocal[] parameters, IrExpr value)
    => new("M", [Function("f", result, parameters, new IrReturn(value))]);

  /// <summary>Emits the primary <c>.pm</c> file text.</summary>
  public static string Emit(IrModule module, PerlProfile? profile = null, PerlEmitOptions? options = null)
    => PerlEmitter.Emit(module, profile, options)[0].Content;

  /// <summary>Emits all generated files.</summary>
  public static IReadOnlyList<GeneratedFile> EmitAll(IrModule module, PerlProfile? profile = null, PerlEmitOptions? options = null)
    => PerlEmitter.Emit(module, profile, options);

  /// <summary>Legalizes the module against the precomputed libraries and emits the primary <c>.pm</c> text.</summary>
  public static string Legalized(IrModule module, PerlProfile? profile = null, PerlEmitOptions? options = null) {
    var parsed = profile ?? PerlProfile.Default;
    var lowered = PerlEmitter.Legalize(module, () => PrecomputedImplementations.Module, parsed, FloatMode.Accurate);
    return PerlEmitter.Emit(lowered, parsed, options)[0].Content;
  }

  /// <summary>Diagnostics returned by <see cref="PerlEmitter.Check"/>.</summary>
  public static IReadOnlyList<string> Problems(IrModule module, PerlProfile? profile = null, PerlEmitOptions? options = null)
    => [.. PerlEmitter.Check(module, profile, options).Select(d => d.ToString())];

}
