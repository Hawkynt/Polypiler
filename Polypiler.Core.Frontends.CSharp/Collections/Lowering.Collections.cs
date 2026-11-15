using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The collections of <c>System.Collections.Generic</c>: <c>List&lt;T&gt;</c> - its constructors,
/// collection initializers and collection expressions, <c>Add</c>, <c>Insert</c>, <c>RemoveAt</c>,
/// <c>Clear</c>, <c>Count</c>, the indexer, <c>Contains</c>, <c>IndexOf</c>, <c>ToArray</c> - and
/// <c>foreach</c> over one, which checks for changes as .NET's enumerator does. Every other member and
/// every other collection type is refused by name.
/// </summary>
internal sealed partial class Lowering {

  private const string GenericCollections = "System.Collections.Generic";

  /// <summary>The metadata name of a generic collection type of <c>System.Collections.Generic</c> - <c>List`1</c> - or <see langword="null"/>.</summary>
  private static string? CollectionName(ITypeSymbol? type)
    => type is INamedTypeSymbol { IsGenericType: true } named && named.ContainingNamespace.ToDisplayString() == GenericCollections ? named.OriginalDefinition.MetadataName : null;

  /// <summary>The IR type of a collection the IR has, or <see langword="null"/> for any other type.</summary>
  private IrType? CollectionType(ITypeSymbol? type, SyntaxNode node) {
    switch (CollectionName(type)) {
      case "List`1":
        var element = this.Map(((INamedTypeSymbol)type!).TypeArguments[0], node);
        var list = IrType.List(element);
        return list.IsStorable ? list : throw new UnsupportedException(node, $"a list of {element}");
      default:
        return null;
    }
  }

  /// <summary>The members of a list the IR has, and the operations they are.</summary>
  private static readonly Dictionary<string, IrCollectionOp> _listMethods = new() {
    ["Add"] = IrCollectionOp.ListAdd,
    ["Insert"] = IrCollectionOp.ListInsert,
    ["RemoveAt"] = IrCollectionOp.ListRemoveAt,
    ["Clear"] = IrCollectionOp.ListClear,
    ["Contains"] = IrCollectionOp.ListContains,
    ["IndexOf"] = IrCollectionOp.ListIndexOfEquals,
    ["ToArray"] = IrCollectionOp.ListCopyToArray
  };

  /// <summary>An operation on a collection, of the type its signature gives.</summary>
  private static IrCollection CollectionOp(IrCollectionOp op, IrType collection, params IrExpr[] arguments)
    => new(op, IrCollections.Signature(op, collection).Result!, arguments);

  /// <summary>
  /// What a collection gives as a value - a creation, a count, an element, the value of a method - or
  /// <see langword="null"/> where the expression is about no collection.
  /// </summary>
  private IrExpr? CollectionNatural(ExpressionSyntax expression, IrType type) {
    switch (expression) {
      case BaseObjectCreationExpressionSyntax { Initializer: { } initializer } when type.IsCollection:
        throw new UnsupportedException(initializer, "a collection initializer other than the value of a variable or a return");
      case BaseObjectCreationExpressionSyntax creation when type.IsList:
        return this.ListCreation(creation, type);
      case MemberAccessExpressionSyntax member when this.CollectionOf(member.Expression) is { } owner:
        var count = owner.Kind switch {
          IrTypeKind.List => IrCollectionOp.ListCount,
          IrTypeKind.Map => IrCollectionOp.MapCount,
          _ => IrCollectionOp.SetCount
        };
        return member.Name.Identifier.Text == "Count"
          ? new IrCollection(count, IrType.I32, [this.Read(member.Expression)])
          : throw new UnsupportedException(member, Member(model.GetTypeInfo(member.Expression).Type!, member.Name.Identifier.Text));
      case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element when this.CollectionOf(element.Expression) is { IsList: true } list:
        return CollectionOp(IrCollectionOp.ListGet, list, this.Read(element.Expression), this.Index(element.ArgumentList.Arguments[0].Expression));
      case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation when this.CollectionOf(member.Expression) is { } owner:
        return this.CollectionCall(invocation, member, owner);
      default:
        return null;
    }
  }

  /// <summary>The collection type of an expression, or <see langword="null"/> when it is no collection.</summary>
  private IrType? CollectionOf(ExpressionSyntax expression)
    => CollectionName(model.GetTypeInfo(expression).Type) is "List`1" && this.Map(model.GetTypeInfo(expression).Type, expression) is { IsCollection: true } type ? type : null;

  /// <summary>A member of a collection type as a diagnostic names it: <c>List&lt;int&gt;.Sort</c>.</summary>
  private static string Member(ITypeSymbol type, string name) => $"{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{name}";

  /// <summary><c>new List&lt;T&gt;()</c> and <c>new List&lt;T&gt;(array)</c>; a capacity is refused.</summary>
  private IrExpr ListCreation(BaseObjectCreationExpressionSyntax creation, IrType type) {
    var arguments = creation.ArgumentList?.Arguments ?? [];
    if (arguments.Count == 0)
      return CollectionOp(IrCollectionOp.ListNew, type);

    var source = this.Read(arguments[0].Expression);
    return arguments.Count == 1 && source.Type == IrType.Array(type.Element!)
      ? CollectionOp(IrCollectionOp.ListFromArray, type, source)
      : throw new UnsupportedException(creation, $"{Member(model.GetTypeInfo(creation).Type!, "List")}({string.Join(", ", arguments.Select(argument => model.GetTypeInfo(argument.Expression).Type?.ToDisplayString()))})");
  }

  /// <summary>A method of a collection the IR has; any other is refused by name.</summary>
  private IrExpr CollectionCall(InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax member, IrType owner) {
    var name = member.Name.Identifier.Text;
    var arguments = invocation.ArgumentList.Arguments;
    if (owner.IsList && _listMethods.TryGetValue(name, out var op) && arguments.Count == IrCollections.Signature(op, owner).Operands.Count) {
      var target = this.Read(member.Expression);
      return CollectionOp(op, owner, [target, .. arguments.Select(argument => this.Value(argument.Expression))]);
    }

    throw new UnsupportedException(invocation, Member(model.GetTypeInfo(member.Expression).Type!, name));
  }

  /// <summary>
  /// A collection with its elements, as the value of a variable or of a return: a creation with a
  /// collection initializer, or a collection expression. The collection is made in a temporary, or in
  /// the variable declared, and every element added after; <see langword="null"/> for any other value.
  /// </summary>
  private (IrExpr Value, List<IrStmt> After)? CollectionInitialized(ExpressionSyntax value, List<IrStmt> into, IrLocal? declared) {
    IrType type;
    IEnumerable<ExpressionSyntax> elements;
    IrExpr start;
    switch (value) {
      case BaseObjectCreationExpressionSyntax { Initializer: { } initializer } creation when this.Map(model.GetTypeInfo(creation).Type, creation) is { IsList: true } list:
        type = list;
        start = this.ListCreation(creation, list);
        elements = initializer.Expressions;
        break;
      case CollectionExpressionSyntax collection when this.Map(model.GetTypeInfo(collection).ConvertedType, collection) is { IsList: true } list:
        type = list;
        start = CollectionOp(IrCollectionOp.ListNew, list);
        elements = collection.Elements.Select(element => element is ExpressionElementSyntax plain
          ? plain.Expression
          : throw new UnsupportedException(element, "a spread in a collection expression"));
        break;
      default:
        return null;
    }

    var target = declared ?? this.Local($"$t{this._locals.Count}", type);
    var adds = elements.Select(element => (IrStmt)new IrExprStmt(CollectionOp(IrCollectionOp.ListAdd, type, new IrLocalRef(target), this.Value(element)))).ToList();
    if (declared is not null)
      return (start, adds);

    into.Add(new IrDeclare(target, start));
    into.AddRange(adds);
    return (new IrLocalRef(target), []);
  }

  /// <summary>The element of a list as the target of an assignment or a step: read and written at one index evaluated once.</summary>
  private (Func<IrExpr> Read, Func<IrExpr, IrStmt> Write)? CollectionTarget(ExpressionSyntax target, List<IrStmt> into, bool alsoRead) {
    if (target is not ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element || this.CollectionOf(element.Expression) is not { IsList: true } list)
      return null;

    var collection = this.Read(element.Expression);
    var index = this.Index(element.ArgumentList.Arguments[0].Expression);
    if (alsoRead) {
      collection = this.Once(collection, into);
      index = this.Once(index, into);
    }

    return (() => CollectionOp(IrCollectionOp.ListGet, list, collection, index), value => new IrExprStmt(CollectionOp(IrCollectionOp.ListSet, list, collection, index, value)));
  }

  /// <summary>
  /// A <c>foreach</c> over a collection: a checked loop, as .NET's enumerators check for changes. The
  /// element is converted to the type of the iteration variable where the source says so.
  /// </summary>
  private IrStmt? CollectionForEach(ForEachStatementSyntax loop) {
    if (this.CollectionOf(loop.Expression) is not { } type)
      return null;

    var collection = this.Read(loop.Expression);
    var symbol = model.GetDeclaredSymbol(loop)!;
    var declaredType = this.Map(symbol.Type, loop.Type);
    var element = type.Kind == IrTypeKind.Map ? throw new UnsupportedException(loop, "a foreach over a dictionary") : type.Element!;
    var item = this.Local(declaredType == element ? symbol.Name : $"$t{this._locals.Count}", element);
    var body = new List<IrStmt>();
    if (declaredType == element) {
      this._variables[symbol] = item;
    } else {
      var converted = this.Local(symbol.Name, declaredType);
      this._variables[symbol] = converted;
      body.Add(new IrDeclare(converted, Convert(new IrLocalRef(item), declaredType, this.IsChecked(loop))));
    }

    body.AddRange(this.Body(loop.Statement).Statements);
    return new IrForEachCollection(collection, EnumerationMode.Checked, item, null, new IrBlock(body));
  }

}
