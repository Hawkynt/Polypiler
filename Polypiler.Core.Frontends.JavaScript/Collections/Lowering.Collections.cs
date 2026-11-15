using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The arrays that change their length, which are lists of the IR (#1328): a literal, <c>new Array()</c>
/// and <c>[...a]</c> make one; <c>a[i]</c>, <c>a.length</c>, <c>push</c> with one or more values,
/// <c>pop</c>, <c>splice(i, 1)</c>, <c>splice(i, 0, x)</c>, <c>a.length = 0</c>, <c>a[a.length] = x</c>,
/// <c>indexOf</c> and <c>includes</c> act on it, and <c>for…of</c> walks it live, as JavaScript does.
/// An element read or written outside the list traps, under the caveat of the reads of arrays.
/// </summary>
internal sealed partial class Lowering {

  private static IrCollection ListOp(IrCollectionOp op, IrType list, params IrExpr[] arguments)
    => new(op, IrCollections.Signature(op, list).Result!, arguments);

  /// <summary>The list type of an expression whose array changes its length, or <see langword="null"/>.</summary>
  private IrType? ListOf(Node node) => types.Of(node) is { Kind: JsKind.Array, IsGrowable: true } list ? list.Ir : null;

  /// <summary>
  /// What a list gives as a value - an element, its length, the value of <c>pop</c>, <c>indexOf</c> or
  /// <c>includes</c>, a new list - or <see langword="null"/> where the expression is about no list.
  /// </summary>
  private IrExpr? ListExpression(Node node) {
    switch (node) {
      case MemberExpression { Computed: true } element when this.ListOf(element.Object) is { } list:
        this.AccessesElements = true;
        return Read(ListOp(IrCollectionOp.ListGet, list, this.Expression(element.Object), this.ListIndex(element.Property)));
      case MemberExpression { Computed: false, Property: Identifier { Name: "length" } } length when this.ListOf(length.Object) is { } list:
        return ListOp(IrCollectionOp.ListCount, list, this.Expression(length.Object));
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } member } call when this.ListOf(member.Object) is { } list:
        return this.ListCall(call, method, member, list);
      case ArrayExpression literal when this.ListOf(literal) is { } list:
        return this.ListLiteral(literal, list);
      case NewExpression { Callee: Identifier { Name: "Array" } constructor, Arguments.Count: 0 } when !this._model.Bindings.ContainsKey(constructor) && this.ListOf(node) is { } list:
        return ListOp(IrCollectionOp.ListNew, list);
      default:
        return null;
    }
  }

  /// <summary>An index of a list as an <c>int32</c>; one outside the list traps.</summary>
  private IrExpr ListIndex(Node node) => Convert(this.Index(node), IrType.I32);

  /// <summary>The value of a method of a list: <c>pop</c>, <c>indexOf</c>, <c>includes</c>; a push or a splice yields what nothing uses here.</summary>
  private IrExpr ListCall(CallExpression call, string method, MemberExpression member, IrType list) {
    var element = list.Element!;
    switch (method) {
      case "pop" when call.Arguments.Count == 0:
        if (IrCollections.Absent(element) is null)
          throw this.Unsupported(call, $"the value of pop of a list of {JsType.Spell(element)}");

        return ListOp(IrCollectionOp.ListRemoveLast, list, this.Expression(member.Object));
      case "indexOf" or "includes" when call.Arguments.Count == 1:
        if (element.IsStruct)
          throw this.Unsupported(call, $"{method} of a list of records, which compares them by identity");

        var op = method == "indexOf" ? IrCollectionOp.ListIndexOfStrict : IrCollectionOp.ListIncludesSameValueZero;
        return ListOp(op, list, this.Expression(member.Object), this.Value(call.Arguments[0], element));
      default:
        throw this.Unsupported(call, method is "push" or "pop" or "splice" ? $"the value of {types.Text(call)}" : $"{types.Text(call.Callee)} of a list");
    }
  }

  /// <summary>A literal as a new list: its elements, or the elements of the one array it spreads.</summary>
  private IrExpr ListLiteral(ArrayExpression literal, IrType list) {
    var element = list.Element!;
    if (literal.Elements is [SpreadElement spread]) {
      var source = this.Expression(spread.Argument);
      if (source.Type.IsList)
        source = ListOp(IrCollectionOp.ListCopyToArray, source.Type, source);

      return source.Type == IrType.Array(element) ? ListOp(IrCollectionOp.ListFromArray, list, source) : throw this.Unsupported(literal, $"a spread of {types.Of(spread.Argument).Describe()}");
    }

    if (literal.Elements.Count == 0)
      return ListOp(IrCollectionOp.ListNew, list);

    if (element.IsCollection || element.IsObject)
      throw this.Unsupported(literal, $"a literal of a list of {types.Of(literal).Element!.Describe()}");

    return ListOp(IrCollectionOp.ListFromArray, list, this.ArrayLiteral(literal, element));
  }

  /// <summary>
  /// A statement that changes a list: <c>push</c> of each value, <c>pop</c>, <c>splice(i, 1)</c>,
  /// <c>splice(i, 0, x)</c>, <c>a.length = 0</c>, <c>a[a.length] = x</c> and a write of an element.
  /// <see langword="false"/> for any other statement.
  /// </summary>
  private bool ListEffect(Expression expression, List<IrStmt> into) {
    switch (expression) {
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } member } call when this.ListOf(member.Object) is { } list:
        this.ListChange(call, method, member, list, into);
        return true;
      case AssignmentExpression { Left: MemberExpression { Computed: false, Property: Identifier { Name: "length" } } length } assignment when this.ListOf(length.Object) is { } list:
        if (assignment.Operator != Operator.Assignment || assignment.Right is not NumericLiteral { Value: 0 })
          throw this.Unsupported(assignment, $"{types.Text(assignment)}, a length other than 0");

        into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListClear, list, this.Expression(length.Object))));
        return true;
      case AssignmentExpression { Left: MemberExpression { Computed: true } element } assignment when this.ListOf(element.Object) is { } list:
        this.ListStore(assignment, element, list, into);
        return true;
      case UpdateExpression { Argument: MemberExpression { Computed: true } stepped } step when this.ListOf(stepped.Object) is { } list:
        // a[i]++ reads and writes one element, in double arithmetic as every ++ of JavaScript.
        var target = this.Once(this.Expression(stepped.Object), into);
        var index = this.Once(this.ListIndex(stepped.Property), into);
        var current = this.Float(new Typed(Read(ListOp(IrCollectionOp.ListGet, list, target, index)), types.Of(stepped)), stepped);
        var one = new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1.0));
        var next = new IrBinary(step.Operator == Operator.Increment ? IrBinaryOp.Add : IrBinaryOp.Subtract, current, one);
        this.AccessesElements = true;
        into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListSet, list, target, index, this.To(new Typed(next, JsType.F64), list.Element!, step))));
        return true;
      default:
        return false;
    }
  }

  private void ListChange(CallExpression call, string method, MemberExpression member, IrType list, List<IrStmt> into) {
    var arguments = call.Arguments;
    if (arguments.Any(argument => argument is SpreadElement))
      throw this.Unsupported(call, "a spread argument");

    switch (method) {
      case "push" when arguments.Count > 0:
        var pushed = arguments.Select(argument => this.Value(argument, list.Element!)).ToList();
        var target = this.Once(this.Expression(member.Object), into);
        foreach (var value in pushed)
          into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListAdd, list, target, value)));

        break;
      case "pop" when arguments.Count == 0 && IrCollections.Absent(list.Element!) is not null:
        into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListRemoveLast, list, this.Expression(member.Object))));
        break;
      case "pop" when arguments.Count == 0:
        // pop of an empty list does nothing; of a list of strings, which no optional value holds, it is the removal of the last one.
        var popped = this.Once(this.Expression(member.Object), into);
        var count = ListOp(IrCollectionOp.ListCount, list, popped);
        var last = new IrBinary(IrBinaryOp.Subtract, count, I32(1));
        into.Add(new IrIf(new IrBinary(IrBinaryOp.Greater, count, I32(0)), new IrBlock([new IrExprStmt(ListOp(IrCollectionOp.ListRemoveAt, list, popped, last))]), null));
        break;
      case "splice" when arguments is [_, NumericLiteral { Value: 1 }]:
        into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListRemoveAt, list, this.Expression(member.Object), this.ListIndex(arguments[0]))));
        break;
      case "splice" when arguments is [_, NumericLiteral { Value: 0 }, _]:
        var inserted = this.Expression(member.Object);
        var at = this.ListIndex(arguments[0]);
        into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListInsert, list, inserted, at, this.Value(arguments[2], list.Element!))));
        break;
      default:
        throw this.Unsupported(call, $"{types.Text(call)} on a list");
    }
  }

  /// <summary>
  /// A write of an element of a list: at <c>a.length</c> an append, anywhere else a write that traps
  /// outside the list - JavaScript would leave holes - read once for a compound assignment.
  /// </summary>
  private void ListStore(AssignmentExpression assignment, MemberExpression element, IrType list, List<IrStmt> into) {
    var target = this.Expression(element.Object);
    var type = list.Element!;
    var compound = Binder.Compound(assignment.Operator);
    if (compound is null && assignment.Operator != Operator.Assignment)
      throw this.Unsupported(assignment, types.Text(assignment));

    if (element.Property is MemberExpression { Computed: false, Object: Identifier measured, Property: Identifier { Name: "length" } } && element.Object is Identifier held
        && measured.Name == held.Name && compound is null) {
      into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListAdd, list, target, this.Value(assignment.Right, type))));
      return;
    }

    var index = this.ListIndex(element.Property);
    this.AccessesElements = true;
    if (compound is { } combining) {
      target = this.Once(target, into);
      index = this.Once(index, into);
      this._substitutes[element] = Read(ListOp(IrCollectionOp.ListGet, list, target, index));
      var combined = this.Binary(combining, element, assignment.Right, Coercion.None, assignment);
      this._substitutes.Remove(element);
      into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListSet, list, target, index, this.To(combined, type, assignment))));
      return;
    }

    into.Add(new IrExprStmt(ListOp(IrCollectionOp.ListSet, list, target, index, this.Value(assignment.Right, type))));
  }

  /// <summary>
  /// <c>for (const x of a)</c> over a list - a live loop, which sees what the body adds - or over an
  /// array, a loop by index over its length. <see langword="null"/> for a loop over anything else.
  /// </summary>
  private IrStmt? ListForOf(ForOfStatement loop) {
    if (types.Of(loop.Right) is not { Kind: JsKind.Array, IsView: false } iterated)
      return null;

    if (loop.Left is not VariableDeclaration { Declarations: [{ Id: Identifier { Name: var name } id }] } || this._model.Bindings.GetValueOrDefault(id) is not Variable variable)
      throw this.Unsupported(loop.Left, "a for...of that does not declare one variable");

    var statements = new List<IrStmt>();
    var collection = this.Once(this.Expression(loop.Right), statements);
    var element = collection.Type.Element!;
    var local = new IrLocal(name, this.Storable(variable.Type, id));
    variable.Local = local;
    this._locals.Add(local);
    var item = local.Type == element ? local : new IrLocal($"$t{this._locals.Count}", element);
    if (item != local)
      this._locals.Add(item);

    var before = this._assigned;
    var body = this.Body(loop.Body).Statements;
    this._assigned = before;
    if (item != local)
      body = [new IrDeclare(local, Read(new IrLocalRef(item))), .. body];

    if (iterated.IsGrowable) {
      statements.Add(new IrForEachCollection(collection, EnumerationMode.Live, item, null, new IrBlock(body)));
      return new IrBlock(statements);
    }

    // An array keeps its length: the loop walks it by index; the position moves on before the body, so that a continue goes on with the next element.
    var at = new IrLocal($"$t{this._locals.Count}", IrType.I32);
    this._locals.Add(at);
    statements.Add(new IrDeclare(at, I32(0)));
    IrStmt[] pass = [
      new IrDeclare(item, new IrArrayLoad(collection, new IrLocalRef(at))),
      new IrAssign(at, new IrBinary(IrBinaryOp.Add, new IrLocalRef(at), I32(1)))
    ];
    this.AccessesElements = true;
    statements.Add(new IrWhile(new IrBinary(IrBinaryOp.Less, new IrLocalRef(at), new IrArrayLength(collection)), new IrBlock([.. pass, .. body])));
    return new IrBlock(statements);
  }

}
