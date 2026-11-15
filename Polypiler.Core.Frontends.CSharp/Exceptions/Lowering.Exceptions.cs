using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Exceptions: <c>try</c>, <c>catch</c>, <c>catch (T)</c>, <c>catch (T e)</c> with or without a
/// <c>when</c> filter, <c>finally</c>, <c>throw e</c>, <c>throw;</c> and throw expressions; the
/// exception classes of the IR's library (<see cref="IrExceptionClass"/>) - created, caught, derived
/// from by classes of the file - and their <c>Message</c> and <c>InnerException</c>. Any other library
/// exception class is refused by name.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The library class of the IR a type of .NET is, or <see langword="null"/>.</summary>
  private static IrClassType? LibraryClass(ITypeSymbol? type)
    => type is INamedTypeSymbol { Arity: 0, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } named
      ? IrExceptionClass.All.FirstOrDefault(library => library.Name == named.Name) : null;

  /// <summary>Whether a type of .NET is an exception class: <c>System.Exception</c> or a class derived from it.</summary>
  private static bool IsException(ITypeSymbol? type) {
    for (var ancestor = type; ancestor is not null; ancestor = ancestor.BaseType)
      if (ancestor is { Name: "Exception", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } })
        return true;

    return false;
  }

  /// <summary>The exception class of the IR a type is - one of the file derived from an exception class, or a library class - refusing any other.</summary>
  private IrClassType ExceptionClass(ITypeSymbol? type, SyntaxNode node) {
    if (this.ClassOf(type) is { } found && IrExceptionClass.IsException(found))
      return found;

    throw new UnsupportedException(node, IsException(type) ? $"the library exception class {type!.Name}" : $"a catch of {type?.Name}, which is no exception class");
  }

  /// <summary>The statements of exception handling; <see langword="false"/> for any other.</summary>
  private bool ExceptionStatement(StatementSyntax statement, List<IrStmt> into) {
    switch (statement) {
      case ThrowStatementSyntax { Expression: null }:
        into.Add(new IrRethrow());
        return true;
      case ThrowStatementSyntax thrown:
        into.Add(new IrThrow(this.Value(thrown.Expression)));
        return true;
      case TryStatementSyntax attempt:
        into.Add(this.Try(attempt));
        return true;
      default:
        return false;
    }
  }

  /// <summary>A try: its block, each catch clause with its class, variable and filter, and its finally block.</summary>
  private IrTry Try(TryStatementSyntax attempt) {
    var body = this.Block(attempt.Block);
    var catches = new List<IrCatch>();
    foreach (var clause in attempt.Catches) {
      var type = clause.Declaration is null ? IrExceptionClass.Exception : this.ExceptionClass(model.GetTypeInfo(clause.Declaration.Type).Type, clause.Declaration.Type);
      IrLocal? local = null;
      if (clause.Declaration is { Identifier.Text.Length: > 0 } declaration) {
        var symbol = model.GetDeclaredSymbol(declaration)!;
        local = this.Local(symbol.Name, IrType.Object(type));
        this._variables[symbol] = local;
      }

      var filter = clause.Filter is null ? null : this.Value(clause.Filter.FilterExpression);
      catches.Add(new IrCatch(type, local, filter, this.Block(clause.Block)));
    }

    return new IrTry(body, catches, attempt.Finally is null ? null : this.Block(attempt.Finally.Block));
  }

  /// <summary>
  /// The expressions of exceptions, or <see langword="null"/> for any other: a throw expression, the
  /// message or the inner exception of an exception, and a new object of a library exception class.
  /// </summary>
  private IrExpr? ExceptionNatural(ExpressionSyntax expression, IrType type) {
    switch (expression) {
      case ThrowExpressionSyntax thrown:
        return new IrThrowExpression(type, this.Value(thrown.Expression));
      case MemberAccessExpressionSyntax access when model.GetSymbolInfo(access).Symbol is IPropertySymbol { IsStatic: false } property && LibraryClass(property.ContainingType) is not null:
        var field = property.Name switch {
          "Message" => IrExceptionClass.Message,
          "InnerException" => IrExceptionClass.InnerException,
          _ => throw new UnsupportedException(access, $"{property.ContainingType.Name}.{property.Name}")
        };
        return new IrFieldLoad(this.Value(access.Expression), field);
      case IdentifierNameSyntax name when model.GetSymbolInfo(name).Symbol is IPropertySymbol { IsStatic: false } property && LibraryClass(property.ContainingType) is not null:
        var own = property.Name switch {
          "Message" => IrExceptionClass.Message,
          "InnerException" => IrExceptionClass.InnerException,
          _ => throw new UnsupportedException(name, $"{property.ContainingType.Name}.{property.Name}")
        };
        return new IrFieldLoad(new IrLocalRef(this._receiver!), own);
      case BaseObjectCreationExpressionSyntax creation when model.GetSymbolInfo(creation).Symbol is IMethodSymbol constructor && IsException(constructor.ContainingType)
          && this.ClassOf(constructor.ContainingType) is not { IsLibrary: false }:
        var library = this.ExceptionClass(constructor.ContainingType, creation);
        if (creation.Initializer is not null)
          throw new UnsupportedException(creation, "an object initializer of an exception");

        var arguments = creation.ArgumentList?.Arguments.Select(argument => this.Value(argument.Expression)).ToList() ?? [];
        return new IrNew(IrType.Object(library), this.LibraryConstructor(library, constructor, creation), arguments);
      default:
        return null;
    }
  }

  /// <summary>
  /// The constructor of a library class a constructor of .NET is: none, the message, or the message
  /// and the inner exception. The parameter name that <c>ArgumentOutOfRangeException(string)</c> takes
  /// is no message, and is refused.
  /// </summary>
  private IrConstructor LibraryConstructor(IrClassType library, IMethodSymbol constructor, SyntaxNode node) {
    var parameters = constructor.Parameters.Select(parameter => parameter.Type).ToList();
    var arity = parameters switch {
      [] => 0,
      [{ SpecialType: SpecialType.System_String }] when library != IrExceptionClass.ArgumentOutOfRangeException => 1,
      [{ SpecialType: SpecialType.System_String }, var inner] when LibraryClass(inner) == IrExceptionClass.Exception => 2,
      _ => -1
    };
    return arity < 0
      ? throw new UnsupportedException(node, $"the constructor {library.Name}({string.Join(", ", parameters.Select(parameter => parameter.Name))})")
      : IrExceptionClass.Constructor(library, arity);
  }

}
