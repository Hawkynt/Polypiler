using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  private const string CollectionsNamespace = "global::System.Collections.Generic.";

  /// <summary>
  /// The last element of a list of values removed, or <c>null</c> for an empty list: JavaScript's
  /// <c>pop</c> on a list of numbers, truth values or structs.
  /// </summary>
  internal static CSharpHelper PopValue { get; } = new("PopValue", "T?", CollectionsNamespace + "List<T> list", [
    "if (list.Count == 0) {",
    "  return null;",
    "}",
    "T last = list[list.Count - 1];",
    "list.RemoveAt(list.Count - 1);",
    "return last;"
  ], "<T>", "where T : struct");

  /// <summary>The last element of a list of references removed, or <c>null</c> for an empty list.</summary>
  internal static CSharpHelper PopReference { get; } = new("PopReference", "T", CollectionsNamespace + "List<T> list", [
    "if (list.Count == 0) {",
    "  return null;",
    "}",
    "T last = list[list.Count - 1];",
    "list.RemoveAt(list.Count - 1);",
    "return last;"
  ], "<T>", "where T : class");

  /// <summary>JavaScript's <c>includes</c> on a list of references: by identity, where <c>Contains</c> calls <c>Equals</c>, which a record class has of its own.</summary>
  internal static CSharpHelper ContainsReference { get; } = new("ContainsReference", "bool", CollectionsNamespace + "List<T> list, T value", [
    "for (int index = 0; index < list.Count; ++index) {",
    "  if (object.ReferenceEquals(list[index], value)) {",
    "    return true;",
    "  }",
    "}",
    "return false;"
  ], "<T>", "where T : class");

  /// <summary>JavaScript's <c>indexOf</c> on a list of <c>double</c>: <c>==</c>, under which a NaN is never found and <c>-0</c> is <c>0</c>.</summary>
  internal static CSharpHelper IndexOfStrictDouble { get; } = IndexOfStrict("IndexOfStrictDouble", "double");

  /// <summary>JavaScript's <c>indexOf</c> on a list of <c>float</c>.</summary>
  internal static CSharpHelper IndexOfStrictSingle { get; } = IndexOfStrict("IndexOfStrictSingle", "float");

  private static CSharpHelper IndexOfStrict(string name, string element) => new(name, "int", $"{CollectionsNamespace}List<{element}> list, {element} value", [
    "for (int index = 0; index < list.Count; ++index) {",
    "  if (list[index] == value) {",
    "    return index;",
    "  }",
    "}",
    "return -1;"
  ]);

  private sealed partial class Writer {

    /// <summary>The C# type of a collection: <c>List&lt;T&gt;</c> from <c>System.Collections.Generic</c>.</summary>
    private string CollectionTypeName(IrType type) => type.IsList ? $"{CollectionsNamespace}List<{this.TypeName(type.Element!)}>" : this.KeyedTypeName(type);

    /// <summary>A name no variable of the function has, for a variable the backend declares in the innermost block.</summary>
    private string Fresh(string stem) {
      var name = stem;
      for (var index = 1; !this._names.Add(name); ++index)
        name = $"{stem}_{index}";

      this._scopes[^1].Own.Add(name);
      return name;
    }

    /// <summary>
    /// An operation on a list as the methods, the indexer and the properties of <c>List&lt;T&gt;</c>,
    /// which every profile has; <c>pop</c> and JavaScript's <c>indexOf</c> on floating-point numbers
    /// through helpers.
    /// </summary>
    private (CsExpression Node, int Binding) Collection(IrCollection operation) {
      if (IrCollections.Kind(operation.Op) != IrTypeKind.List)
        return this.Keyed(operation);

      var arguments = operation.Arguments;
      switch (operation.Op) {
        case IrCollectionOp.ListNew:
          return (new CsObjectCreation(new CsType(this.TypeName(operation.Type)), []), Primary);
        case IrCollectionOp.ListFromArray:
          return (new CsObjectCreation(new CsType(this.TypeName(operation.Type)), [this.Stored(arguments[0])]), Primary);
        case IrCollectionOp.ListCount:
          return (new CsMemberAccess(this.Operand(arguments[0], Primary), "Count"), Primary);
        case IrCollectionOp.ListGet:
          return (new CsElementAccess(this.Operand(arguments[0], Primary), this.Stored(arguments[1])), Primary);
        case IrCollectionOp.ListSet:
          var element = new CsElementAccess(this.Operand(arguments[0], Primary), this.Stored(arguments[1])) { Type = operation.Arguments[2].Type };
          return (new CsAssignment(element, "=", this.Stored(arguments[2])), Loose);
        case IrCollectionOp.ListRemoveLast:
          var helper = this.Helper(arguments[0].Type.Element!.IsObject ? PopReference : PopValue);
          return (new CsInvocation(CSharpFragments.Name(helper), [this.Stored(arguments[0])]), Primary);
        case IrCollectionOp.ListIncludesSameValueZero when arguments[0].Type.Element!.IsObject:
          var identity = this.Helper(ContainsReference);
          return (new CsInvocation(CSharpFragments.Name(identity), [this.Stored(arguments[0]), this.Stored(arguments[1])]), Primary);
        case IrCollectionOp.ListIndexOfStrict when arguments[0].Type.Element!.IsFloat:
          var strict = this.Helper(arguments[0].Type.Element == IrType.F32 ? IndexOfStrictSingle : IndexOfStrictDouble);
          return (new CsInvocation(CSharpFragments.Name(strict), [this.Stored(arguments[0]), this.Stored(arguments[1])]), Primary);
      }

      var method = operation.Op switch {
        IrCollectionOp.ListAdd => "Add",
        IrCollectionOp.ListInsert => "Insert",
        IrCollectionOp.ListRemoveAt => "RemoveAt",
        IrCollectionOp.ListClear => "Clear",
        IrCollectionOp.ListContains or IrCollectionOp.ListIncludesSameValueZero => "Contains",
        IrCollectionOp.ListIndexOfEquals or IrCollectionOp.ListIndexOfStrict => "IndexOf",
        _ => "ToArray"
      };
      var target = new CsMemberAccess(this.Operand(arguments[0], Primary), method);
      return (new CsInvocation(target, [.. arguments.Skip(1).Select(this.Stored)]), Primary);
    }

    /// <summary>
    /// A loop over a collection. A checked loop is C#'s <c>foreach</c>, whose enumerator checks as the
    /// IR says; a live one walks a list by index, reading its count before every pass.
    /// </summary>
    private void ForEachCollection(IrForEachCollection loop, List<CsStatement> statements) {
      var collection = this.Text(loop.Collection);
      var scope = new Scope();
      this._scopes.Add(scope);
      var element = loop.Element!;
      if (!loop.Collection.Type.IsList) {
        this.KeyedLoop(loop, statements, collection);
      } else if (loop.Mode == EnumerationMode.Checked) {
        var name = this.Declare(element);
        var body = this.Body(loop.Body, null);
        statements.Add(new CsForEach(new CsType(this.TypeName(element.Type)), name, collection, new CsBlock(body)));
      } else {
        // The list is evaluated once; the counter reads its count before every pass.
        var list = loop.Collection is IrLocalRef ? collection : new CsName(this.Fresh("list")) { Type = loop.Collection.Type };
        if (list != collection)
          statements.Add(new CsLocalDeclaration(new CsType(this.TypeName(loop.Collection.Type)), ((CsName)list).Identifier, collection));

        var index = this.Fresh("index");
        var name = this.Declare(element);
        var read = new CsLocalDeclaration(new CsType(this.TypeName(element.Type)), name, new CsElementAccess(list, new CsName(index)));
        var body = this.Body(loop.Body, null);
        var condition = new CsBinary(new CsName(index), "<", new CsMemberAccess(list, "Count"));
        statements.Add(new CsFor(new CsLocalDeclaration(new CsType("int"), index, new CsIntegerLiteral(0)), condition,
          [new CsPrefixUnary("++", new CsName(index))], new CsBlock([read, .. body])));
      }

      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(scope.Within);
    }

  }

}
