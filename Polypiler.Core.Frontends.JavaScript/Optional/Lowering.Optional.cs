using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// <c>null</c> and <c>undefined</c>: a scalar that an absent value may reach is an optional value of
/// the IR, a reference is <c>null</c>. JavaScript's own meaning stays: an operator that computes with
/// a value that may be absent takes <c>null</c> as 0 and <c>undefined</c> as a NaN (ECMA-262 §7.1.4,
/// <c>ToNumber</c>), written out as a coalesce - never C#'s lifting. <c>== null</c> tests both absent
/// values; <c>=== null</c> and <c>=== undefined</c> tell them apart, which a place both reach cannot
/// be imported for (<c>PLY1105</c>): the IR has one absent value.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The temporaries an operand evaluated once is assigned to, declared at the start of the function being lowered.</summary>
  private readonly List<IrLocal> _bound = [];

  /// <summary>The absent value as the source spells it - <c>null</c>, <c>undefined</c>, <c>void 0</c> - before it has the type of its place.</summary>
  private static IrNull AbsentValue => new(IrType.Void);

  /// <summary>An operand that is read more than once, evaluated once: as it is when that is the same, otherwise through a temporary.</summary>
  private (IrExpr First, IrExpr Again) Bound(IrExpr value) {
    if (value is IrLocalRef or IrConst or IrNone or IrNull)
      return (value, value);

    var temporary = new IrLocal($"$t{this._locals.Count}", value.Type);
    this._locals.Add(temporary);
    this._bound.Add(temporary);
    return (new IrBind(temporary, value), new IrLocalRef(temporary));
  }

  /// <summary>The declarations of the temporaries of the function lowered last, which go in front of its body.</summary>
  private List<IrStmt> TakeBound() {
    var declarations = this._bound.Select(local => (IrStmt)new IrDeclare(local, null)).ToList();
    this._bound.Clear();
    return declarations;
  }

  /// <summary>
  /// A value an operator computes with, which ToNumber makes of an absent one: <c>null</c> is 0,
  /// <c>undefined</c> a NaN. A value both may be is refused: the IR does not know which one it is.
  /// </summary>
  private Typed Present(Typed value, Node node) {
    if (!value.Expr.Type.IsOptional)
      return value;

    if (value.Expr is IrLocalRef { Local: var local } && this._present.Contains(local))
      return new Typed(new IrUnwrap(value.Expr) { IsTested = true }, value.Type.Present);

    var underlying = value.Expr.Type.Underlying!;
    if (value.Type.MayBeNull && value.Type.MayBeUndefined)
      throw this.Unsupported(node, $"{types.Text(node)} as a number, which may be null or undefined");

    if (value.Type.MayBeNull)
      return new Typed(new IrCoalesce(value.Expr, new IrConst(underlying, 0)), value.Type.Present);

    // An integer an optional key of a typedef states (#1409) is a double here, a NaN where it is undefined.
    if (underlying.IsNumeric && underlying != IrType.F64) {
      var (first, again) = this.Bound(value.Expr);
      var number = new IrConvert(IrType.F64, new IrUnwrap(again) { IsTested = true });
      return new Typed(new IrConditional(new IrIsNone(first), new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, double.NaN)), number), JsType.F64);
    }

    // undefined is a NaN, and a truth value false; a number undefined reaches is a double.
    IrExpr fallback = underlying == IrType.Bool ? new IrConst(IrType.Bool, 0) : new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, double.NaN));
    return new Typed(new IrCoalesce(value.Expr, fallback), value.Type.Present);
  }

  /// <summary>
  /// A value in an optional type or out of one: a value made optional, an absent one none, an
  /// optional value of another type converted where it holds one, and an optional value where a
  /// value is expected as ToNumber makes it one; <see langword="null"/> where neither type is optional.
  /// </summary>
  private IrExpr? ToOptional(Typed value, IrType type, Node node) {
    var from = value.Expr.Type;
    if (value.Expr is IrNull { Type.Kind: IrTypeKind.Void })
      return type.IsOptional ? new IrNone(type) : type.IsNullable ? new IrNull(type) : throw this.Unsupported(node, types.Text(node));

    if (from == type)
      return null;

    if (!type.IsOptional)
      return from.IsOptional ? this.To(this.Present(value, node), type, node) : null;

    if (!from.IsOptional)
      return new IrSome(this.To(value with { Type = value.Type.Present }, type.Underlying!, node));

    var (first, again) = this.Bound(value.Expr);
    var held = new Typed(new IrUnwrap(again) { IsTested = true }, value.Type.Present);
    return new IrConditional(new IrIsNone(first), new IrNone(type), new IrSome(this.To(held, type.Underlying!, node)));
  }

  /// <summary>
  /// <c>x == null</c>, <c>x === undefined</c> and their negations. The loose test takes both absent
  /// values; a strict one tells them apart, which is refused for a place both reach. A strict test for
  /// the absent value that never reaches the place is false.
  /// </summary>
  private IrExpr? AbsentTest(Operator op, Node left, Node right) {
    if (op is not (Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality))
      return null;

    var names = types.Names;
    var (literal, other) = names.IsAbsentLiteral(right) ? (right, left) : names.IsAbsentLiteral(left) ? (left, right) : (null, null);
    if (literal is null || other is null)
      return null;

    var strict = op is Operator.StrictEquality or Operator.StrictInequality;
    var type = types.Of(other);
    // Two objects, or one and null, compare by identity, as the classes do.
    if (strict && type.Kind == JsKind.Object)
      return null;

    var equal = op is Operator.Equality or Operator.StrictEquality;
    if (strict && type.MayBeNull && type.MayBeUndefined)
      throw this.Refuse(other, JavaScriptFrontend.MixedKindsDescriptor, $"'{types.Text(other)}' holds both null and undefined, and the program tells them apart");

    var value = this.Expression(other);
    var isNull = literal is NullLiteral;
    IrExpr test;
    if (value is IrNull { Type.Kind: IrTypeKind.Void })
      // null == undefined; null === undefined is not.
      test = new IrConst(IrType.Bool, !strict || (other is NullLiteral) == isNull ? 1 : 0);
    else if (value.Type.IsOptional)
      test = strict && !(isNull ? type.MayBeNull : type.MayBeUndefined) ? this.Never(value, other) : new IrIsNone(value);
    else if (value.Type.IsString)
      // A string tells which absent value reaches it as a number does (#1370).
      test = strict && !(isNull ? type.MayBeNull : type.MayBeUndefined) ? this.Never(value, other) : new IrIsNull(value);
    else if (value.Type.IsNullable)
      test = new IrIsNull(value);
    else
      test = this.Never(value, other);

    return equal ? test : new IrUnary(IrUnaryOp.LogicalNot, test);
  }

  /// <summary>
  /// <c>a === b</c>, <c>a !== b</c>, <c>==</c> and <c>!=</c> of an optional truth value or integer and a value
  /// that is never absent (#1409): an absent value equals no value of these kinds - <c>undefined !== false</c>,
  /// <c>null != 0</c> - so it is unequal, and a present one is compared as it is. The optional operand is
  /// evaluated once and first; the other one only where it is present, so it may have no effect. <see langword="null"/> for any
  /// other comparison.
  /// </summary>
  private IrExpr? AbsentOperandEquality(Operator op, Node left, Node right, IrType operand) {
    var (a, b) = (types.Of(left), types.Of(right));
    if (op is not (Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality) || a.IsAbsent == b.IsAbsent)
      return null;

    var (absent, present) = a.IsAbsent ? (left, right) : (right, left);
    if (types.Of(absent).Ir is not { IsOptional: true })
      return null;

    var value = this.Evaluate(absent);
    var (first, again) = this.Bound(value.Expr);
    var other = this.To(this.Evaluate(present), operand, present);
    // The absent operand is tested first, and the other one is evaluated only where it is present.
    if (!IrShapeFree(other))
      throw this.Unsupported(present, $"a comparison with {types.Text(absent)}, which may be absent, of an operand with an effect");

    var held = this.To(new Typed(new IrUnwrap(again) { IsTested = true }, value.Type.Present), operand, absent);
    var compared = absent == left ? new IrBinary(Operation(op), held, other) : new IrBinary(Operation(op), other, held);
    var unequal = new IrConst(IrType.Bool, op is Operator.Inequality or Operator.StrictInequality ? 1 : 0);
    return new IrConditional(new IrIsNone(first), unequal, compared);
  }

  /// <summary>The test of a value the absent value it is compared with never reaches: false, where nothing else happens in it.</summary>
  private IrConst Never(IrExpr value, Node node)
    => IrShapeFree(value) ? new IrConst(IrType.Bool, 0) : throw this.Unsupported(node, $"a test for null or undefined of {types.Text(node)}, which is never either");

  private static bool IrShapeFree(IrExpr value) => IrTree.Nodes(value).All(node => node is not (IrCall or IrStep or IrBind or IrNew or IrMethodCall));

  /// <summary>
  /// <c>a ?? b</c>: <c>a</c> where it is present, otherwise <c>b</c>, which is evaluated only then. Of a
  /// value never absent it is that value; of a reference the reference unless it is <c>null</c>.
  /// </summary>
  private IrExpr Coalesce(LogicalExpression logical) {
    if (types.Of(logical).Ir is not { IsStorable: true } type) {
      this.Expression(logical.Left);
      this.Expression(logical.Right);
      throw this.Unsupported(logical, $"?? on {types.Of(logical.Left).Describe()} and {types.Of(logical.Right).Describe()}");
    }

    var left = this.Evaluate(logical.Left);
    if (left.Expr is IrNull { Type.Kind: IrTypeKind.Void })
      return this.Value(logical.Right, type);

    if (left.Expr.Type.IsOptional) {
      var optional = type.IsOptional ? type : IrType.Optional(type);
      var value = this.ToOptional(left, optional, logical.Left) ?? left.Expr;
      return new IrCoalesce(value, this.Value(logical.Right, type));
    }

    if (!left.Expr.Type.IsNullable)
      return this.To(left, type, logical.Left);

    var (first, again) = this.Bound(this.To(left, type, logical.Left));
    return new IrConditional(new IrIsNull(first), this.Value(logical.Right, type), again);
  }

  /// <summary>
  /// <c>a?.length</c> and <c>o?.f</c>: <c>undefined</c> where <c>a</c> is absent, otherwise the member,
  /// with <c>a</c> evaluated once. A computed key or a call in the chain is refused.
  /// </summary>
  private IrExpr Chain(ChainExpression chain) {
    if (chain.Expression is not MemberExpression { Optional: true, Computed: false } member || member.Object is ChainExpression)
      throw this.Unsupported(chain, chain.Expression is MemberExpression { Computed: true } ? "obj?.[key], an optional chain with a computed key" : "an optional chain other than a?.b");

    if (types.Of(chain).Ir is not { IsStorable: true } type)
      throw this.Unsupported(chain, $"?. on {types.Of(member.Object).Describe()}");

    var receiver = this.Evaluate(member.Object);
    if (!receiver.Expr.Type.IsNullable)
      throw this.Unsupported(chain, $"?. on {receiver.Type.Describe()}");

    var (first, again) = this.Bound(receiver.Expr);
    this._substitutes[member.Object] = again;
    try {
      var read = new Typed(this.Expression(member), types.Of(member));
      var absent = type.IsOptional ? (IrExpr)new IrNone(type) : new IrNull(type);
      return new IrConditional(new IrIsNull(first), absent, this.ToOptional(read, type, member) ?? this.To(read, type, member));
    } finally {
      this._substitutes.Remove(member.Object);
    }
  }

  /// <summary><c>x ??= v;</c> of a variable: <c>v</c> is assigned only where <c>x</c> is absent; nothing where it never is.</summary>
  private bool CoalescingAssignment(AssignmentExpression assignment, List<IrStmt> into) {
    if (assignment.Operator != Operator.NullishCoalescingAssignment)
      return false;

    if (assignment.Left is not Identifier target || this._model.Bindings.GetValueOrDefault(target) is not Variable { IsConstant: false } variable)
      throw this.Unsupported(assignment, "??= of anything but a variable");

    var place = Read(variable);
    if (!(place.Type.IsOptional || place.Type.IsNullable))
      return true;

    var value = this.Value(assignment.Right, place.Type);
    into.Add(new IrIf(place.Type.IsOptional ? new IrIsNone(place) : new IrIsNull(place), new IrBlock([Write(variable, value)]), null));
    return true;
  }

  /// <summary>The arguments a call leaves out: the default of a parameter that has one, <c>undefined</c> for each that may be absent, or the reason the call is refused.</summary>
  private IEnumerable<IrExpr> Missing(Node call, FunctionModel target, int given) {
    foreach (var parameter in target.Parameters.Skip(given)) {
      var type = parameter.Parameter?.Type ?? this.Storable(parameter.Entry, call);
      yield return parameter.Default is { } value ? this.Value(value, type)
        : type.IsOptional ? new IrNone(type) : type.IsNullable ? new IrNull(type)
        : throw this.Unsupported(call, $"a call of {target.Candidate.Name} with {given} arguments for {target.Parameters.Count} parameters");
    }
  }

  /// <summary>The variables a test has proven present where they are read: in a branch, behind a <c>&amp;&amp;</c> or a <c>||</c>.</summary>
  private readonly HashSet<IrLocal> _present = [];

  /// <summary>
  /// The variable a test of <c>x == null</c>, <c>x != null</c> or a strict one proves present where it
  /// is true and where it is false; <see langword="null"/> for a side it proves nothing for.
  /// </summary>
  private (IrLocal? WhenTrue, IrLocal? WhenFalse) Narrowed(Node test) {
    if (test is not BinaryExpression { Operator: Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality } compared)
      return (null, null);

    var names = types.Names;
    var other = names.IsAbsentLiteral(compared.Right) ? compared.Left : names.IsAbsentLiteral(compared.Left) ? compared.Right : null;
    if (other is not Identifier identifier || this._model.Bindings.GetValueOrDefault(identifier) is not Variable { Local: { Type.IsOptional: true } local, IsGlobal: false })
      return (null, null);

    // A strict test for the one absent value proves nothing where both may reach; the refusal of that comes elsewhere.
    var equal = compared.Operator is Operator.Equality or Operator.StrictEquality;
    return equal ? (null, local) : (local, null);
  }

  /// <summary>Lowers a part with a variable proven present in it, unless the part assigns the variable.</summary>
  private T Within<T>(IrLocal? present, Node part, System.Func<T> lower) {
    if (present is null || this._present.Contains(present)
        || Contains(part, node => node is AssignmentExpression { Left: Identifier { Name: var name } } && name == present.Name || node is UpdateExpression { Argument: Identifier { Name: var stepped } } && stepped == present.Name))
      return lower();

    this._present.Add(present);
    try {
      return lower();
    } finally {
      this._present.Remove(present);
    }
  }

  /// <summary>How the source spells the absent result of a function: undefined where nothing but undefined reaches it.</summary>
  private static IrAbsent AbsentResult(FunctionModel model) => model.Result.MayBeUndefined && !model.Result.MayBeNull ? IrAbsent.Undefined : IrAbsent.Null;

}
