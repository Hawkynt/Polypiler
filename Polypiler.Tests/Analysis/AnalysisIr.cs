using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Analysis;

/// <summary>Small IR built by hand and imported from C#, for the tests of the fact facade.</summary>
internal static class AnalysisIr {

  public static readonly IrLocal X = new("x", IrType.U32);
  public static readonly IrLocal A = new("a", IrType.U32);
  public static readonly IrLocal B = new("b", IrType.U32);
  public static readonly IrLocal Y = new("y", IrType.U32);
  public static readonly IrLocal C = new("c", IrType.Bool);

  public static IrExpr R(IrLocal local) => new IrLocalRef(local);

  public static IrExpr K(long value) => new IrConst(IrType.U32, value);

  public static IrExpr Op(IrBinaryOp op, IrExpr left, IrExpr right) => new IrBinary(op, left, right);

  public static IrBlock Block(params IrStmt[] statements) => new(statements);

  public static IrFunction Function(IrLocal[] parameters, params IrStmt[] body) {
    var declared = body.SelectMany(Declared).Distinct().ToList();
    return new IrFunction("f", true, IrType.U32, parameters, declared, new IrBlock(body));
  }

  public static IrModule Module(IrFunction function) => new("m", [function]);

  /// <summary>The method of a C# snippet, imported.</summary>
  public static IrFunction Imported(string methods, string name) => ImportedModule(methods).Functions.Single(function => function.Name == name);

  /// <summary>The module of a C# snippet, imported.</summary>
  public static IrModule ImportedModule(string methods) => PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods));

  /// <summary>Every block of a function's body, the body first, in pre-order.</summary>
  public static IReadOnlyList<IrBlock> Blocks(IrFunction function) {
    var blocks = new List<IrBlock>();
    void Walk(IrBlock block) {
      blocks.Add(block);
      foreach (var statement in block.Statements)
        foreach (var inner in IrTree.Blocks(statement))
          Walk(inner);
    }

    Walk(function.Body);
    return blocks;
  }

  private static IEnumerable<IrLocal> Declared(IrStmt statement) {
    if (statement is IrDeclare declare)
      yield return declare.Local;

    foreach (var inner in IrTree.Blocks(statement))
      foreach (var nested in inner.Statements.SelectMany(Declared))
        yield return nested;
  }

}
