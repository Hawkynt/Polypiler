using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>The type of an expression and the type the compiler converts it to, closed under the type arguments of a <see cref="BoundModel"/>.</summary>
/// <param name="Type">The type of the expression.</param>
/// <param name="ConvertedType">The type it is converted to.</param>
internal readonly record struct BoundType(ITypeSymbol? Type, ITypeSymbol? ConvertedType);

/// <summary>The symbol a name or a call binds to, closed under the type arguments of a <see cref="BoundModel"/>.</summary>
/// <param name="Symbol">The symbol, or <see langword="null"/>.</param>
internal readonly record struct BoundSymbol(ISymbol? Symbol);

/// <summary>
/// The semantic model of a tree as one instantiation of the generic definitions in it sees it: every
/// type and symbol the compiler bound in terms of a type parameter is answered with the type
/// argument in its place (monomorphization), so that the lowering only ever meets closed types -
/// <c>double</c> for <c>T</c>, <c>Ring&lt;long&gt;.Push</c> for <c>Ring&lt;T&gt;.Push</c>. Without
/// type arguments it answers what the compiler bound.
/// </summary>
internal sealed class BoundModel {

  private readonly IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> _arguments;

  /// <summary>The model of a tree as the compiler bound it.</summary>
  /// <param name="inner">The semantic model.</param>
  public BoundModel(SemanticModel inner) : this(inner, new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default)) { }

  private BoundModel(SemanticModel inner, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> arguments) {
    this.Inner = inner;
    this._arguments = arguments;
  }

  /// <summary>The semantic model as the compiler bound it, open types and all.</summary>
  public SemanticModel Inner { get; }

  /// <summary>The compilation of the tree.</summary>
  public Compilation Compilation => this.Inner.Compilation;

  /// <summary>The model of the same tree with type arguments for type parameters, added to those it has.</summary>
  /// <param name="arguments">The type argument of every type parameter.</param>
  /// <returns>The model of that instantiation.</returns>
  public BoundModel With(IEnumerable<(ITypeParameterSymbol Parameter, ITypeSymbol Argument)> arguments) {
    var all = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(this._arguments, SymbolEqualityComparer.Default);
    foreach (var (parameter, argument) in arguments)
      all[parameter] = argument;

    return new(this.Inner, all);
  }

  /// <summary>The same instantiation in the model of another tree of the compilation.</summary>
  /// <param name="tree">The tree.</param>
  /// <returns>Its model with the type arguments of this one.</returns>
  public BoundModel Of(SyntaxTree tree) => new(this.Compilation.GetSemanticModel(tree), this._arguments);

  public BoundType GetTypeInfo(SyntaxNode node) {
    var info = ModelExtensions.GetTypeInfo(this.Inner, node);
    return new(this.Close(info.Type), this.Close(info.ConvertedType));
  }

  public BoundSymbol GetSymbolInfo(SyntaxNode node) => new(this.Close(ModelExtensions.GetSymbolInfo(this.Inner, node).Symbol));

  public Optional<object?> GetConstantValue(SyntaxNode node) => this.Inner.GetConstantValue(node);

  public INamedTypeSymbol? GetDeclaredSymbol(BaseTypeDeclarationSyntax node) => (INamedTypeSymbol?)this.Close(this.Inner.GetDeclaredSymbol(node));

  public IMethodSymbol? GetDeclaredSymbol(BaseMethodDeclarationSyntax node) => (IMethodSymbol?)this.Close(this.Inner.GetDeclaredSymbol(node));

  public IPropertySymbol? GetDeclaredSymbol(PropertyDeclarationSyntax node) => (IPropertySymbol?)this.Close(this.Inner.GetDeclaredSymbol(node));

  public ISymbol? GetDeclaredSymbol(VariableDeclaratorSyntax node) => this.Close(this.Inner.GetDeclaredSymbol(node));

  public ISymbol? GetDeclaredSymbol(SingleVariableDesignationSyntax node) => this.Close(this.Inner.GetDeclaredSymbol(node));

  public ILocalSymbol? GetDeclaredSymbol(ForEachStatementSyntax node) => this.Inner.GetDeclaredSymbol(node);

  public INamedTypeSymbol? GetDeclaredSymbol(DelegateDeclarationSyntax node) => (INamedTypeSymbol?)this.Close(this.Inner.GetDeclaredSymbol(node));

  public IMethodSymbol? GetDeclaredSymbol(LocalFunctionStatementSyntax node) => (IMethodSymbol?)this.Close(this.Inner.GetDeclaredSymbol(node));

  /// <summary>Whether the compiler bound a type written in the source to a type parameter, which the instantiation replaces.</summary>
  /// <param name="node">The type as written, or an expression of it.</param>
  /// <returns><see langword="true"/> for a type parameter.</returns>
  public bool IsTypeParameter(SyntaxNode node) => ModelExtensions.GetTypeInfo(this.Inner, node).Type is ITypeParameterSymbol;

  /// <summary>A type with every type parameter replaced by its type argument.</summary>
  /// <param name="type">The type.</param>
  /// <returns>The closed type; the type itself when there is nothing to replace.</returns>
  public ITypeSymbol? Close(ITypeSymbol? type) {
    if (type is null || this._arguments.Count == 0)
      return type;

    switch (type) {
      case ITypeParameterSymbol parameter:
        return this._arguments.TryGetValue(parameter, out var argument) ? argument : parameter;
      case IArrayTypeSymbol array:
        var element = this.Close(array.ElementType)!;
        return SymbolEqualityComparer.Default.Equals(element, array.ElementType) ? array : this.Compilation.CreateArrayTypeSymbol(element, array.Rank);
      case INamedTypeSymbol { IsGenericType: true } named:
        var arguments = named.TypeArguments.Select(this.Close).ToArray();
        return arguments.Zip(named.TypeArguments).All(pair => SymbolEqualityComparer.Default.Equals(pair.First, pair.Second))
          ? named
          : named.OriginalDefinition.Construct(arguments!);
      default:
        return type;
    }
  }

  /// <summary>
  /// A symbol of a member of an open type - a method, a constructor, a field, a property or a
  /// parameter - as the member of the closed type, and a generic method with its type arguments
  /// closed; a local and a closed symbol stay as they are.
  /// </summary>
  /// <param name="symbol">The symbol.</param>
  /// <returns>The closed symbol.</returns>
  public ISymbol? Close(ISymbol? symbol) {
    if (symbol is null || this._arguments.Count == 0)
      return symbol;

    switch (symbol) {
      case ITypeSymbol type:
        return this.Close(type);
      case IParameterSymbol { ContainingSymbol: IMethodSymbol method } parameter:
        return this.Close(method) is IMethodSymbol closed && !SymbolEqualityComparer.Default.Equals(closed, method) ? closed.Parameters[parameter.Ordinal] : parameter;
      case IMethodSymbol or IFieldSymbol or IPropertySymbol when symbol.ContainingType is { } owner:
        var member = symbol;
        if (this.Close(owner) is INamedTypeSymbol closedOwner && !SymbolEqualityComparer.Default.Equals(closedOwner, owner))
          member = closedOwner.GetMembers(symbol.Name).First(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, symbol.OriginalDefinition));

        if (symbol is IMethodSymbol { IsGenericMethod: true } generic) {
          var arguments = generic.TypeArguments.Select(argument => this.Close(argument)!).ToArray();
          member = ((IMethodSymbol)member).ConstructedFrom.Construct(arguments);
        }

        return member;
      default:
        return symbol;
    }
  }

}
