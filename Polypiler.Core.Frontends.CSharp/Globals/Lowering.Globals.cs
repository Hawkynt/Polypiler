using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The static fields of the class that hold state: a <c>static</c> field, and a <c>static readonly</c>
/// one whose value is no constant or whose array elements are written. Each becomes a global of the
/// module. The class is initialised as .NET initialises one without a static constructor - in some
/// order before its first use - which is not the order a JavaScript module runs its declarations in;
/// so an initializer may read only constants and fields declared before it and call only methods that
/// touch no static field, and then the order cannot be seen.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The fields that become globals, known before any initializer is lowered.</summary>
  private readonly HashSet<ISymbol> _stateFields = new(SymbolEqualityComparer.Default);

  /// <summary>The fields that are globals, each once its initializer is lowered.</summary>
  private readonly Dictionary<ISymbol, IrGlobal> _globals = new(SymbolEqualityComparer.Default);

  /// <summary>The fields that become globals, in source order, with the file and the model they are read with.</summary>
  private readonly List<(string Path, SemanticModel Model, VariableDeclaratorSyntax Variable, IFieldSymbol Symbol, IrType Type)> _state = [];

  /// <summary>The globals in source order, each with the declaration its initializer stands in.</summary>
  private readonly List<(IrGlobal Global, string Path, VariableDeclaratorSyntax Variable)> _initialized = [];

  /// <summary>The <c>static readonly</c> arrays whose elements a method of the class writes.</summary>
  private readonly HashSet<ISymbol> _writtenArrays = new(SymbolEqualityComparer.Default);

  /// <summary>
  /// Finds the <c>static readonly</c> arrays whose elements are written: by an assignment, a compound
  /// assignment, <c>++</c> or <c>--</c> of an element, or as the array <c>Array.Fill</c> or
  /// <c>Array.Clear</c> fill or <c>Array.Copy</c> copies into. Such an array is state, not a table.
  /// </summary>
  private void FindWrittenArrays(IEnumerable<(string Path, SemanticModel Model, ClassDeclarationSyntax Type)> classes) {
    foreach (var (_, semantics, type) in classes)
      foreach (var name in type.DescendantNodes().OfType<IdentifierNameSyntax>())
        if (semantics.GetSymbolInfo(name).Symbol is IFieldSymbol { IsStatic: true, Type: IArrayTypeSymbol } field && IsElementWrite(semantics, name))
          this._writtenArrays.Add(field);
  }

  private static bool IsElementWrite(SemanticModel semantics, IdentifierNameSyntax name) {
    ExpressionSyntax array = name.Parent is MemberAccessExpressionSyntax member && member.Name == name ? member : name;
    switch (array.Parent) {
      case ElementAccessExpressionSyntax element when element.Expression == array:
        return element.Parent switch {
          AssignmentExpressionSyntax assignment => assignment.Left == element,
          PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => element.Parent.Kind()
            is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression,
          _ => false
        };
      case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } list } argument
        when semantics.GetSymbolInfo(call).Symbol is IMethodSymbol { ContainingType.SpecialType: SpecialType.System_Array } method:
        var position = list.Arguments.IndexOf(argument);
        return method.Name switch {
          "Fill" or "Clear" => position == 0,
          "Copy" => position == 2,
          _ => false
        };
      default:
        return false;
    }
  }

  /// <summary>
  /// Whether a field holds state: any <c>static</c> field that is not <c>readonly</c>, and a
  /// <c>static readonly</c> one whose initializer is no constant, or no list of constants, or whose
  /// elements are written.
  /// </summary>
  private bool IsState(FieldDeclarationSyntax field, VariableDeclaratorSyntax variable, IFieldSymbol symbol) {
    if (!field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
      return true;

    if (variable.Initializer?.Value is not { } initializer)
      return true;

    if (symbol.Type is not IArrayTypeSymbol)
      return !model.GetConstantValue(initializer).HasValue;

    IEnumerable<ExpressionSyntax>? elements = initializer switch {
      InitializerExpressionSyntax list => list.Expressions,
      ArrayCreationExpressionSyntax { Initializer: { } list } => list.Expressions,
      ImplicitArrayCreationExpressionSyntax creation => creation.Initializer.Expressions,
      CollectionExpressionSyntax collection when collection.Elements.All(element => element is ExpressionElementSyntax)
        => collection.Elements.Cast<ExpressionElementSyntax>().Select(element => element.Expression),
      _ => null
    };
    return this._writtenArrays.Contains(symbol) || elements is null || !elements.All(element => model.GetConstantValue(element).HasValue);
  }

  /// <summary>
  /// Takes note of a field that holds state. A public one is refused: JavaScript code that imports a
  /// module cannot assign what it exports, and the field made private would no longer be what the
  /// class shows. A struct is not state yet.
  /// </summary>
  private void State(FieldDeclarationSyntax field, VariableDeclaratorSyntax variable, IFieldSymbol symbol, IrType type) {
    var readOnly = field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
    if (symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal && (!readOnly || type.IsArray))
      throw new UnsupportedException(variable, $"a public mutable static field {symbol.Name}");

    if ((type.Element ?? type).IsStruct)
      throw new UnsupportedException(variable, $"a static field of the type {type}");

    if (type.IsArray && variable.Initializer is null)
      throw new UnsupportedException(variable, $"an array field without an initializer, {symbol.Name}");

    // A string field without an initializer holds null, which no string of the IR is.
    if (type.IsString && variable.Initializer is null)
      throw new UnsupportedException(variable, $"a string field without an initializer, {symbol.Name}");

    this._stateFields.Add(symbol);
    this._state.Add((path, model, variable, symbol, type));
  }

  /// <summary>Lowers the initializers of the fields that hold state, in source order: each may read the ones before it.</summary>
  private void Globals() {
    foreach (var (file, semantics, variable, symbol, type) in this._state) {
      path = file;
      model = semantics;
      try {
        IrExpr? initializer = variable.Initializer?.Value switch {
          null => null,
          InitializerExpressionSyntax list => this.ArrayOf(type, list),
          { } value => this.Value(value)
        };
        var global = new IrGlobal(symbol.Name, type, initializer);
        this._globals[symbol] = global;
        this._initialized.Add((global, file, variable));
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
      }
    }
  }

  /// <summary>
  /// Refuses an initializer that calls a method that reads or writes a static field, directly or
  /// through the methods it calls: what it saw would depend on the order of initialisation.
  /// </summary>
  private void CheckInitializers(string name, IReadOnlyList<IrFunction> functions) {
    var impure = IrVerifier.Impure(new IrModule(name, functions));
    foreach (var (global, file, variable) in this._initialized) {
      if (IrTree.Nodes(global.Initializer!).OfType<IrCall>().FirstOrDefault(call => impure.Contains(call.Function)) is not { } call)
        continue;

      path = file;
      this.Report(new UnsupportedException(variable.Initializer!.Value, $"an initializer that calls {call.Function}, which reads or writes a static field"));
    }
  }

  /// <summary>
  /// The global a name or a member access reads, <c>field</c> or <c>Class.field</c>, or
  /// <see langword="null"/> when it reads none. A field whose initializer is not lowered yet is one
  /// declared after the initializer being lowered, or that initializer's own field.
  /// </summary>
  private IrGlobal? GlobalOf(ExpressionSyntax expression) {
    if (model.GetSymbolInfo(expression).Symbol is not IFieldSymbol field || !this._stateFields.Contains(field))
      return null;

    return this._globals.TryGetValue(field, out var global)
      ? global
      : throw new UnsupportedException(expression, $"an initializer that reads the field {field.Name}, which is not initialised before it");
  }

}
