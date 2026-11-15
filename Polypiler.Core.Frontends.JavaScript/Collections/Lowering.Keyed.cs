using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// <c>Map</c> and <c>Set</c>, which are maps and sets of the IR in the order of insertion (#1328):
/// <c>new Map()</c>, <c>new Map([[k, v], …])</c>, <c>new Set()</c>, <c>new Set([…])</c> and
/// <c>new Set(array)</c> as the value of a variable; <c>get</c> as an optional value, <c>set</c>,
/// <c>has</c>, <c>delete</c>, <c>clear</c>, <c>size</c>, <c>add</c>; <c>for…of</c> over a map
/// destructured into its key and value, over <c>keys()</c>, <c>values()</c> and <c>entries()</c>, and
/// over a set, each walked live. A floating-point key is inserted as <c>k + 0</c>, which is how a
/// <c>Map</c> stores <c>-0</c> as <c>+0</c>.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The map or set type of an expression, or <see langword="null"/>.</summary>
  private IrType? KeyedOf(Node node) => types.Of(node) is { Kind: JsKind.Map or JsKind.Set } keyed ? keyed.Ir : null;

  /// <summary>A key as an insertion stores it: a floating-point number plus 0, under which <c>-0</c> is <c>+0</c>.</summary>
  private static IrExpr Inserted(IrExpr key)
    => key.Type.IsFloat ? new IrBinary(IrBinaryOp.Add, key, new IrConst(key.Type, IrOperators.Bits(key.Type, 0.0))) : key;

  /// <summary>
  /// What a map or a set gives as a value - a new empty one, its size, what <c>get</c>, <c>has</c> and
  /// <c>delete</c> yield - or <see langword="null"/> where the expression is about neither.
  /// </summary>
  private IrExpr? KeyedExpression(Node node) {
    switch (node) {
      case NewExpression { Callee: Identifier { Name: var name } constructor } creation when name is "Map" or "Set" && !this._model.Bindings.ContainsKey(constructor):
        if (creation.Arguments.Count > 0)
          throw this.Unsupported(creation, $"new {name} with elements other than as the value of a variable");

        var created = types.Of(creation).Ir!;
        return ListOp(created.Kind == IrTypeKind.Map ? IrCollectionOp.MapNew : IrCollectionOp.SetNew, created);
      case MemberExpression { Computed: false, Property: Identifier { Name: "size" } } size when this.KeyedOf(size.Object) is { } counted:
        return ListOp(counted.Kind == IrTypeKind.Map ? IrCollectionOp.MapCount : IrCollectionOp.SetCount, counted, this.Expression(size.Object));
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } member } call when this.KeyedOf(member.Object) is { } keyed:
        var map = keyed.Kind == IrTypeKind.Map;
        var key = keyed.Key ?? keyed.Element!;
        switch (method) {
          case "get" when map && call.Arguments.Count == 1:
            if (IrCollections.Absent(keyed.Element!) is null)
              throw this.Unsupported(call, $"the value of get of a Map of {JsType.Spell(keyed.Element!)}, which may be undefined");

            return ListOp(IrCollectionOp.MapTryGet, keyed, this.Expression(member.Object), this.Value(call.Arguments[0], key));
          case "has" when call.Arguments.Count == 1:
            return ListOp(map ? IrCollectionOp.MapContainsKey : IrCollectionOp.SetContains, keyed, this.Expression(member.Object), this.Value(call.Arguments[0], key));
          case "delete" when call.Arguments.Count == 1:
            return ListOp(map ? IrCollectionOp.MapRemove : IrCollectionOp.SetRemove, keyed, this.Expression(member.Object), this.Value(call.Arguments[0], key));
          default:
            throw this.Unsupported(call, method is "set" or "add" or "clear" ? $"the value of {types.Text(call)}" : $"{types.Text(call.Callee)} of a {(map ? "Map" : "Set")}");
        }
      default:
        return null;
    }
  }

  /// <summary>
  /// A statement that changes a map or a set - <c>set</c>, <c>add</c>, <c>delete</c>, <c>clear</c> -
  /// or <see langword="false"/> for any other statement.
  /// </summary>
  private bool KeyedEffect(Expression expression, List<IrStmt> into) {
    if (expression is not CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } member } call || this.KeyedOf(member.Object) is not { } keyed)
      return false;

    var map = keyed.Kind == IrTypeKind.Map;
    var key = keyed.Key ?? keyed.Element!;
    var arguments = call.Arguments;
    switch (method) {
      case "set" when map && arguments.Count == 2:
        var target = this.Expression(member.Object);
        var inserted = Inserted(this.Value(arguments[0], key));
        into.Add(new IrExprStmt(ListOp(IrCollectionOp.MapPut, keyed, target, inserted, this.Value(arguments[1], keyed.Element!))));
        return true;
      case "add" when !map && arguments.Count == 1:
        into.Add(new IrExprStmt(ListOp(IrCollectionOp.SetAdd, keyed, this.Expression(member.Object), Inserted(this.Value(arguments[0], key)))));
        return true;
      case "clear" when arguments.Count == 0:
        into.Add(new IrExprStmt(ListOp(map ? IrCollectionOp.MapClear : IrCollectionOp.SetClear, keyed, this.Expression(member.Object))));
        return true;
      default:
        into.Add(new IrExprStmt(this.Expression(call)));
        return true;
    }
  }

  /// <summary>
  /// A variable declared with a new map or set that has entries or elements: the collection is made
  /// empty and each entry or element inserted, in order, as the constructor inserts them.
  /// <see langword="false"/> for any other declaration.
  /// </summary>
  private bool KeyedDeclaration(Node? initializer, IrLocal local, List<IrStmt> into) {
    if (initializer is not NewExpression { Callee: Identifier { Name: var name } constructor, Arguments: [var source] } creation
        || name is not ("Map" or "Set") || this._model.Bindings.ContainsKey(constructor) || !local.Type.IsCollection)
      return false;

    var type = local.Type;
    var map = type.Kind == IrTypeKind.Map;
    into.Add(new IrDeclare(local, ListOp(map ? IrCollectionOp.MapNew : IrCollectionOp.SetNew, type)));
    var target = new IrLocalRef(local);
    if (source is ArrayExpression literal) {
      foreach (var item in this.Elements(literal)) {
        if (!map) {
          into.Add(new IrExprStmt(ListOp(IrCollectionOp.SetAdd, type, target, Inserted(this.Value(item, type.Element!)))));
          continue;
        }

        if (item is not ArrayExpression { Elements: [Node key, Node value] } || key is SpreadElement || value is SpreadElement)
          throw this.Unsupported(item, "an entry of a Map other than [key, value]");

        into.Add(new IrExprStmt(ListOp(IrCollectionOp.MapPut, type, target, Inserted(this.Value(key, type.Key!)), this.Value(value, type.Element!))));
      }

      return true;
    }

    // new Set(array): the elements of an array or a list, each inserted.
    var elements = this.Expression(source);
    if (map || !(elements.Type.IsArray || elements.Type.IsList) || elements.Type.Element != type.Element)
      throw this.Unsupported(creation, $"new {name} of {types.Of(source).Describe()}");

    if (elements.Type.IsArray)
      elements = ListOp(IrCollectionOp.ListFromArray, IrType.List(type.Element!), elements);

    var each = new IrLocal($"$t{this._locals.Count}", type.Element!);
    this._locals.Add(each);
    into.Add(new IrForEachCollection(elements, EnumerationMode.Live, each, null, new IrBlock([
      new IrExprStmt(ListOp(IrCollectionOp.SetAdd, type, target, Inserted(new IrLocalRef(each))))])));
    return true;
  }

  /// <summary>
  /// <c>for…of</c> over a map destructured into its key and value, over its <c>entries()</c>,
  /// <c>keys()</c> or <c>values()</c>, or over a set or its <c>keys()</c> or <c>values()</c>: a live
  /// loop. <see langword="null"/> for a loop over anything else.
  /// </summary>
  private IrStmt? KeyedForOf(ForOfStatement loop) {
    var source = loop.Right;
    string? part = null;
    if (source is CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } member, Arguments.Count: 0 } && method is "keys" or "values" or "entries"
        && this.KeyedOf(member.Object) is not null) {
      part = method;
      source = member.Object;
    }

    if (this.KeyedOf(source) is not { } type)
      return null;

    var statements = new List<IrStmt>();
    var collection = this.Once(this.Expression(source), statements);
    var map = type.Kind == IrTypeKind.Map;
    IrLocal? element = null;
    IrLocal? value = null;
    var prologue = new List<IrStmt>();
    switch (loop.Left) {
      case VariableDeclaration { Declarations: [{ Id: ArrayPattern { Elements: [Identifier key, Identifier entry] } }] } when map && part is null or "entries":
        element = this.Iteration(key, type.Key!, prologue);
        value = this.Iteration(entry, type.Element!, prologue);
        break;
      case VariableDeclaration { Declarations: [{ Id: Identifier single }] } when !map || part is "keys" or "values":
        var held = map && part == "values" ? type.Element! : type.Key ?? type.Element!;
        if (map && part == "values")
          value = this.Iteration(single, held, prologue);
        else
          element = this.Iteration(single, held, prologue);

        break;
      default:
        throw this.Unsupported(loop.Left, map ? "a for...of over a Map that does not destructure its entry into [key, value]" : "a for...of that does not declare one variable");
    }

    var before = this._assigned;
    var body = this.Body(loop.Body).Statements;
    this._assigned = before;
    statements.Add(new IrForEachCollection(collection, EnumerationMode.Live, element, value, new IrBlock([.. prologue, .. body])));
    return new IrBlock(statements);
  }

  /// <summary>
  /// The local a <c>for…of</c> assigns the key, the value or the element to: the variable itself where
  /// it has that type, otherwise a local of the loop that the variable takes at the start of every pass.
  /// </summary>
  private IrLocal Iteration(Identifier id, IrType held, List<IrStmt> prologue) {
    var variable = (Variable)this._model.Bindings[id];
    var local = new IrLocal(id.Name, this.Storable(variable.Type, id));
    variable.Local = local;
    this._locals.Add(local);
    if (local.Type == held)
      return local;

    var item = new IrLocal($"$t{this._locals.Count}", held);
    this._locals.Add(item);
    prologue.Add(new IrDeclare(local, this.To(new Typed(Read(new IrLocalRef(item)), JsType.Of(held)), local.Type, id)));
    return item;
  }

}
