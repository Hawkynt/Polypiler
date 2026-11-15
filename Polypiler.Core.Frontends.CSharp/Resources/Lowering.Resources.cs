using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Resource scopes: <c>using (T r = e) S</c>, <c>using (T a = e1, b = e2) S</c>, <c>using (e) S</c>
/// and the declarations <c>using var r = e;</c> and <c>using T r = e;</c>, whose scope is the rest of
/// their block, over objects of classes that implement <c>System.IDisposable</c> - which becomes the
/// module's interface of cleanup (<see cref="IrDisposable"/>). Each resource is one
/// <see cref="IrUsing"/>, several are nested in the order they are declared. <c>await using</c>, a
/// resource of a struct type and the pattern-based <c>Dispose</c> of a <c>ref struct</c> are refused
/// by name.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The interface of cleanup of the module and the function that is the signature of its <c>Dispose</c>, once a class implements <c>System.IDisposable</c>.</summary>
  private (IrMethod Dispose, IrFunction Signature)? _disposable;

  /// <summary>Whether a type is <c>System.IDisposable</c>.</summary>
  private static bool IsDisposable(ITypeSymbol type) => type.SpecialType == SpecialType.System_IDisposable;

  /// <summary>
  /// Declares the interface of cleanup when a class of the file implements <c>System.IDisposable</c>,
  /// so that the class implements it, its <c>Dispose</c> implements the interface's, and a reference
  /// of the type <c>IDisposable</c> is one of the interface.
  /// </summary>
  private void DeclareDisposable(IReadOnlyList<(string Path, BoundModel Model, TypeDeclarationSyntax Type)> classes) {
    var implementing = classes.FirstOrDefault(entry => entry.Model.GetDeclaredSymbol(entry.Type)!.AllInterfaces.Any(IsDisposable));
    if (implementing.Model is null)
      return;

    var symbol = implementing.Model.Compilation.GetSpecialType(SpecialType.System_IDisposable);
    var (type, dispose, signature) = IrDisposable.Declare();
    this._interfaces[symbol] = type;
    this._types.Add(type);
    this._signatures.Add(dispose);
    this._methods[symbol.GetMembers(IrDisposable.Method).OfType<IMethodSymbol>().Single()] = dispose;
    this._disposable = (dispose, signature);
  }

  /// <summary>The signature of the method of the interface of cleanup, which the module holds once it declares the interface.</summary>
  private IEnumerable<IrFunction> DisposableSignature() => this._disposable is { } declared ? [declared.Signature] : [];

  /// <summary>Refuses <c>await using</c> anywhere in a member, before anything else of the member is looked at.</summary>
  private static void NoAwaitUsing(SyntaxNode member) {
    var awaited = member.DescendantNodes().FirstOrDefault(node => node is UsingStatementSyntax { AwaitKeyword.RawKind: not 0 } or LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 });
    if (awaited is not null)
      throw new UnsupportedException(awaited, "await using");
  }

  /// <summary>The statements of a block; a using declaration among them holds the statements after it.</summary>
  private void Statements(IReadOnlyList<StatementSyntax> statements, List<IrStmt> into) {
    for (var index = 0; index < statements.Count; ++index) {
      if (statements[index] is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } declaration) {
        var resources = new List<IrStmt>();
        var scopes = this.Resources(declaration.Declaration, resources);
        var rest = new List<IrStmt>();
        this.Statements([.. statements.Skip(index + 1)], rest);
        into.AddRange(Nested(resources, scopes, new IrBlock(rest), true));
        return;
      }

      this.Statement(statements[index], into);
    }
  }

  /// <summary>A using statement; <see langword="false"/> for any other statement.</summary>
  private bool ResourceStatement(StatementSyntax statement, List<IrStmt> into) {
    if (statement is not UsingStatementSyntax scope)
      return false;

    var acquired = new List<IrStmt>();
    List<(IrLocal Resource, IrExpr Initializer, List<IrStmt> Before)> resources;
    if (scope.Declaration is { } declaration) {
      resources = this.Resources(declaration, acquired);
    } else {
      var value = this.Initialized(scope.Expression!, acquired).Value;
      this.Resource(model.GetTypeInfo(scope.Expression!).Type!, scope.Expression!);
      resources = [(this.Local($"$t{this._locals.Count}", value.Type), value, [])];
    }

    into.AddRange(Nested(acquired, resources, this.Body(scope.Statement), false));
    return true;
  }

  /// <summary>The resources a declaration acquires, in order; the statements that compute the first initializer go to <paramref name="before"/>, those of a later one in front of its own scope.</summary>
  private List<(IrLocal Resource, IrExpr Initializer, List<IrStmt> Before)> Resources(VariableDeclarationSyntax declaration, List<IrStmt> before) {
    var resources = new List<(IrLocal, IrExpr, List<IrStmt>)>();
    foreach (var variable in declaration.Variables) {
      var symbol = (ILocalSymbol)model.GetDeclaredSymbol(variable)!;
      this.Resource(symbol.Type, declaration.Type);
      var statements = resources.Count == 0 ? before : [];
      var local = this.Local(symbol.Name, this.Map(symbol.Type, declaration.Type));
      var value = this.Initialized(variable.Initializer!.Value, statements).Value;
      this._variables[symbol] = local;
      resources.Add((local, value, statements));
    }

    return resources;
  }

  /// <summary>Refuses a resource the IR cannot dispose: of a struct type, whose copy C# disposes, or a <c>ref struct</c> with a <c>Dispose</c> by pattern.</summary>
  private void Resource(ITypeSymbol type, SyntaxNode node) {
    if (type.IsRefLikeType)
      throw new UnsupportedException(node, "a ref struct resource with a pattern-based Dispose");

    if (type.IsValueType)
      throw new UnsupportedException(node, "a resource of a struct type");

    if (this._disposable is null)
      throw new UnsupportedException(node, $"a resource of the type {type.Name}, which implements System.IDisposable through no class of the file");
  }

  /// <summary>The scopes of the resources, the first acquired outermost, around a body.</summary>
  private List<IrStmt> Nested(List<IrStmt> before, List<(IrLocal Resource, IrExpr Initializer, List<IrStmt> Before)> resources, IrBlock body, bool isDeclaration) {
    var dispose = this._disposable!.Value.Dispose;
    for (var index = resources.Count - 1; index >= 0; --index) {
      var (resource, initializer, statements) = resources[index];
      IrStmt scope = new IrUsing(resource, initializer, dispose, body) { IsDeclaration = isDeclaration };
      body = new IrBlock(index == 0 ? [scope] : [.. statements, scope]);
    }

    return [.. before, .. body.Statements];
  }

}
