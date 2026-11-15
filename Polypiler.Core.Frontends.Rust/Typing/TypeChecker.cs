using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>Ends the check of a function: the source does not compile, and the reason has been reported.</summary>
internal sealed class CheckAbort : Exception;

/// <summary>
/// The type checker of the subset: infers the type of every expression the way <c>rustc</c> does for
/// it - inference variables for unsuffixed literals and untyped locals, unified by every use, an
/// integer variable nothing decides becoming <c>i32</c> and a float one <c>f64</c> - resolves every
/// name, method, <c>break</c> and <c>continue</c>, and rejects what <c>rustc</c> rejects with the
/// compiler's error code (<c>E0308</c> mismatched types, <c>E0689</c> a method on an ambiguous
/// number, <c>E0600</c> negation of an unsigned value, the <c>overflowing_literals</c> lint, ...).
/// A construct outside the subset is reported with the diagnostic its <see cref="Refusal"/> names.
/// </summary>
internal sealed class TypeChecker {

  private readonly Typed _typed;
  private readonly Action<string, string, Span> _report;
  private readonly List<Dictionary<string, Binding>> _scopes = [];
  private readonly List<Context> _contexts = [];
  private readonly List<RType> _variables = [];
  private readonly List<(RType Type, Binding Binding, Span At)> _undecided = [];
  private readonly List<Action> _deferred = [];

  /// <summary>The variables that stand for a refused expression: nothing is reported about them again.</summary>
  private readonly HashSet<RType> _errors = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<string, Binding> _constants = [];
  private RType _result = RType.Unit;

  /// <summary>A loop or a labelled block that a <c>break</c> may leave.</summary>
  private sealed class Context(Expr node, string? label, bool isLoop, bool takesValue) {

    public Expr Node { get; } = node;

    public string? Label { get; } = label;

    /// <summary>Whether it is a loop, which <c>continue</c> and an unlabelled <c>break</c> may name.</summary>
    public bool IsLoop { get; } = isLoop;

    /// <summary>Whether a <c>break</c> may carry a value: a <c>loop</c> and a labelled block.</summary>
    public bool TakesValue { get; } = takesValue;

    /// <summary>The type of the values breaks carry, or <see langword="null"/> when nothing breaks out.</summary>
    public RType? BreakType { get; set; }

  }

  public TypeChecker(Typed typed, Action<string, string, Span> report) {
    this._typed = typed;
    this._report = report;
  }

  // ---------------------------------------------------------------- reporting

  private CheckAbort Invalid(string message, Span at) {
    this._report(RustFrontend.InvalidSource, message, at);
    return new CheckAbort();
  }

  private void Refuse(Refusal refusal) => this._report(refusal.Code, refusal.Construct, refusal.At);

  private RType Refused(Refusal refusal) {
    this.Refuse(refusal);
    return this.ErrorType();
  }

  /// <summary>The type of a refused expression: anything, and never reported again.</summary>
  private RType ErrorType() {
    var error = this.Fresh(VariableKind.Any);
    this._errors.Add(error);
    return error;
  }

  private RType Fresh(VariableKind kind) {
    var variable = RType.Variable(kind);
    this._variables.Add(variable);
    return variable;
  }

  // ---------------------------------------------------------------- types

  /// <summary>The type a type of the source names, or a refusal when the subset has no such type.</summary>
  public RType? Resolve(TypeRef? type, bool allowUnit) {
    if (type is null)
      return RType.Unit;

    if (type.Name is "()" && allowUnit)
      return RType.Unit;

    if (type.Name is "!" && allowUnit)
      return RType.Never;

    if (RType.Named(type.Name) is { } primitive)
      return primitive;

    this._report(RustFrontend.UnsupportedType, type.Name, type.At);
    return null;
  }

  private void Unify(RType expected, RType found, Span at) {
    var a = expected.Resolved;
    var b = found.Resolved;
    if (ReferenceEquals(a, b) || a.Kind == RKind.Never || b.Kind == RKind.Never)
      return;

    if (a.Kind == RKind.Variable && Bind(a, b))
      return;

    if (b.Kind == RKind.Variable && Bind(b, a))
      return;

    throw this.Invalid($"E0308: mismatched types: expected `{a}`, found `{b}`", at);
  }

  /// <summary>Binds a variable to a type it may stand for; <see langword="false"/> when it may not.</summary>
  private static bool Bind(RType variable, RType type) {
    if (type.Kind == RKind.Variable) {
      if (variable.VariableKind == VariableKind.Any || variable.VariableKind == type.VariableKind) {
        variable.Bound = type;
        return true;
      }

      if (type.VariableKind == VariableKind.Any) {
        type.Bound = variable;
        return true;
      }

      return false;
    }

    var fits = variable.VariableKind switch {
      VariableKind.Integer => type.Kind == RKind.Integer,
      VariableKind.Float => type.Kind == RKind.Float,
      _ => type.Kind is not (RKind.Tuple or RKind.Option)
    };
    if (fits)
      variable.Bound = type;

    return fits;
  }

  /// <summary>The type of two branches that meet: the one that yields a value where the other never does.</summary>
  private RType Join(RType first, RType second, Span at) {
    if (first.Resolved.Kind == RKind.Never)
      return second;

    if (second.Resolved.Kind == RKind.Never)
      return first;

    this.Unify(first, second, at);
    return first;
  }

  // ---------------------------------------------------------------- functions

  /// <summary>Checks a function; <see langword="false"/> when it does not compile or uses a construct outside the subset (already reported).</summary>
  public bool Function(FunctionSignature signature) {
    this._scopes.Clear();
    this._contexts.Clear();
    this._variables.Clear();
    this._undecided.Clear();
    this._deferred.Clear();
    this._errors.Clear();
    this._result = signature.Result;
    var scope = new Dictionary<string, Binding>();
    this._scopes.Add(scope);
    var item = signature.Item;
    for (var index = 0; index < item.Parameters.Count; ++index) {
      var parameter = item.Parameters[index];
      var binding = new Binding(parameter.Name, signature.Parameters[index], parameter.Mutable) { Initialized = true };
      scope[parameter.Name] = binding;
      this._typed.Bindings[parameter] = binding;
    }

    try {
      var body = this.Block(item.Body);
      this.Unify(signature.Result, body, item.Body.Tail?.At ?? item.Body.At);
      this.Finish();
      foreach (var check in this._deferred)
        check();
    } catch (CheckAbort) {
      return false;
    }

    return true;
  }

  /// <summary>Decides every variable nothing decided: an integer is <c>i32</c>, a float <c>f64</c>, anything else needs an annotation.</summary>
  private void Finish() {
    foreach (var variable in this._variables) {
      var resolved = variable.Resolved;
      if (resolved.Kind != RKind.Variable)
        continue;

      if (resolved.VariableKind == VariableKind.Integer)
        resolved.Bound = RType.I32;
      else if (resolved.VariableKind == VariableKind.Float)
        resolved.Bound = RType.F64;
    }

    foreach (var (type, binding, at) in this._undecided)
      if (type.Resolved.Kind == RKind.Variable && !this._errors.Contains(type.Resolved))
        throw this.Unresolved(binding.Name, at);
  }

  private CheckAbort Unresolved(string name, Span at) {
    this._report(RustFrontend.UnresolvedType, name, at);
    return new CheckAbort();
  }

  // ---------------------------------------------------------------- scopes

  private Binding Declare(string name, RType type, bool mutable, object node, Span at, bool initialized) {
    var binding = new Binding(name, type, mutable) { Initialized = initialized };
    this._scopes[^1][name] = binding;
    this._typed.Bindings[node] = binding;
    if (type.Resolved.Kind == RKind.Variable && type.Resolved.VariableKind == VariableKind.Any)
      this._undecided.Add((type, binding, at));

    return binding;
  }

  private Binding? Lookup(string name) {
    for (var index = this._scopes.Count - 1; index >= 0; --index)
      if (this._scopes[index].TryGetValue(name, out var binding))
        return binding;

    return this._constants.GetValueOrDefault(name);
  }

  /// <summary>Checks a constant of the file against its declared type; once it passes, every function may name it.</summary>
  public bool Constant(ConstItem constant) {
    this._scopes.Clear();
    this._contexts.Clear();
    this._variables.Clear();
    this._undecided.Clear();
    this._deferred.Clear();
    this._errors.Clear();
    this._scopes.Add([]);
    try {
      var type = this.Resolve(constant.Type, false) ?? throw new CheckAbort();
      this.Unify(type, this.Value(constant.Value), constant.Value.At);
      this.Finish();
      foreach (var check in this._deferred)
        check();

      this._constants[constant.Name] = new Binding(constant.Name, type, false, constant) { Initialized = true };
    } catch (CheckAbort) {
      return false;
    }

    return true;
  }

  // ---------------------------------------------------------------- blocks and statements

  private RType Block(Block block) {
    this._scopes.Add([]);
    var diverges = false;
    foreach (var statement in block.Statements)
      diverges |= this.Statement(statement);

    var type = block.Tail is null ? diverges ? RType.Never : RType.Unit : this.Value(block.Tail);
    this._scopes.RemoveAt(this._scopes.Count - 1);
    return diverges && type.Resolved.Kind == RKind.Unit && block.Tail is null ? RType.Never : type;
  }

  /// <summary>Checks a statement; <see langword="true"/> when control never goes on after it.</summary>
  private bool Statement(Statement statement) {
    switch (statement) {
      case LetStatement let:
        return this.Let(let);
      case ExpressionStatement expression:
        return this.Value(expression.Expression).Resolved.Kind == RKind.Never;
      case ConstStatement constant: {
        var type = this.Resolve(constant.Item.Type, false) ?? throw new CheckAbort();
        this.Unify(type, this.Value(constant.Item.Value), constant.Item.Value.At);
        var binding = new Binding(constant.Item.Name, type, false, constant.Item) { Initialized = true };
        this._scopes[^1][constant.Item.Name] = binding;
        return false;
      }
      default:
        this.Refuse(((RefusedStatement)statement).Refusal);
        return false;
    }
  }

  private bool Let(LetStatement let) {
    var annotated = let.Type is null ? null : this.Resolve(let.Type, false) ?? this.ErrorType();
    if (let.Pattern is TuplePattern tuple) {
      if (let.Initializer is not MethodCallExpr { Name: var name } call || !name.StartsWith("overflowing_", StringComparison.Ordinal) || tuple.Elements.Count != 2
          || tuple.Elements.Any(element => element is not (IdentifierPattern or WildcardPattern))) {
        this.Refuse(new Refusal(RustFrontend.UnsupportedPattern, "tuple pattern", tuple.At));
        return false;
      }

      var pair = this.Method(call, true);
      this._typed.Types[call] = pair;
      if (annotated is not null)
        this.Unify(annotated, pair, let.Type!.At);

      RType[] parts = [pair.Resolved.Element!, RType.Bool];
      for (var index = 0; index < 2; ++index)
        if (tuple.Elements[index] is IdentifierPattern part)
          this.Declare(part.Name, parts[index], part.Mutable, part, part.At, true);

      return false;
    }

    var type = annotated;
    var diverges = false;
    if (let.Initializer is not null) {
      var initializer = this.Value(let.Initializer);
      diverges = initializer.Resolved.Kind == RKind.Never;
      if (type is null)
        type = initializer.Resolved.Kind == RKind.Never ? this.Fresh(VariableKind.Any) : initializer;
      else
        this.Unify(type, initializer, let.Initializer.At);
    }

    type ??= this.Fresh(VariableKind.Any);
    switch (let.Pattern) {
      case IdentifierPattern identifier:
        if (type.Resolved.Kind == RKind.Unit)
          this.Refuse(new Refusal(RustFrontend.Unsupported, "unit value '()'", identifier.At));

        this.Declare(identifier.Name, type, identifier.Mutable, identifier, identifier.At, let.Initializer is not null);
        break;
      case WildcardPattern:
        break;
      case RefusedPattern refused:
        this.Refuse(refused.Refusal);
        break;
      default:
        this.Refuse(new Refusal(RustFrontend.UnsupportedPattern, "refutable pattern in let", let.Pattern.At));
        break;
    }

    return diverges;
  }

  // ---------------------------------------------------------------- expressions

  /// <summary>Checks an expression whose value is used as a value: an optional value or a pair of a method may not be.</summary>
  private RType Value(Expr expression) {
    // An optional value or a pair never gets here: a method returning one refuses being used as a value itself.
    return this.Expression(expression);
  }

  private RType Expression(Expr expression) {
    var type = this.ExpressionUnrecorded(expression);
    this._typed.Types[expression] = type;
    return type;
  }

  private RType ExpressionUnrecorded(Expr expression) => expression switch {
    IntegerLiteral literal => this.Integer(literal, false),
    FloatLiteral literal => literal.Suffix is null ? this.Fresh(VariableKind.Float) : RType.Named(literal.Suffix)!,
    BoolLiteral => RType.Bool,
    StringLiteral literal => this.Refused(new Refusal(RustFrontend.UnsupportedType, "&str", literal.At)),
    PathExpr path => this.Path(path),
    UnaryExpr unary => this.Unary(unary),
    BinaryExpr binary => this.Binary(binary),
    AssignExpr assign => this.Assign(assign),
    CastExpr cast => this.Cast(cast),
    MethodCallExpr call => this.Method(call, false),
    CallExpr call => this.Call(call),
    FieldExpr field => this.Field(field),
    TupleExpr tuple => this.Refused(new Refusal(RustFrontend.Unsupported, "tuple expression", tuple.At)),
    RangeExpr range => this.Refused(new Refusal(RustFrontend.Unsupported, "range expression outside a for loop", range.At)),
    BlockExpr block => this.BlockExpression(block),
    IfExpr conditional => this.If(conditional),
    MatchExpr match => this.Match(match),
    WhileExpr loop => this.While(loop),
    LoopExpr loop => this.Loop(loop),
    ForExpr loop => this.For(loop),
    BreakExpr jump => this.Break(jump),
    ContinueExpr jump => this.Continue(jump),
    ReturnExpr jump => this.Return(jump),
    _ => this.Refused(((RefusedExpr)expression).Refusal)
  };

  private RType Integer(IntegerLiteral literal, bool negated) {
    if (literal.Suffix is { } suffix && RType.Named(suffix) is not { Kind: RKind.Integer or RKind.Float }) {
      return this.Refused(RType.Unsupported.Contains(suffix)
        ? new Refusal(RustFrontend.UnsupportedType, suffix, literal.At)
        : new Refusal(RustFrontend.InvalidSource, $"invalid suffix `{suffix}` for number literal", literal.At));
    }

    var type = literal.Suffix is null ? this.Fresh(VariableKind.Integer) : RType.Named(literal.Suffix)!;
    // The overflowing_literals lint, denied by default: the literal must be a value of its type, its sign included.
    this._deferred.Add(() => {
      var resolved = type.Resolved;
      var value = negated ? -literal.Value : literal.Value;
      // A negated unsigned literal is E0600, which the negation reports.
      if (resolved.Kind == RKind.Integer && !(negated && !resolved.IsSigned) && (value < resolved.Minimum || value > resolved.Maximum))
        throw this.Invalid($"literal out of range for `{resolved}` (overflowing_literals)", literal.At);
    });
    return type;
  }

  private RType Path(PathExpr path) {
    if (path.Segments.Count == 1) {
      var binding = this.Lookup(path.Segments[0]);
      if (binding is null) {
        if (this._typed.Functions.ContainsKey(path.Segments[0]))
          return this.Refused(new Refusal(RustFrontend.Unsupported, $"function '{path.Segments[0]}' as a value", path.At));

        throw this.Invalid($"E0425: cannot find value `{path.Segments[0]}` in this scope", path.At);
      }

      this._typed.Bindings[path] = binding;
      return binding.Type;
    }

    if (path.Segments.Count == 2 && RType.Named(path.Segments[0]) is { } owner) {
      if (owner.Kind == RKind.Integer && path.Segments[1] is "MIN" or "MAX")
        return owner;

      if (owner.Kind == RKind.Integer && path.Segments[1] == "BITS")
        return RType.U32;

      if (owner.Kind == RKind.Float && path.Segments[1] is "MIN" or "MAX" or "NAN" or "INFINITY" or "NEG_INFINITY" or "EPSILON" or "MIN_POSITIVE")
        return owner;
    }

    if (path.Segments.Count == 2 && RType.Unsupported.Contains(path.Segments[0]))
      return this.Refused(new Refusal(RustFrontend.UnsupportedType, path.Segments[0], path.At));

    return this.Refused(new Refusal(RustFrontend.Unsupported, path.Text, path.At));
  }

  private RType Unary(UnaryExpr unary) {
    var type = unary is { Op: "-", Operand: IntegerLiteral literal } ? this.Literal(literal, true) : this.Value(unary.Operand);
    this._deferred.Add(() => {
      var resolved = type.Resolved;
      var valid = unary.Op == "-" ? resolved.Kind == RKind.Float || (resolved.Kind == RKind.Integer && resolved.IsSigned) : resolved.Kind is RKind.Integer or RKind.Bool;
      if (!valid)
        throw this.Invalid($"E0600: cannot apply unary operator `{unary.Op}` to type `{resolved}`", unary.At);

      if (unary.Op == "-" && resolved.Kind == RKind.Integer && unary.Operand is not IntegerLiteral && -Constants.Evaluate(unary.Operand, this._typed) > resolved.Maximum)
        throw this.Invalid("this arithmetic operation will overflow (arithmetic_overflow)", unary.At);
    });
    return type;
  }

  /// <summary>An integer literal under a unary minus: recorded as the operand, its range checked with the sign.</summary>
  private RType Literal(IntegerLiteral literal, bool negated) {
    var type = this.Integer(literal, negated);
    this._typed.Types[literal] = type;
    return type;
  }

  private static readonly HashSet<string> _arithmetic = ["+", "-", "*", "/", "%"];
  private static readonly HashSet<string> _bitwise = ["&", "|", "^"];
  private static readonly HashSet<string> _comparisons = ["==", "!=", "<", ">", "<=", ">="];

  private RType Binary(BinaryExpr binary) {
    var left = this.Value(binary.Left);
    var right = this.Value(binary.Right);
    if (binary.Op is "&&" or "||") {
      this.Unify(RType.Bool, left, binary.Left.At);
      this.Unify(RType.Bool, right, binary.Right.At);
      return RType.Bool;
    }

    this._deferred.Add(() => this.KnownOverflow(binary, left));
    if (binary.Op is "<<" or ">>") {
      this.Operands(binary.Op, left, binary.At, type => type.Kind == RKind.Integer);
      this.Operands(binary.Op, right, binary.At, type => type.Kind == RKind.Integer);
      return left;
    }

    this.Unify(left, right, binary.At);
    if (_comparisons.Contains(binary.Op)) {
      this.Operands(binary.Op, left, binary.At, type => type.Kind is RKind.Integer or RKind.Float or RKind.Bool);
      return RType.Bool;
    }

    this.Operands(binary.Op, left, binary.At, _bitwise.Contains(binary.Op)
      ? type => type.Kind is RKind.Integer or RKind.Bool
      : type => type.Kind is RKind.Integer or RKind.Float);
    return left;
  }

  /// <summary>
  /// The deny-by-default lints <c>arithmetic_overflow</c> and <c>unconditional_panic</c>: an operator
  /// on constant operands that overflows, a shift by a constant amount outside the width, and a
  /// division by a constant zero do not compile.
  /// </summary>
  private void KnownOverflow(BinaryExpr binary, RType type) {
    var resolved = type.Resolved;
    if (resolved.Kind != RKind.Integer)
      return;

    var right = Constants.Evaluate(binary.Right, this._typed);
    if (right is null)
      return;

    if (binary.Op is "<<" or ">>") {
      if (right < 0 || right >= resolved.Bits)
        throw this.Invalid($"this arithmetic operation will overflow: attempt to shift {(binary.Op == "<<" ? "left" : "right")} by `{right}` (arithmetic_overflow)", binary.At);

      return;
    }

    if (binary.Op is "/" or "%" && right.Value.IsZero)
      throw this.Invalid($"this operation will panic at runtime: attempt to {(binary.Op == "/" ? "divide" : "calculate the remainder of")} by zero (unconditional_panic)", binary.At);

    var left = Constants.Evaluate(binary.Left, this._typed);
    if (left is null)
      return;

    BigInteger? result = binary.Op switch {
      "+" => left + right,
      "-" => left - right,
      "*" => left * right,
      "/" or "%" => BigInteger.Divide(left.Value, right.Value),
      _ => null
    };
    if (result is { } value && (value < resolved.Minimum || value > resolved.Maximum))
      throw this.Invalid("this arithmetic operation will overflow (arithmetic_overflow)", binary.At);
  }

  /// <summary>Requires, once the types are decided, that an operand of an operator is of a type the operator is defined on.</summary>
  private void Operands(string op, RType type, Span at, Func<RType, bool> valid)
    => this._deferred.Add(() => {
      if (!valid(type.Resolved) && type.Resolved.Kind != RKind.Never)
        throw this.Invalid($"E0369: no implementation for `{type} {op} {type}`", at);
    });

  private RType Assign(AssignExpr assign) {
    var value = this.Value(assign.Value);
    if (assign.Target is not PathExpr { Segments.Count: 1 } path || this.Lookup(path.Segments[0]) is not { Constant: null } binding) {
      if (assign.Target is PathExpr { Segments.Count: 1 } named && this.Lookup(named.Segments[0]) is null)
        throw this.Invalid($"E0425: cannot find value `{named.Segments[0]}` in this scope", named.At);

      return this.Refused(new Refusal(RustFrontend.Unsupported, "assignment to anything but a local variable", assign.Target.At));
    }

    this._typed.Bindings[path] = binding;
    this._typed.Types[path] = binding.Type;
    if (!binding.Mutable && (binding.Initialized || assign.Op != "="))
      throw this.Invalid($"E0384: cannot assign twice to immutable variable `{binding.Name}`", assign.At);

    binding.Initialized = true;
    if (assign.Op is "<<" or ">>") {
      this.Operands(assign.Op, binding.Type, assign.At, type => type.Kind == RKind.Integer);
      this.Operands(assign.Op, value, assign.At, type => type.Kind == RKind.Integer);
      return RType.Unit;
    }

    this.Unify(binding.Type, value, assign.Value.At);
    if (assign.Op != "=")
      this.Operands(assign.Op + "=", binding.Type, assign.At, _bitwise.Contains(assign.Op)
        ? type => type.Kind is RKind.Integer or RKind.Bool
        : type => type.Kind is RKind.Integer or RKind.Float);

    return RType.Unit;
  }

  private RType Cast(CastExpr cast) {
    var operand = this.Value(cast.Operand);
    var target = this.Resolve(cast.Type, false);
    if (target is null)
      return this.ErrorType();

    // An unsuffixed literal takes the type it is cast to, as rustc's expectation makes it: `300 as u8` is an overflowing literal.
    var literal = cast.Operand is IntegerLiteral or FloatLiteral or UnaryExpr { Op: "-", Operand: IntegerLiteral or FloatLiteral };
    if (literal && operand.Resolved is { Kind: RKind.Variable } variable
        && ((variable.VariableKind == VariableKind.Integer && target.Kind == RKind.Integer) || (variable.VariableKind == VariableKind.Float && target.Kind == RKind.Float)))
      variable.Bound = target;

    this._deferred.Add(() => {
      var from = operand.Resolved;
      var valid = target.Kind == RKind.Bool ? from.Kind == RKind.Bool : from.Kind is RKind.Integer or RKind.Float or RKind.Bool;
      if (!valid)
        throw this.Invalid($"E0054: cannot cast `{from}` as `{target}`", cast.At);
    });
    return target;
  }

  /// <summary>Checks a method call; an optional value or a pair is allowed only where it is consumed on the spot.</summary>
  private RType Method(MethodCallExpr call, bool allowCompound) {
    RType receiver;
    if (Methods.Consumers.Contains(call.Name) && call.Receiver is MethodCallExpr { Name: var inner } checkedCall && inner.StartsWith("checked_", StringComparison.Ordinal)) {
      receiver = this.Method(checkedCall, true);
      this._typed.Types[checkedCall] = receiver;
    } else {
      receiver = this.Value(call.Receiver);
    }

    var resolved = receiver.Resolved;
    if (resolved.Kind == RKind.Variable) {
      if (this._errors.Contains(resolved))
        return this.ErrorType();

      if (resolved.VariableKind == VariableKind.Any)
        throw this.Unresolved(call.Receiver is PathExpr path ? path.Text : call.Name, call.Receiver.At);

      throw this.Invalid($"E0689: can't call method `{call.Name}` on ambiguous numeric type `{resolved}`", call.At);
    }

    RType result;
    if (resolved.Kind == RKind.Option) {
      result = this.Consumer(call, resolved.Element!);
    } else if (Methods.Of(resolved, call.Name) is { } method) {
      if (call.Arguments.Count != method.Arguments.Count)
        throw this.Invalid($"E0061: this method takes {method.Arguments.Count} argument{(method.Arguments.Count == 1 ? "" : "s")} but {call.Arguments.Count} were supplied", call.At);

      for (var index = 0; index < method.Arguments.Count; ++index)
        this.Unify(Methods.ArgumentOf(method.Arguments[index], resolved), this.Value(call.Arguments[index]), call.Arguments[index].At);

      result = Methods.ResultOf(method, resolved);
    } else {
      foreach (var argument in call.Arguments)
        this.Expression(argument);

      return this.Refused(new Refusal(RustFrontend.Unsupported, $"{resolved}::{call.Name}", call.At));
    }

    if (!allowCompound && result.Kind is RKind.Option or RKind.Tuple) {
      this.Refuse(new Refusal(RustFrontend.Unsupported, $"the result of '{call.Name}' used other than by {(result.Kind == RKind.Option ? "unwrap, expect, unwrap_or, is_some or is_none" : ".0, .1 or a tuple let")}", call.At));
      return this.ErrorType();
    }

    return result;
  }

  private RType Consumer(MethodCallExpr call, RType value) {
    var (arguments, result) = call.Name switch {
      "unwrap" => (0, value),
      "expect" => (1, value),
      "unwrap_or" => (1, value),
      _ => (0, RType.Bool)
    };
    if (call.Arguments.Count != arguments)
      throw this.Invalid($"E0061: this method takes {arguments} argument{(arguments == 1 ? "" : "s")} but {call.Arguments.Count} were supplied", call.At);

    if (call.Name == "expect") {
      if (call.Arguments[0] is not StringLiteral)
        return this.Refused(new Refusal(RustFrontend.Unsupported, "expect with a message that is not a string literal", call.Arguments[0].At));
    } else if (call.Name == "unwrap_or") {
      this.Unify(value, this.Value(call.Arguments[0]), call.Arguments[0].At);
    }

    return result;
  }

  private RType Call(CallExpr call) {
    if (call.Callee is PathExpr { Segments.Count: 1 } name && this.Lookup(name.Segments[0]) is null && this._typed.Functions.TryGetValue(name.Segments[0], out var function)) {
      if (call.Arguments.Count != function.Parameters.Count)
        throw this.Invalid($"E0061: this function takes {function.Parameters.Count} argument{(function.Parameters.Count == 1 ? "" : "s")} but {call.Arguments.Count} were supplied", call.At);

      for (var index = 0; index < call.Arguments.Count; ++index)
        this.Unify(function.Parameters[index], this.Value(call.Arguments[index]), call.Arguments[index].At);

      return function.Result;
    }

    if (call.Callee is PathExpr { Segments: [var owner, "from_bits"] } && RType.Named(owner) is { Kind: RKind.Float } floating && call.Arguments.Count == 1) {
      this.Unify(floating.Bits == 32 ? RType.U32 : RType.U64, this.Value(call.Arguments[0]), call.Arguments[0].At);
      return floating;
    }

    foreach (var argument in call.Arguments)
      this.Expression(argument);

    if (call.Callee is PathExpr { Segments.Count: 1 } unknown && this.Lookup(unknown.Segments[0]) is null)
      throw this.Invalid($"E0425: cannot find function `{unknown.Segments[0]}` in this scope", unknown.At);

    return this.Refused(call.Callee is RefusedExpr refused ? refused.Refusal : new Refusal(RustFrontend.Unsupported, call.Callee is PathExpr path ? path.Text : "call of a value", call.Callee.At));
  }

  private RType Field(FieldExpr field) {
    if (field.Name is "0" or "1" && field.Target is MethodCallExpr { Name: var name } call && name.StartsWith("overflowing_", StringComparison.Ordinal)) {
      var pair = this.Method(call, true);
      this._typed.Types[call] = pair;
      return field.Name == "0" ? pair.Resolved.Element! : RType.Bool;
    }

    this.Expression(field.Target);
    return this.Refused(new Refusal(RustFrontend.Unsupported, $"field access '.{field.Name}'", field.At));
  }

  // ---------------------------------------------------------------- control flow

  private RType BlockExpression(BlockExpr block) {
    if (block.Label is null)
      return this.Block(block.Block);

    var context = new Context(block, block.Label, false, true);
    this._contexts.Add(context);
    var type = this.Block(block.Block);
    this._contexts.RemoveAt(this._contexts.Count - 1);
    return context.BreakType is null ? type : this.Join(type, context.BreakType, block.At);
  }

  private RType If(IfExpr conditional) {
    this.Unify(RType.Bool, this.Value(conditional.Condition), conditional.Condition.At);
    var then = this.Block(conditional.Then);
    if (conditional.Else is null) {
      if (then.Resolved.Kind != RKind.Never)
        this.Unify(RType.Unit, then, conditional.Then.Tail?.At ?? conditional.At);

      return RType.Unit;
    }

    var otherwise = this.Expression(conditional.Else);
    return this.Join(then, otherwise, conditional.Else.At);
  }

  private RType While(WhileExpr loop) {
    this.Unify(RType.Bool, this.Value(loop.Condition), loop.Condition.At);
    this._contexts.Add(new Context(loop, loop.Label, true, false));
    var body = this.Block(loop.Body);
    this._contexts.RemoveAt(this._contexts.Count - 1);
    this.Unify(RType.Unit, body, loop.Body.Tail?.At ?? loop.At);
    return RType.Unit;
  }

  private RType Loop(LoopExpr loop) {
    var context = new Context(loop, loop.Label, true, true);
    this._contexts.Add(context);
    var body = this.Block(loop.Body);
    this._contexts.RemoveAt(this._contexts.Count - 1);
    this.Unify(RType.Unit, body, loop.Body.Tail?.At ?? loop.At);
    return context.BreakType ?? RType.Never;
  }

  private RType For(ForExpr loop) {
    // The adapters, outermost last in the source; the base is a range with both bounds.
    var adapters = new List<MethodCallExpr>();
    var iterable = loop.Iterable;
    while (iterable is MethodCallExpr { Name: "rev" or "step_by" } adapter) {
      adapters.Insert(0, adapter);
      iterable = adapter.Receiver;
    }

    if (iterable is not RangeExpr { Start: not null, End: not null } range
        || adapters.Count(adapter => adapter.Name == "rev") > 1 || adapters.Count(adapter => adapter.Name == "step_by") > 1
        || adapters.Any(adapter => adapter.Arguments.Count != (adapter.Name == "rev" ? 0 : 1))) {
      this.Refuse(new Refusal(RustFrontend.UnsupportedIterable, Describe(loop.Iterable), loop.Iterable.At));
      return RType.Unit;
    }

    var element = this.Value(range.Start);
    this.Unify(element, this.Value(range.End), range.End.At);
    this._deferred.Add(() => {
      if (element.Resolved.Kind != RKind.Integer)
        throw this.Invalid($"E0277: the trait bound `{element}: Step` is not satisfied", range.At);
    });
    var step = adapters.FirstOrDefault(adapter => adapter.Name == "step_by");
    if (step is not null)
      this.Unify(RType.Usize, this.Value(step.Arguments[0]), step.Arguments[0].At);

    var reversed = adapters.Any(adapter => adapter.Name == "rev");
    var stepFirst = step is not null && reversed && adapters[0] == step;
    if (stepFirst) {
      // Reversing a StepBy needs an ExactSizeIterator: Range of the types up to 32 bits and pointer width, RangeInclusive of 8 and 16 bits.
      this._deferred.Add(() => {
        var type = element.Resolved;
        var exact = range.Inclusive ? type.Bits <= 16 && type.Name is not ("isize" or "usize") : type.Bits <= 32 || type.Name is "isize" or "usize";
        if (!exact)
          throw this.Invalid($"E0277: the trait bound `{(range.Inclusive ? "RangeInclusive" : "Range")}<{type}>: ExactSizeIterator` is not satisfied", loop.Iterable.At);
      });
    }

    this._typed.Ranges[loop] = new RangeLoop(range.Start, range.End, range.Inclusive, reversed, step?.Arguments[0], stepFirst);
    this._scopes.Add([]);
    switch (loop.Pattern) {
      case IdentifierPattern identifier:
        this.Declare(identifier.Name, element, identifier.Mutable, identifier, identifier.At, true);
        break;
      case WildcardPattern:
        break;
      case RefusedPattern refused:
        this.Refuse(refused.Refusal);
        break;
      default:
        this.Refuse(new Refusal(RustFrontend.UnsupportedPattern, "pattern of a for over a range", loop.Pattern.At));
        break;
    }

    this._contexts.Add(new Context(loop, loop.Label, true, false));
    var body = this.Block(loop.Body);
    this._contexts.RemoveAt(this._contexts.Count - 1);
    this._scopes.RemoveAt(this._scopes.Count - 1);
    this.Unify(RType.Unit, body, loop.Body.Tail?.At ?? loop.At);
    return RType.Unit;
  }

  /// <summary>How a diagnostic names what a <c>for</c> iterates over.</summary>
  private static string Describe(Expr iterable) => iterable switch {
    MethodCallExpr call => $"{Describe(call.Receiver)}.{call.Name}()",
    PathExpr path => path.Text,
    RangeExpr range => range.Start is null ? ".." : range.End is null ? "a.." : "range",
    _ => "expression"
  };

  private Context Target(string? label, bool forContinue, Span at) {
    for (var index = this._contexts.Count - 1; index >= 0; --index) {
      var context = this._contexts[index];
      if (label is null ? context.IsLoop : context.Label == label) {
        if (forContinue && !context.IsLoop)
          throw this.Invalid($"E0696: `continue` pointing to a labeled block", at);

        return context;
      }
    }

    if (label is not null)
      throw this.Invalid($"E0426: use of undeclared label `'{label}`", at);

    throw this.Invalid(forContinue ? "E0268: `continue` outside of a loop" : "E0268: `break` outside of a loop or labeled block", at);
  }

  private RType Break(BreakExpr jump) {
    var context = this.Target(jump.Label, false, jump.At);
    this._typed.Targets[jump] = context.Node;
    if (jump.Value is not null && !context.TakesValue)
      throw this.Invalid("E0571: `break` with value from a `while` or `for` loop", jump.At);

    var value = jump.Value is null ? RType.Unit : this.Value(jump.Value);
    context.BreakType = context.BreakType is null ? value : this.Join(context.BreakType, value, jump.At);
    return RType.Never;
  }

  private RType Continue(ContinueExpr jump) {
    this._typed.Targets[jump] = this.Target(jump.Label, true, jump.At).Node;
    return RType.Never;
  }

  private RType Return(ReturnExpr jump) {
    var value = jump.Value is null ? RType.Unit : this.Value(jump.Value);
    this.Unify(this._result, value, jump.Value?.At ?? jump.At);
    return RType.Never;
  }

  // ---------------------------------------------------------------- match

  private RType Match(MatchExpr match) {
    IReadOnlyList<RType> columns = match.Scrutinee is TupleExpr tuple
      ? [.. tuple.Elements.Select(this.Value)]
      : [this.Value(match.Scrutinee)];
    if (match.Scrutinee is TupleExpr whole)
      this._typed.Types[whole] = RType.Unit;

    RType? result = null;
    foreach (var arm in match.Arms) {
      this._scopes.Add([]);
      this.ArmPattern(arm.Pattern, columns);
      if (arm.Guard is not null)
        this.Unify(RType.Bool, this.Value(arm.Guard), arm.Guard.At);

      var body = this.Value(arm.Body);
      this._scopes.RemoveAt(this._scopes.Count - 1);
      result = result is null ? body : this.Join(result, body, arm.Body.At);
    }

    this._deferred.Add(() => {
      foreach (var column in columns)
        if (column.Resolved.Kind is not (RKind.Integer or RKind.Bool))
          throw this.Refusal(new Refusal(RustFrontend.UnsupportedPattern, $"match on {column}", match.Scrutinee.At));

      if (!Exhaustiveness.Covers(match, columns, this._typed))
        throw this.Invalid("E0004: non-exhaustive patterns", match.Scrutinee.At);
    });
    return result ?? RType.Never;
  }

  private CheckAbort Refusal(Refusal refusal) {
    this.Refuse(refusal);
    return new CheckAbort();
  }

  private void ArmPattern(Pattern pattern, IReadOnlyList<RType> columns) {
    if (columns.Count == 1) {
      this.Pattern(pattern, columns[0], false);
      return;
    }

    var alternatives = pattern is OrPattern or ? or.Alternatives : [pattern];
    foreach (var alternative in alternatives) {
      if (alternative is RefusedPattern refused) {
        this.Refuse(refused.Refusal);
        continue;
      }

      if (alternative is WildcardPattern)
        continue;

      if (alternative is not TuplePattern tuple || tuple.Elements.Count != columns.Count)
        throw this.Invalid($"E0308: mismatched types: expected a tuple of {columns.Count} elements", alternative.At);

      for (var index = 0; index < columns.Count; ++index)
        this.Pattern(tuple.Elements[index], columns[index], alternatives.Count > 1);
    }
  }

  private void Pattern(Pattern pattern, RType type, bool inAlternative) {
    switch (pattern) {
      case WildcardPattern:
        return;
      case IdentifierPattern identifier:
        if (!identifier.Mutable && this.Lookup(identifier.Name) is { Constant: not null } constant) {
          this._typed.Bindings[identifier] = constant;
          this.Unify(type, constant.Type, identifier.At);
          return;
        }

        if (inAlternative) {
          this.Refuse(new Refusal(RustFrontend.UnsupportedPattern, $"binding '{identifier.Name}' in an or-pattern", identifier.At));
          return;
        }

        this.Declare(identifier.Name, type, identifier.Mutable, identifier, identifier.At, true);
        return;
      case LiteralPattern literal:
        this.Unify(type, this.PatternValue(literal.Value), literal.At);
        return;
      case RangePattern range:
        if (range.Low is not null)
          this.Unify(type, this.PatternValue(range.Low), range.Low.At);

        if (range.High is not null)
          this.Unify(type, this.PatternValue(range.High), range.High.At);

        return;
      case OrPattern alternatives:
        foreach (var alternative in alternatives.Alternatives)
          this.Pattern(alternative, type, true);

        return;
      case TuplePattern tuple:
        throw this.Invalid("E0308: mismatched types: a tuple pattern for a value that is no tuple", tuple.At);
      default:
        this.Refuse(((RefusedPattern)pattern).Refusal);
        return;
    }
  }

  /// <summary>A literal, a negated literal or a path to a constant in a pattern.</summary>
  private RType PatternValue(Expr value) {
    if (value is PathExpr { Segments.Count: 1 } path && this.Lookup(path.Segments[0]) is { Constant: null }) {
      this.Refuse(new Refusal(RustFrontend.UnsupportedPattern, $"variable '{path.Text}' in a range pattern", path.At));
      return this.ErrorType();
    }

    return this.Expression(value);
  }

}
