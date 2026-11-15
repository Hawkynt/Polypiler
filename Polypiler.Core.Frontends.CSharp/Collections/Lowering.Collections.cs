using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The collections of <c>System.Collections.Generic</c>. <c>List&lt;T&gt;</c>: its constructors,
/// collection initializers and collection expressions, <c>Add</c>, <c>Insert</c>, <c>RemoveAt</c>,
/// <c>Clear</c>, <c>Count</c>, the indexer, <c>Contains</c>, <c>IndexOf</c>, <c>ToArray</c>.
/// <c>Dictionary&lt;TKey, TValue&gt;</c>, whose order is that of its slots: initializers, the indexer,
/// <c>Add</c>, <c>TryAdd</c>, <c>TryGetValue</c> as the condition of an <c>if</c>, <c>ContainsKey</c>,
/// <c>Remove</c>, <c>Clear</c>, <c>Count</c>, <c>Keys</c>, <c>Values</c> and its entries with
/// <c>Key</c> and <c>Value</c> or deconstructed. <c>HashSet&lt;T&gt;</c>: <c>Add</c>, <c>Remove</c>,
/// <c>Contains</c>, <c>Clear</c>, <c>Count</c>. A <c>foreach</c> over any of them checks for changes as
/// .NET's enumerators do. Every other member and every other collection type is refused by name.
/// </summary>
internal sealed partial class Lowering {

  private const string GenericCollections = "System.Collections.Generic";

  /// <summary>The key and the value an entry of a dictionary enumerated by <c>foreach</c> stands for, by its variable.</summary>
  private readonly Dictionary<ISymbol, (IrLocal Key, IrLocal Value)> _entries = new(SymbolEqualityComparer.Default);

  /// <summary>The metadata name of a generic collection type of <c>System.Collections.Generic</c> - <c>List`1</c> - or <see langword="null"/>.</summary>
  private static string? CollectionName(ITypeSymbol? type)
    => type is INamedTypeSymbol { IsGenericType: true } named && named.ContainingNamespace.ToDisplayString() == GenericCollections ? named.OriginalDefinition.MetadataName : null;

  /// <summary>The IR type of a collection the IR has, or <see langword="null"/> for any other type.</summary>
  private IrType? CollectionType(ITypeSymbol? type, SyntaxNode node) {
    var arguments = type is INamedTypeSymbol named ? named.TypeArguments : [];
    IrType collection;
    switch (CollectionName(type)) {
      case "List`1":
        collection = IrType.List(this.Map(arguments[0], node));
        break;
      case "Dictionary`2":
        collection = IrType.Map(this.Map(arguments[0], node), this.Map(arguments[1], node), MapOrder.SlotReuse);
        break;
      case "HashSet`1":
        collection = IrType.Set(this.Map(arguments[0], node), MapOrder.SlotReuse);
        break;
      default:
        return null;
    }

    return collection.IsStorable ? collection : throw new UnsupportedException(node, type!.ToDisplayString());
  }

  /// <summary>The methods of a list the IR has, and the operations they are.</summary>
  private static readonly Dictionary<string, IrCollectionOp> _listMethods = new() {
    ["Add"] = IrCollectionOp.ListAdd,
    ["Insert"] = IrCollectionOp.ListInsert,
    ["RemoveAt"] = IrCollectionOp.ListRemoveAt,
    ["Clear"] = IrCollectionOp.ListClear,
    ["Contains"] = IrCollectionOp.ListContains,
    ["IndexOf"] = IrCollectionOp.ListIndexOfEquals,
    ["ToArray"] = IrCollectionOp.ListCopyToArray
  };

  /// <summary>The methods of a dictionary the IR has, and the operations they are.</summary>
  private static readonly Dictionary<string, IrCollectionOp> _mapMethods = new() {
    ["Add"] = IrCollectionOp.MapAdd,
    ["TryAdd"] = IrCollectionOp.MapTryAdd,
    ["ContainsKey"] = IrCollectionOp.MapContainsKey,
    ["Remove"] = IrCollectionOp.MapRemove,
    ["Clear"] = IrCollectionOp.MapClear
  };

  /// <summary>The methods of a set the IR has, and the operations they are.</summary>
  private static readonly Dictionary<string, IrCollectionOp> _setMethods = new() {
    ["Add"] = IrCollectionOp.SetAdd,
    ["Remove"] = IrCollectionOp.SetRemove,
    ["Contains"] = IrCollectionOp.SetContains,
    ["Clear"] = IrCollectionOp.SetClear
  };

  /// <summary>An operation on a collection, of the type its signature gives.</summary>
  private static IrCollection CollectionOp(IrCollectionOp op, IrType collection, params IrExpr[] arguments)
    => new(op, IrCollections.Signature(op, collection).Result!, arguments);

  /// <summary>
  /// What a collection gives as a value - a creation, a count, an element, the value of a method, the
  /// key or the value of an entry - or <see langword="null"/> where the expression is about no collection.
  /// </summary>
  private IrExpr? CollectionNatural(ExpressionSyntax expression, IrType type) {
    switch (expression) {
      case BaseObjectCreationExpressionSyntax { Initializer: { } initializer } when type.IsCollection:
        throw new UnsupportedException(initializer, "a collection initializer other than the value of a variable or a return");
      case BaseObjectCreationExpressionSyntax creation when type.IsCollection:
        return this.CollectionCreation(creation, type);
      // Any other use of an entry has its type, KeyValuePair, which the IR has not and which is refused by name.
      case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax entry, Name.Identifier.Text: var part } when part is "Key" or "Value" && model.GetSymbolInfo(entry).Symbol is { } symbol
          && this._entries.TryGetValue(symbol, out var pair):
        return new IrLocalRef(part == "Key" ? pair.Key : pair.Value);
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
      case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element when this.CollectionOf(element.Expression) is { Kind: IrTypeKind.Map } map:
        return CollectionOp(IrCollectionOp.MapGet, map, this.Read(element.Expression), this.Value(element.ArgumentList.Arguments[0].Expression));
      case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation when this.CollectionOf(member.Expression) is { } owner:
        return this.CollectionCall(invocation, member, owner);
      default:
        return null;
    }
  }

  /// <summary>The collection type of an expression, or <see langword="null"/> when it is no collection.</summary>
  private IrType? CollectionOf(ExpressionSyntax expression)
    => CollectionName(model.GetTypeInfo(expression).Type) is "List`1" or "Dictionary`2" or "HashSet`1"
      && this.Map(model.GetTypeInfo(expression).Type, expression) is { IsCollection: true } type ? type : null;

  /// <summary>A member of a collection type as a diagnostic names it: <c>List&lt;int&gt;.Sort</c>.</summary>
  private static string Member(ITypeSymbol type, string name) => $"{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{name}";

  /// <summary>
  /// <c>new List&lt;T&gt;()</c>, <c>new List&lt;T&gt;(array)</c>, <c>new Dictionary&lt;TKey, TValue&gt;()</c>
  /// and <c>new HashSet&lt;T&gt;()</c>; a capacity, a comparer and any other argument is refused.
  /// </summary>
  private IrExpr CollectionCreation(BaseObjectCreationExpressionSyntax creation, IrType type) {
    var arguments = creation.ArgumentList?.Arguments ?? [];
    if (arguments.Count == 0)
      return CollectionOp(type.Kind switch { IrTypeKind.List => IrCollectionOp.ListNew, IrTypeKind.Map => IrCollectionOp.MapNew, _ => IrCollectionOp.SetNew }, type);

    var created = model.GetTypeInfo(creation).Type!;
    var refused = $"new {created.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}({string.Join(", ", arguments.Select(argument => model.GetTypeInfo(argument.Expression).Type?.ToDisplayString()))})";
    if (!type.IsList || arguments.Count != 1)
      throw new UnsupportedException(creation, refused);

    var source = this.Read(arguments[0].Expression);
    return source.Type == IrType.Array(type.Element!) ? CollectionOp(IrCollectionOp.ListFromArray, type, source) : throw new UnsupportedException(creation, refused);
  }

  /// <summary>A method of a collection the IR has; any other is refused by name.</summary>
  private IrExpr CollectionCall(InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax member, IrType owner) {
    var name = member.Name.Identifier.Text;
    var arguments = invocation.ArgumentList.Arguments;
    var methods = owner.Kind switch {
      IrTypeKind.List => _listMethods,
      IrTypeKind.Map => _mapMethods,
      _ => _setMethods
    };
    if (methods.TryGetValue(name, out var op) && arguments.Count == IrCollections.Signature(op, owner).Operands.Count
        && arguments.All(argument => argument.RefKindKeyword.IsKind(SyntaxKind.None))) {
      var target = this.Read(member.Expression);
      return CollectionOp(op, owner, [target, .. arguments.Select(argument => this.Value(argument.Expression))]);
    }

    throw new UnsupportedException(invocation, Member(model.GetTypeInfo(member.Expression).Type!, name));
  }

  /// <summary>
  /// A collection with its elements, as the value of a variable or of a return: a creation with a
  /// collection initializer, or a collection expression of a list. The collection is made in a
  /// temporary, or in the variable declared, and every element added after - to a dictionary by
  /// <c>Add</c> for <c>{ k, v }</c>, which traps for a key present, and by the indexer for
  /// <c>[k] = v</c>; <see langword="null"/> for any other value.
  /// </summary>
  private (IrExpr Value, List<IrStmt> After)? CollectionInitialized(ExpressionSyntax value, List<IrStmt> into, IrLocal? declared) {
    IrType type;
    IEnumerable<ExpressionSyntax> elements;
    IrExpr start;
    switch (value) {
      case BaseObjectCreationExpressionSyntax { Initializer: { } initializer } creation when this.Map(model.GetTypeInfo(creation).Type, creation) is { IsCollection: true } collection:
        type = collection;
        start = this.CollectionCreation(creation, collection);
        elements = initializer.Expressions;
        break;
      case CollectionExpressionSyntax expression when this.Map(model.GetTypeInfo(expression).ConvertedType, expression) is { IsList: true } list:
        type = list;
        start = CollectionOp(IrCollectionOp.ListNew, list);
        elements = expression.Elements.Select(element => element is ExpressionElementSyntax plain
          ? plain.Expression
          : throw new UnsupportedException(element, "a spread in a collection expression"));
        break;
      default:
        return null;
    }

    var target = declared ?? this.Local($"$t{this._locals.Count}", type);
    var reference = new IrLocalRef(target);
    var adds = elements.Select(element => (IrStmt)new IrExprStmt(type.Kind switch {
      IrTypeKind.List => CollectionOp(IrCollectionOp.ListAdd, type, reference, this.Value(element)),
      IrTypeKind.Set => CollectionOp(IrCollectionOp.SetAdd, type, reference, this.Value(element)),
      _ => element switch {
        InitializerExpressionSyntax { Expressions: [var key, var entry] } => CollectionOp(IrCollectionOp.MapAdd, type, reference, this.Value(key), this.Value(entry)),
        AssignmentExpressionSyntax { Left: ImplicitElementAccessSyntax { ArgumentList.Arguments: [var key] } } entry when entry.IsKind(SyntaxKind.SimpleAssignmentExpression)
          => CollectionOp(IrCollectionOp.MapPut, type, reference, this.Value(key.Expression), this.Value(entry.Right)),
        _ => throw new UnsupportedException(element, "a dictionary initializer entry other than { key, value } and [key] = value")
      }
    })).ToList();
    if (declared is not null)
      return (start, adds);

    into.Add(new IrDeclare(target, start));
    into.AddRange(adds);
    return (new IrLocalRef(target), []);
  }

  /// <summary>
  /// The element of a list or the value of a key of a dictionary as the target of an assignment or a
  /// step: read and written at one index or key evaluated once. A write to a dictionary inserts the
  /// key or replaces its value.
  /// </summary>
  private (Func<IrExpr> Read, Func<IrExpr, IrStmt> Write)? CollectionTarget(ExpressionSyntax target, List<IrStmt> into, bool alsoRead) {
    if (target is not ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element || this.CollectionOf(element.Expression) is not { Kind: IrTypeKind.List or IrTypeKind.Map } owner)
      return null;

    var collection = this.Read(element.Expression);
    var at = element.ArgumentList.Arguments[0].Expression;
    var index = owner.IsList ? this.Index(at) : this.Value(at);
    if (alsoRead) {
      collection = this.Once(collection, into);
      index = this.Once(index, into);
    }

    return owner.IsList
      ? (() => CollectionOp(IrCollectionOp.ListGet, owner, collection, index), value => new IrExprStmt(CollectionOp(IrCollectionOp.ListSet, owner, collection, index, value)))
      : (() => CollectionOp(IrCollectionOp.MapGet, owner, collection, index), value => new IrExprStmt(CollectionOp(IrCollectionOp.MapPut, owner, collection, index, value)));
  }

  /// <summary>
  /// <c>if (d.TryGetValue(k, out var v)) S else E</c>, also negated: the value is read where the key is
  /// present and the default value of its type assigned where it is not, as .NET assigns it. The out
  /// variable is declared in front of the <c>if</c>, where C# scopes it. <see langword="false"/> for any other statement.
  /// </summary>
  private bool TryGetIf(IfStatementSyntax conditional, List<IrStmt> into) {
    var condition = conditional.Condition;
    var negated = false;
    if (condition is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not) {
      condition = not.Operand;
      negated = true;
    }

    if (condition is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "TryGetValue" } member } invocation
        || this.CollectionOf(member.Expression) is not { Kind: IrTypeKind.Map } map
        || invocation.ArgumentList.Arguments is not [var key, { RefKindKeyword.RawKind: (int)SyntaxKind.OutKeyword, Expression: var output }])
      return false;

    var collection = this.Once(this.Read(member.Expression), into);
    var lookup = this.Once(this.Value(key.Expression), into);
    var value = map.Element!;
    IrLocal variable;
    switch (output) {
      case DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax designation }:
        var symbol = model.GetDeclaredSymbol(designation)!;
        variable = this.Local(symbol.Name, value);
        this._variables[symbol] = variable;
        into.Add(new IrDeclare(variable, null));
        break;
      case IdentifierNameSyntax when this.Value(output) is IrLocalRef { Local: var assigned } && assigned.Type == value:
        variable = assigned;
        break;
      default:
        throw new UnsupportedException(output, "an out argument of TryGetValue other than a variable of the value type");
    }

    IrStmt[] found = [new IrAssign(variable, CollectionOp(IrCollectionOp.MapGet, map, collection, lookup))];
    IrStmt[] missing = value.IsString ? [] : [new IrAssign(variable, this.Defaulted(value, output))];
    var then = this.Body(conditional.Statement).Statements;
    IReadOnlyList<IrStmt> otherwise = conditional.Else is null ? [] : this.Body(conditional.Else.Statement).Statements;
    var (whenFound, whenMissing) = negated ? (otherwise, then) : (then, otherwise);
    if (value.IsString && whenMissing.Count > 0)
      throw new UnsupportedException(output, "TryGetValue of a string that is read where the key is missing, which .NET sets to null");

    var test = CollectionOp(IrCollectionOp.MapContainsKey, map, collection, lookup);
    var present = new IrBlock([.. found, .. whenFound]);
    var absent = new IrBlock([.. missing, .. whenMissing]);
    into.Add(negated
      ? new IrIf(new IrUnary(IrUnaryOp.LogicalNot, test), absent, present.Statements.Count == 0 ? null : present)
      : new IrIf(test, present, absent.Statements.Count == 0 ? null : absent));
    return true;
  }

  /// <summary>The default value of a type, as .NET gives it to an out variable: zero, false, the default struct, null.</summary>
  private IrExpr Defaulted(IrType type, SyntaxNode node)
    => type.IsStruct ? this.Default(type, node)
      : type.IsObject ? new IrNull(type)
      : type.IsCollection ? throw new UnsupportedException(node, $"the default value of {type}, which is null")
      : new IrConst(type, type.IsFloat ? IrOperators.Bits(type, 0.0) : 0);

  /// <summary>
  /// A <c>foreach</c> over a collection: a checked loop, as .NET's enumerators check for changes. The
  /// element is converted to the type of the iteration variable where the source says so. Over a
  /// dictionary the variable is an entry, whose <c>Key</c> and <c>Value</c> are the two locals of the
  /// loop, or the pair it is deconstructed into; over its <c>Keys</c> or <c>Values</c> one of them.
  /// </summary>
  private IrStmt? CollectionForEach(CommonForEachStatementSyntax loop) {
    var source = loop.Expression;
    string? part = null;
    if (source is MemberAccessExpressionSyntax { Name.Identifier.Text: "Keys" or "Values" } keys && this.CollectionOf(keys.Expression) is { Kind: IrTypeKind.Map }) {
      part = keys.Name.Identifier.Text;
      source = keys.Expression;
    }

    if (this.CollectionOf(source) is not { } type)
      return null;

    var collection = this.Read(source);
    var body = new List<IrStmt>();
    IrLocal? element = null;
    IrLocal? value = null;
    if (type.Kind == IrTypeKind.Map && part is null) {
      element = this.Local($"$t{this._locals.Count}", type.Key!);
      value = this.Local($"$t{this._locals.Count}", type.Element!);
      switch (loop) {
        case ForEachStatementSyntax entry:
          this._entries[model.GetDeclaredSymbol(entry)!] = (element, value);
          break;
        case ForEachVariableStatementSyntax { Variable: DeclarationExpressionSyntax { Designation: ParenthesizedVariableDesignationSyntax { Variables: [SingleVariableDesignationSyntax k, SingleVariableDesignationSyntax v] } } }:
          this.Bound(model.GetDeclaredSymbol(k)!, element, body, loop, null);
          this.Bound(model.GetDeclaredSymbol(v)!, value, body, loop, null);
          break;
        default:
          throw new UnsupportedException(loop, "a foreach over a dictionary that deconstructs its entry into anything but two new variables");
      }
    } else if (loop is ForEachStatementSyntax single) {
      var symbol = model.GetDeclaredSymbol(single)!;
      var held = part == "Values" ? type.Element! : type.Key ?? type.Element!;
      // The variable is the local of the loop where it has the element type.
      var item = this.Local(this.Map(symbol.Type, single.Type) == held ? symbol.Name : $"$t{this._locals.Count}", held);
      if (part == "Values")
        value = item;
      else
        element = item;

      if (item.Name == symbol.Name)
        this._variables[symbol] = item;
      else
        this.Bound(symbol, item, body, loop, single.Type);
    } else {
      throw new UnsupportedException(loop, "a foreach that deconstructs an element");
    }

    body.AddRange(this.Body(loop.Statement).Statements);
    return new IrForEachCollection(collection, EnumerationMode.Checked, element, value, new IrBlock(body));
  }

  /// <summary>An iteration variable as a variable of its own that the element is converted to at the start of every pass.</summary>
  private void Bound(ISymbol symbol, IrLocal local, List<IrStmt> body, SyntaxNode loop, TypeSyntax? written) {
    var declared = this.Map(((ILocalSymbol)symbol).Type, (SyntaxNode?)written ?? loop);
    var converted = this.Local(symbol.Name, declared);
    this._variables[symbol] = converted;
    body.Add(new IrDeclare(converted, Convert(new IrLocalRef(local), declared, this.IsChecked(loop))));
  }

}
