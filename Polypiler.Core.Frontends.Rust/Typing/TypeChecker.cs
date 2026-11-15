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
/// name, path, method, field, <c>break</c> and <c>continue</c>, and rejects what <c>rustc</c> rejects
/// with the compiler's error code (<c>E0308</c> mismatched types, <c>E0689</c> a method on an
/// ambiguous number, <c>E0600</c> negation of an unsigned value, the <c>overflowing_literals</c> lint,
/// ...). A construct outside the subset is reported with the diagnostic its <see cref="Refusal"/> names.
/// </summary>
internal sealed partial class TypeChecker {

  private readonly Typed _typed;
  private readonly Action<string, string, Span> _report;
  private readonly CrateScope _crate;
  private readonly List<Dictionary<string, Binding>> _scopes = [];
  private readonly List<Dictionary<string, FunctionSignature>> _localFunctions = [];
  private readonly List<Context> _contexts = [];
  private readonly List<RType> _variables = [];
  private readonly List<(RType Type, Binding Binding, Span At)> _undecided = [];

  /// <summary>The expressions checked, whose types must be decided once the function is: an expression nothing types is <c>E0282</c>.</summary>
  private readonly List<(Expr Expression, RType Type)> _expressions = [];
  private readonly List<Action> _deferred = [];
  private readonly List<ClosureInfo> _closures = [];

  /// <summary>The variables that stand for a refused expression: nothing is reported about them again.</summary>
  private readonly HashSet<RType> _errors = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<ConstItem, Binding> _constants = new(ReferenceEqualityComparer.Instance);
  private RType _result = RType.Unit;
  private ModuleScope _module;
  private RType? _self;

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

    /// <summary>The closure the loop is in: a <c>break</c> never leaves a closure.</summary>
    public int ClosureDepth { get; init; }

  }

  public TypeChecker(Typed typed, CrateScope crate, Action<string, string, Span> report) {
    this._typed = typed;
    this._report = report;
    this._crate = crate;
    this._module = crate.Root;
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

  private RType Refused(string code, string construct, Span at) => this.Refused(new Refusal(code, construct, at));

  /// <summary>The type of a refused expression: anything, and never reported again.</summary>
  private RType ErrorType() {
    var error = this.Fresh(VariableKind.Any);
    this._errors.Add(error);
    return error;
  }

  private bool IsError(RType type) => this._errors.Contains(type.Resolved);

  private RType Fresh(VariableKind kind) {
    var variable = RType.Variable(kind);
    this._variables.Add(variable);
    return variable;
  }

  private CheckAbort Unresolved(string name, Span at) {
    this._report(RustFrontend.UnresolvedType, name, at);
    return new CheckAbort();
  }

  // ---------------------------------------------------------------- unification

  private void Unify(RType expected, RType found, Span at) {
    if (!this.TryUnify(expected, found))
      throw this.Invalid($"E0308: mismatched types: expected `{expected}`, found `{found}`", at);
  }

  /// <summary>Makes two types one, binding variables; <see langword="false"/> when they differ.</summary>
  private bool TryUnify(RType expected, RType found) {
    var a = expected.Resolved;
    var b = found.Resolved;
    if (ReferenceEquals(a, b) || a.Kind == RKind.Never || b.Kind == RKind.Never || this.IsError(a) || this.IsError(b))
      return true;

    if (a.Kind == RKind.Variable && Bind(a, b))
      return true;

    if (b.Kind == RKind.Variable && Bind(b, a))
      return true;

    if (a.Kind != b.Kind || a.Kind is RKind.Variable or RKind.Integer or RKind.Float or RKind.Bool)
      return false;

    if (a.Length != b.Length || a.Mutable != b.Mutable || !ReferenceEquals(a.Declaration, b.Declaration) && a.Kind != RKind.Closure && a.Kind != RKind.Iterator)
      return false;

    if (a.Kind is RKind.Closure or RKind.Iterator)
      return ReferenceEquals(a.Declaration, b.Declaration);

    if (a.Element is not null && !this.TryUnify(a.Element, b.Element!))
      return false;

    if (a.Elements.Count != b.Elements.Count)
      return false;

    for (var index = 0; index < a.Elements.Count; ++index)
      if (!this.TryUnify(a.Elements[index], b.Elements[index]))
        return false;

    return true;
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

    if (Occurs(variable, type))
      return false;

    var fits = variable.VariableKind switch {
      VariableKind.Integer => type.Kind == RKind.Integer,
      VariableKind.Float => type.Kind == RKind.Float,
      _ => true
    };
    if (fits)
      variable.Bound = type;

    return fits;
  }

  /// <summary>Whether a variable occurs in a type, which would make binding it to the type infinite.</summary>
  private static bool Occurs(RType variable, RType type) {
    var resolved = type.Resolved;
    if (ReferenceEquals(resolved, variable))
      return true;

    return (resolved.Element is { } element && resolved.Kind is not (RKind.Closure or RKind.Iterator) && Occurs(variable, element))
      || resolved.Kind is not (RKind.Closure or RKind.Iterator) && resolved.Elements.Any(part => Occurs(variable, part));
  }

  /// <summary>
  /// A value of one type where another is expected, at a coercion site of The Rust Reference ("Type
  /// coercions"): <c>&amp;mut T</c> to <c>&amp;T</c>, <c>&amp;[T; N]</c> and <c>&amp;Vec&lt;T&gt;</c> to
  /// <c>&amp;[T]</c> (unsizing and deref coercion), a function item or a non-capturing closure to a
  /// function pointer; otherwise the types must unify.
  /// </summary>
  private void Coerce(RType expected, RType found, Span at) {
    var a = expected.Resolved;
    var b = found.Resolved;
    if (a.Kind == RKind.Reference && b.Kind == RKind.Reference && (!a.Mutable || b.Mutable)) {
      var target = a.Element!.Resolved;
      var source = b.Element!.Resolved;
      if (target.Kind == RKind.Slice && source.Kind is RKind.Array or RKind.Vec) {
        this.Unify(target.Element!, source.Element!, at);
        return;
      }

      // Deref coercion: &String is a &str, &&str too.
      if (target.Kind == RKind.Str && (source.Kind == RKind.String || source.IsText))
        return;

      if (a.Mutable != b.Mutable) {
        this.Unify(a.Element!, b.Element!, at);
        return;
      }
    }

    // A closure that captures nothing coerces to a function pointer, which the subset has no value of a closure for.
    if (a.Kind == RKind.Function && b.Kind == RKind.Closure && b.Declaration is ClosureInfo closure) {
      if (closure.Captures.Count > 0)
        throw this.Invalid("E0308: mismatched types: closures can only be coerced to `fn` types if they do not capture any variables", at);

      this.Refuse(new Refusal(RustFrontend.EscapingClosure, "closure coerced to a function pointer", at));
      return;
    }

    this.Unify(expected, found, at);
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

  private void Reset(ModuleScope module, RType? self) {
    this._scopes.Clear();
    this._localFunctions.Clear();
    this._contexts.Clear();
    this._variables.Clear();
    this._undecided.Clear();
    this._expressions.Clear();
    this._deferred.Clear();
    this._errors.Clear();
    this._closures.Clear();
    this._module = module;
    this._self = self;
  }

  /// <summary>Checks a function; <see langword="false"/> when it does not compile or uses a construct outside the subset (already reported).</summary>
  public bool Function(FunctionSignature signature) {
    if (this._nestedChecked.Contains(signature))
      return true;

    this.Reset(signature.Module ?? this._crate.Root, signature.Receiver is { } receiver ? receiver.Dereferenced : this.SelfOf(signature));
    this._enclosing = signature;
    this._typeParameters = signature.Substitution;
    this._instantiations.Clear();
    try {
      this.Body(signature);
      this.Finish();
      foreach (var check in this._deferred)
        check();

      this.Moves(signature.Item);
      this.Instantiate();
    } catch (CheckAbort) {
      return false;
    } finally {
      this._typeParameters = null;
    }

    return true;
  }

  private RType? SelfOf(FunctionSignature signature) => this._selfTypes.GetValueOrDefault(signature);

  /// <summary>Rejects the use of a moved value in a function, as <c>rustc</c>'s borrow checker does (<c>E0382</c>).</summary>
  private void Moves(FunctionItem function) {
    if (MoveCheck.Run(function, this._typed) is { } error)
      throw this.Invalid($"E0382: use of moved value: `{error.Binding.Name}`", error.At);
  }

  /// <summary>The <c>Self</c> type of the associated functions of an impl, which have no receiver.</summary>
  private readonly Dictionary<FunctionSignature, RType> _selfTypes = new(ReferenceEqualityComparer.Instance);

  /// <summary>Checks the parameters and the body of a function, in a new set of scopes; a nested function is checked the same way.</summary>
  private void Body(FunctionSignature signature) {
    var (scopes, locals, contexts, result, closures) = (this._scopes.ToList(), this._localFunctions.ToList(), this._contexts.ToList(), this._result, this._closures.ToList());
    this._scopes.Clear();
    this._contexts.Clear();
    this._closures.Clear();
    // A nested function sees the items of the blocks around it, never their variables.
    this._localFunctions.Clear();
    this._localFunctions.AddRange(locals);
    this._result = signature.Result;
    var scope = new Dictionary<string, Binding>();
    this._scopes.Add(scope);
    var item = signature.Item;
    try {
      if (signature.Receiver is { } receiver) {
        var self = new Binding("self", receiver, item.MutableReceiver) { Initialized = true };
        scope["self"] = self;
        this._typed.Bindings[item] = self;
      }

      for (var index = 0; index < item.Parameters.Count; ++index)
        this.Irrefutable(item.Parameters[index].Pattern, signature.Parameters[index], true, "parameter");

      var body = this.Block(item.Body);
      this.Coerce(signature.Result, body, item.Body.Tail?.At ?? item.Body.At);
    } finally {
      this._scopes.Clear();
      this._scopes.AddRange(scopes);
      this._localFunctions.Clear();
      this._localFunctions.AddRange(locals);
      this._contexts.Clear();
      this._contexts.AddRange(contexts);
      this._closures.Clear();
      this._closures.AddRange(closures);
      this._result = result;
    }
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
      if (Undecided(type) && !this.IsError(type))
        throw this.Unresolved(binding.Name, at);

    foreach (var (expression, type) in this._expressions)
      if (Undecided(type) && !this.HoldsError(type))
        throw this.Unresolved(Describe(expression), expression.At);

    // The IR holds a string in a list or an array, never in an optional value or a struct.
    if (this._expressions.FirstOrDefault(pair => HoldsText(pair.Type)) is ({ } held, { } holder))
      this.Refuse(new Refusal(RustFrontend.Unsupported, $"'{holder}', a value holding text", held.At));
  }

  /// <summary>Whether an option, a result or a tuple holds text, which no IR optional and no IR struct can.</summary>
  private static bool HoldsText(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind switch {
      RKind.Option => resolved.Element!.IsText || HoldsText(resolved.Element),
      RKind.Result => resolved.Element!.IsText || resolved.Error.IsText || HoldsText(resolved.Element) || HoldsText(resolved.Error),
      RKind.Tuple => resolved.Elements.Any(element => element.IsText || HoldsText(element)),
      _ => false
    };
  }

  /// <summary>Whether a type has a refused part, about which everything has been said.</summary>
  private bool HoldsError(RType type) {
    var resolved = type.Resolved;
    if (this.IsError(resolved))
      return true;

    if (resolved.Kind is RKind.Closure or RKind.Iterator)
      return false;

    return (resolved.Element is { } element && this.HoldsError(element)) || resolved.Elements.Any(this.HoldsError);
  }

  /// <summary>Whether a type still has a variable in it.</summary>
  private static bool Undecided(RType type) {
    var resolved = type.Resolved;
    if (resolved.Kind == RKind.Variable)
      return true;

    if (resolved.Kind is RKind.Closure or RKind.Iterator)
      return false;

    return (resolved.Element is { } element && Undecided(element)) || resolved.Elements.Any(Undecided);
  }

  // ---------------------------------------------------------------- scopes

  private Binding Declare(string name, RType type, bool mutable, object node, Span at, bool initialized) {
    var binding = new Binding(name, type, mutable) { Initialized = initialized, Depth = this._closures.Count };
    this._scopes[^1][name] = binding;
    this._typed.Bindings[node] = binding;
    if (Undecided(type))
      this._undecided.Add((type, binding, at));

    return binding;
  }

  private Binding? Lookup(string name) {
    for (var index = this._scopes.Count - 1; index >= 0; --index)
      if (this._scopes[index].TryGetValue(name, out var binding)) {
        this.Captured(binding);
        return binding;
      }

    return null;
  }

  /// <summary>Records a variable a closure names from outside it as captured by every closure it is named from.</summary>
  private void Captured(Binding binding) {
    if (binding.Constant is not null)
      return;

    for (var depth = binding.Depth; depth < this._closures.Count; ++depth)
      if (!this._closures[depth].Captures.Contains(binding))
        this._closures[depth].Captures.Add(binding);
  }

  /// <summary>A function declared in a block around the current point, by name.</summary>
  private FunctionSignature? LocalFunction(string name) {
    for (var index = this._localFunctions.Count - 1; index >= 0; --index)
      if (this._localFunctions[index].TryGetValue(name, out var function))
        return function;

    return null;
  }

  /// <summary>Checks a constant or a static against its declared type; once it passes, every function may name it.</summary>
  public bool Constant(ConstItem constant, ModuleScope module, RType? self = null) {
    this.Reset(module, self);
    this._scopes.Add([]);
    try {
      var type = this.Resolve(constant.Type, false) ?? throw new CheckAbort();
      this.Coerce(type, this.Value(constant.Value), constant.Value.At);
      this.Finish();
      foreach (var check in this._deferred)
        check();

      this._constants[constant] = new Binding(constant.Name, type, false, constant) { Initialized = true };
      if (constant.IsStatic)
        this._typed.Statics.Add(this._constants[constant]);
    } catch (CheckAbort) {
      return false;
    }

    return true;
  }

  // ---------------------------------------------------------------- blocks and statements

  private RType Block(Block block) {
    this._scopes.Add([]);
    var functions = new Dictionary<string, FunctionSignature>();
    this._localFunctions.Add(functions);
    try {
      // The items of a block are visible in all of it, before and after their declaration.
      foreach (var statement in block.Statements) {
        if (statement is FunctionStatement nested)
          functions[nested.Item.Name] = this.Nested(nested.Item);
        else if (statement is ConstStatement constant)
          this.LocalConstant(constant.Item);
      }

      var diverges = false;
      foreach (var statement in block.Statements)
        diverges |= this.Statement(statement);

      var type = block.Tail is null ? diverges ? RType.Never : RType.Unit : this.Value(block.Tail);
      return diverges && type.Resolved.Kind == RKind.Unit && block.Tail is null ? RType.Never : type;
    } finally {
      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._localFunctions.RemoveAt(this._localFunctions.Count - 1);
    }
  }

  private void LocalConstant(ConstItem constant) {
    var type = this.Resolve(constant.Type, false) ?? throw new CheckAbort();
    var saved = this._closures.ToList();
    this._closures.Clear();
    try {
      this.Coerce(type, this.Value(constant.Value), constant.Value.At);
    } finally {
      this._closures.AddRange(saved);
    }

    var binding = new Binding(constant.Name, type, false, constant) { Initialized = true };
    this._scopes[^1][constant.Name] = binding;
    this._constants[constant] = binding;
    if (constant.IsStatic)
      this._typed.Statics.Add(binding);
  }

  /// <summary>Checks a statement; <see langword="true"/> when control never goes on after it.</summary>
  private bool Statement(Statement statement) {
    switch (statement) {
      case LetStatement let:
        return this.Let(let);
      case ExpressionStatement expression:
        return this.Value(expression.Expression).Resolved.Kind == RKind.Never;
      case ConstStatement or FunctionStatement:
        return false;
      default:
        this.Refuse(((RefusedStatement)statement).Refusal);
        return false;
    }
  }

  private bool Let(LetStatement let) {
    var annotated = let.Type is null ? null : this.Resolve(let.Type, true) ?? this.ErrorType();
    var type = annotated;
    var diverges = false;
    if (let.Initializer is not null) {
      if (let is { Pattern: TuplePattern { Elements.Count: 2 }, Initializer: MethodCallExpr { Name: "split_at" or "split_at_mut" } split })
        this._splitInitializer = split;

      var initializer = this.Value(let.Initializer, annotated);
      this._splitInitializer = null;
      diverges = initializer.Resolved.Kind == RKind.Never;
      if (type is null)
        type = initializer;
      else
        this.Coerce(type, initializer, let.Initializer.At);
    }

    type ??= this.Fresh(VariableKind.Any);
    if (let.Else is not null) {
      if (let.Initializer is null)
        throw this.Invalid("let ... else needs an initializer", let.At);

      var otherwise = this.Block(let.Else);
      if (otherwise.Resolved.Kind != RKind.Never)
        throw this.Invalid("E0308: `else` clause of `let...else` does not diverge", let.Else.At);

      this.Pattern(let.Pattern, type, false);
      return diverges;
    }

    this.Irrefutable(let.Pattern, type, let.Initializer is not null, "let");
    return diverges;
  }

  /// <summary>
  /// Binds an irrefutable pattern of a <c>let</c> or a parameter: a name, <c>_</c>, a tuple, a struct
  /// or a reference pattern made of them. A refutable pattern there is <c>E0005</c>.
  /// </summary>
  private void Irrefutable(Pattern pattern, RType type, bool initialized, string where) {
    if (pattern is IdentifierPattern identifier && this.PatternConstant(identifier) is null) {
      if (type.Resolved.Kind == RKind.Unit && where == "let")
        this.Refuse(new Refusal(RustFrontend.Unsupported, "unit value '()'", identifier.At));

      this.Declare(identifier.Name, type, identifier.Mutable, identifier, identifier.At, initialized);
      return;
    }

    this.Pattern(pattern, type, false);
    this._deferred.Add(() => {
      if (!Exhaustiveness.Irrefutable(pattern, type, this._typed))
        throw this.Invalid($"E0005: refutable pattern in {(where == "let" ? "local binding" : "function argument")}", pattern.At);
    });
  }

  // ---------------------------------------------------------------- expressions

  /// <summary>Checks an expression whose value is used.</summary>
  private RType Value(Expr expression, RType? expected = null) => this.Expression(expression, expected);

  private RType Expression(Expr expression, RType? expected = null) {
    var type = this.ExpressionUnrecorded(expression, expected);
    this._typed.Types[expression] = type;
    this._expressions.Add((expression, type));
    return type;
  }

  private RType ExpressionUnrecorded(Expr expression, RType? expected) => expression switch {
    IntegerLiteral literal => this.Integer(literal, false),
    FloatLiteral literal => literal.Suffix is null ? this.Fresh(VariableKind.Float) : RType.Named(literal.Suffix)!,
    BoolLiteral => RType.Bool,
    StringLiteral => RType.StrRef,
    CharLiteral => RType.Char,
    ByteStringLiteral literal => RType.Reference(RType.Array(RType.U8, literal.Bytes.Count), false),
    PathExpr path => this.Path(path, expected),
    UnaryExpr unary => this.Unary(unary),
    ReferenceExpr reference => this.Reference(reference, expected),
    DerefExpr deref => this.Deref(deref),
    BinaryExpr binary => this.Binary(binary),
    AssignExpr assign => this.Assign(assign),
    CastExpr cast => this.Cast(cast),
    MethodCallExpr call => this.Method(call, expected),
    CallExpr call => this.Call(call, expected),
    FieldExpr field => this.Field(field),
    IndexExpr index => this.Index(index),
    TupleExpr tuple => this.Tuple(tuple, expected),
    ArrayExpr array => this.Array(array, expected),
    ArrayRepeatExpr array => this.ArrayRepeat(array, expected),
    StructExpr structure => this.StructExpression(structure),
    RangeExpr range => this.Refused(RustFrontend.Unsupported, "range expression outside a for loop, an iterator or an index", range.At),
    BlockExpr block => this.BlockExpression(block),
    IfExpr conditional => this.If(conditional),
    MatchExpr match => this.Match(match),
    WhileExpr loop => this.While(loop),
    LoopExpr loop => this.Loop(loop),
    ForExpr loop => this.For(loop),
    BreakExpr jump => this.Break(jump),
    ContinueExpr jump => this.Continue(jump),
    ReturnExpr jump => this.Return(jump),
    TryExpr attempt => this.Try(attempt),
    ClosureExpr closure => this.Closure(closure, expected),
    MacroExpr macro => this.Macro(macro, expected),
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

  private RType Unary(UnaryExpr unary) {
    var operand = unary is { Op: "-", Operand: IntegerLiteral literal } ? this.Literal(literal, true) : this.Value(unary.Operand);
    if (this.TraitUnary(unary, operand) is { } overloaded)
      return overloaded;

    // The operators of the primitive types are implemented for references to them too.
    var type = this.Fresh(VariableKind.Any);
    this._deferred.Add(() => {
      var resolved = Primitive(operand);
      var valid = unary.Op == "-" ? resolved.Kind == RKind.Float || (resolved.Kind == RKind.Integer && resolved.IsSigned) : resolved.Kind is RKind.Integer or RKind.Bool;
      if (!valid && !this.IsError(resolved))
        throw this.Invalid($"E0600: cannot apply unary operator `{unary.Op}` to type `{operand}`", unary.At);

      if (unary.Op == "-" && resolved.Kind == RKind.Integer && unary.Operand is not IntegerLiteral && -Constants.Evaluate(unary.Operand, this._typed) > resolved.Maximum)
        throw this.Invalid("this arithmetic operation will overflow (arithmetic_overflow)", unary.At);
    });
    this.Unify(type, StripShared(operand), unary.At);
    return type;
  }

  /// <summary>The type an operator of a primitive type works on: a shared reference to a primitive is the primitive.</summary>
  private static RType Primitive(RType type) => StripShared(type).Resolved;

  private static RType StripShared(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind == RKind.Reference && !resolved.Mutable && resolved.Element!.Resolved.Kind is RKind.Integer or RKind.Float or RKind.Bool ? resolved.Element : type;
  }

  /// <summary>An integer literal under a unary minus: recorded as the operand, its range checked with the sign.</summary>
  private RType Literal(IntegerLiteral literal, bool negated) {
    var type = this.Integer(literal, negated);
    this._typed.Types[literal] = type;
    return type;
  }

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

    if (this.TextOperator(binary, left, right) is { } text)
      return text;

    if (this.TraitOperator(binary, StripShared(left), right) is { } overloaded)
      return overloaded;

    if (StripShared(left).Resolved.Kind == RKind.Char && !_comparisons.Contains(binary.Op))
      throw this.Invalid($"E0369: cannot apply `{binary.Op}` to `char`", binary.At);

    this._deferred.Add(() => this.KnownOverflow(binary, StripShared(left)));
    if (_comparisons.Contains(binary.Op)) {
      this.Unify(left, right, binary.At);
      this._deferred.Add(() => this.Comparable(binary, left));
      return RType.Bool;
    }

    if (binary.Op is "<<" or ">>") {
      this.Operands(binary.Op, left, binary.At, type => type.Kind == RKind.Integer);
      this.Operands(binary.Op, right, binary.At, type => type.Kind == RKind.Integer);
      return StripShared(left);
    }

    // `&a op b`, `a op &b` and `&a op &b` of the primitives: the result is the primitive.
    var leftValue = StripShared(left);
    this.Unify(leftValue, StripShared(right), binary.At);
    this.Operands(binary.Op, leftValue, binary.At, _bitwise.Contains(binary.Op)
      ? type => type.Kind is RKind.Integer or RKind.Bool
      : type => type.Kind is RKind.Integer or RKind.Float);
    return leftValue;
  }

  /// <summary>Whether the operands of a comparison may be compared: primitives, and for <c>==</c> and <c>!=</c> the types that derive <c>PartialEq</c>.</summary>
  private void Comparable(BinaryExpr binary, RType type) {
    var resolved = type.Dereferenced;
    if (this.IsError(resolved) || resolved.Kind is RKind.Integer or RKind.Float or RKind.Bool or RKind.Never or RKind.Char or RKind.String or RKind.Str)
      return;

    if (binary.Op is "==" or "!=" && Equatable(resolved))
      return;

    throw this.Invalid($"E0369: binary operation `{binary.Op}` cannot be applied to type `{type}`", binary.At);
  }

  /// <summary>Whether <c>==</c> is defined on a type: primitives, and tuples, options, results, arrays and the structs and enums deriving <c>PartialEq</c> of them.</summary>
  private static bool Equatable(RType type) {
    var resolved = type.Dereferenced;
    return resolved.Kind switch {
      RKind.Integer or RKind.Float or RKind.Bool or RKind.Unit or RKind.Char or RKind.String or RKind.Str => true,
      RKind.Tuple => resolved.Elements.All(Equatable),
      RKind.Option or RKind.Array => Equatable(resolved.Element!),
      RKind.Result => Equatable(resolved.Element!) && Equatable(resolved.Error),
      RKind.Struct => resolved.Struct!.Derives.Contains("PartialEq"),
      RKind.Enum => resolved.Enum!.Derives.Contains("PartialEq"),
      _ => false
    };
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
      "+" => left.Value + right.Value,
      "-" => left.Value - right.Value,
      "*" => left.Value * right.Value,
      "/" or "%" => BigInteger.Divide(left.Value, right.Value),
      _ => null
    };
    if (result is { } value && (value < resolved.Minimum || value > resolved.Maximum))
      throw this.Invalid("this arithmetic operation will overflow (arithmetic_overflow)", binary.At);
  }

  /// <summary>Requires, once the types are decided, that an operand of an operator is of a type the operator is defined on.</summary>
  private void Operands(string op, RType type, Span at, Func<RType, bool> valid)
    => this._deferred.Add(() => {
      var resolved = Primitive(type);
      if (!valid(resolved) && resolved.Kind != RKind.Never && !this.IsError(resolved))
        throw this.Invalid($"E0369: no implementation for `{type} {op} {type}`", at);
    });

  private RType Assign(AssignExpr assign) {
    // The value is checked first: it is evaluated first, and its type may decide the place's.
    var value = this.Value(assign.Value);
    var place = this.Place(assign.Target, assign.Op == "=");
    if (place is null)
      return RType.Unit;

    if (assign.Op is "<<" or ">>") {
      this.Operands(assign.Op, place, assign.At, type => type.Kind == RKind.Integer);
      this.Operands(assign.Op, value, assign.At, type => type.Kind == RKind.Integer);
      return RType.Unit;
    }

    if (assign.Op == "=") {
      this.Coerce(place, value, assign.Value.At);
      return RType.Unit;
    }

    if (this.TraitAssign(assign, place, value))
      return RType.Unit;

    if (assign.Op == "+" && place.Resolved.Kind == RKind.String) {
      this.Coerce(RType.StrRef, value, assign.Value.At);
      return RType.Unit;
    }

    this.Unify(place, StripShared(value), assign.Value.At);
    this.Operands(assign.Op + "=", place, assign.At, _bitwise.Contains(assign.Op)
      ? type => type.Kind is RKind.Integer or RKind.Bool
      : type => type.Kind is RKind.Integer or RKind.Float);
    return RType.Unit;
  }

  /// <summary>
  /// Checks the target of an assignment: a variable, <c>*r</c>, a field or an element of a place
  /// that may be written. The type of the place, or <see langword="null"/> when it was refused.
  /// </summary>
  private RType? Place(Expr target, bool plain) {
    switch (target) {
      case PathExpr { Segments.Count: 1 } path: {
        var binding = this.Lookup(path.Segments[0]);
        if (binding is null) {
          if (this.LocalFunction(path.Segments[0]) is null && this._crate.Lookup(this._module, path.Segments[0], new(ReferenceEqualityComparer.Instance)) is null)
            throw this.Invalid($"E0425: cannot find value `{path.Segments[0]}` in this scope", path.At);

          throw this.Invalid("E0070: invalid left-hand side of assignment", path.At);
        }

        if (binding.Constant is not null)
          throw this.Invalid("E0070: invalid left-hand side of assignment", path.At);

        this._typed.Bindings[path] = binding;
        this._typed.Types[path] = binding.Type;
        if (!binding.Mutable && (binding.Initialized || !plain))
          throw this.Invalid($"E0384: cannot assign twice to immutable variable `{binding.Name}`", target.At);

        if (binding.Depth < this._closures.Count && this._closures[^1].Expression.Move)
          this.Refuse(new Refusal(RustFrontend.EscapingClosure, $"move closure assigns the captured variable `{binding.Name}`", target.At));

        binding.Initialized = true;
        return binding.Type;
      }
      case FieldExpr or IndexExpr or DerefExpr: {
        var type = this.Value(target);
        if (!this.Writable(target))
          throw this.Invalid($"E0594: cannot assign to `{Describe(target)}`, which is behind a `&` reference or not declared as mutable", target.At);

        return type;
      }
      default:
        this.Value(target);
        if (target is RefusedExpr)
          return null;

        throw this.Invalid("E0070: invalid left-hand side of assignment", target.At);
    }
  }

  /// <summary>Whether a place expression may be written: a mutable variable, a field or element of one, or what a <c>&amp;mut</c> points to.</summary>
  private bool Writable(Expr place) {
    switch (place) {
      case PathExpr path when this._typed.Bindings.TryGetValue(path, out var binding):
        return binding.Mutable || binding.Type.Resolved is { Kind: RKind.Reference, Mutable: true };
      case FieldExpr field:
        return this.ThroughMutable(field.Target);
      case IndexExpr index:
        return this.ThroughMutable(index.Target);
      case DerefExpr deref:
        return this._typed.TypeOf(deref.Operand) is { Kind: RKind.Reference, Mutable: true };
      default:
        return false;
    }
  }

  /// <summary>Whether a field or an element of a value may be written: the value is a mutable place, or a <c>&amp;mut</c> reaches it.</summary>
  private bool ThroughMutable(Expr target) {
    var type = this._typed.TypeOf(target);
    if (type.Kind == RKind.Reference)
      return type.Mutable;

    return this.Writable(target);
  }

  private static string Describe(Expr expression) => expression switch {
    PathExpr path => path.Text,
    FieldExpr field => $"{Describe(field.Target)}.{field.Name}",
    IndexExpr index => $"{Describe(index.Target)}[..]",
    DerefExpr deref => "*" + Describe(deref.Operand),
    MethodCallExpr call => $"{Describe(call.Receiver)}.{call.Name}()",
    _ => "expression"
  };

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
      // A char casts to every integer type and only u8 to char (The Rust Reference, "Type cast expressions").
      var valid = target.Kind == RKind.Bool ? from.Kind == RKind.Bool
        : target.Kind == RKind.Char ? from.Kind == RKind.Char || from == RType.U8
        : target.Kind is RKind.Integer or RKind.Float && (from.Kind is RKind.Integer or RKind.Float or RKind.Bool || (from.Kind is RKind.Enum or RKind.Char && target.Kind == RKind.Integer));
      if (!valid && !this.IsError(from))
        throw this.Invalid($"E0054: cannot cast `{from}` as `{target}`", cast.At);
    });
    return target;
  }

  // ---------------------------------------------------------------- control flow

  private RType BlockExpression(BlockExpr block) {
    if (block.Label is null)
      return this.Block(block.Block);

    var context = new Context(block, block.Label, false, true) { ClosureDepth = this._closures.Count };
    this._contexts.Add(context);
    var type = this.Block(block.Block);
    this._contexts.RemoveAt(this._contexts.Count - 1);
    return context.BreakType is null ? type : this.Join(type, context.BreakType, block.At);
  }

  private RType If(IfExpr conditional) {
    RType then;
    if (conditional.Pattern is { } pattern) {
      var scrutinee = this.Value(conditional.Condition);
      this._scopes.Add([]);
      this.Pattern(pattern, scrutinee, false);
      then = this.Block(conditional.Then);
      this._scopes.RemoveAt(this._scopes.Count - 1);
    } else {
      this.Unify(RType.Bool, this.Value(conditional.Condition), conditional.Condition.At);
      then = this.Block(conditional.Then);
    }

    if (conditional.Else is null) {
      if (then.Resolved.Kind != RKind.Never)
        this.Unify(RType.Unit, then, conditional.Then.Tail?.At ?? conditional.At);

      return RType.Unit;
    }

    var otherwise = this.Expression(conditional.Else);
    return this.Join(then, otherwise, conditional.Else.At);
  }

  private RType While(WhileExpr loop) {
    this._scopes.Add([]);
    if (loop.Pattern is { } pattern) {
      var scrutinee = this.Value(loop.Condition);
      this.Pattern(pattern, scrutinee, false);
    } else {
      this.Unify(RType.Bool, this.Value(loop.Condition), loop.Condition.At);
    }

    this._contexts.Add(new Context(loop, loop.Label, true, false) { ClosureDepth = this._closures.Count });
    var body = this.Block(loop.Body);
    this._contexts.RemoveAt(this._contexts.Count - 1);
    this._scopes.RemoveAt(this._scopes.Count - 1);
    this.Unify(RType.Unit, body, loop.Body.Tail?.At ?? loop.At);
    return RType.Unit;
  }

  private RType Loop(LoopExpr loop) {
    var context = new Context(loop, loop.Label, true, true) { ClosureDepth = this._closures.Count };
    this._contexts.Add(context);
    var body = this.Block(loop.Body);
    this._contexts.RemoveAt(this._contexts.Count - 1);
    this.Unify(RType.Unit, body, loop.Body.Tail?.At ?? loop.At);
    return context.BreakType ?? RType.Never;
  }

  private Context Target(string? label, bool forContinue, Span at) {
    for (var index = this._contexts.Count - 1; index >= 0; --index) {
      var context = this._contexts[index];
      if (context.ClosureDepth < this._closures.Count)
        break;

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
    var result = this._closures.Count > 0 ? this._closures[^1].Result : this._result;
    var value = jump.Value is null ? RType.Unit : this.Value(jump.Value, result);
    this.Coerce(result, value, jump.Value?.At ?? jump.At);
    return RType.Never;
  }

}
