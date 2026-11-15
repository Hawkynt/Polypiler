using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// Lowers one checked function into the structured IR. Rust is expression-oriented and the IR is
/// not: a block, an <c>if</c>, a <c>match</c> or a <c>loop</c> that yields a value becomes statements
/// that leave it in a local. To keep Rust's strict left-to-right evaluation, an operand whose
/// lowering needed statements makes every operand before it a local first
/// (<see cref="Operands(Expr[])"/>), and an operand an operation reads twice or out of order is
/// read once into a local (<see cref="Stable"/>). Once control cannot go on - after a
/// <c>return</c>, a <c>break</c>, a <c>continue</c> or an expression of type <c>!</c> - nothing
/// more is emitted into the statement list.
/// <para>
/// References follow from the borrow rules: a shared reference to a value cannot see the value
/// change while it lives, so it is the value; a <c>&amp;mut</c> to a value is the only way to reach
/// it while it lives, so a local one is the place it points to, and a parameter one is the value
/// copied in and copied back out with the result - which no other code can tell from writing through
/// the reference, because none can look at the place before the call returns. References to slices,
/// arrays and vectors are the views, arrays and lists themselves, which the IR shares.
/// </para>
/// </summary>
internal sealed partial class FunctionLowering {

  private readonly ModuleLowering _module;
  private readonly Typed _typed;
  private readonly OverflowChecks _mode;
  private readonly Action<string, string, Span> _report;
  private readonly List<IrLocal> _locals = [];
  private readonly HashSet<string> _names = new(StringComparer.Ordinal);
  private readonly Dictionary<Binding, IrExpr> _values = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<Binding, Place> _places = new(ReferenceEqualityComparer.Instance);
  private List<IrStmt> _statements = [];
  private bool _diverged;

  /// <summary>The places of the <c>&amp;mut</c> parameters the function copies back out with its result, in parameter order.</summary>
  private readonly List<(string Name, Place Place)> _outputs = [];
  private IrStructType? _outputType;
  private FunctionSignature _signature = null!;

  public FunctionLowering(ModuleLowering module, Typed typed, OverflowChecks mode, Action<string, string, Span> report) {
    this._module = module;
    this._typed = typed;
    this._mode = mode;
    this._report = report;
    this._names.UnionWith(module.Names);
  }

  /// <summary>Whether <c>+ - *</c>, unary <c>-</c>, <c>abs</c>, <c>pow</c> and the shifts panic on overflow.</summary>
  private bool Checked => this._mode == OverflowChecks.Panic;

  /// <summary>Reports a construct the lowering cannot express; the import then fails.</summary>
  private void Refuse(string code, string construct, Span at) => this._report(code, construct, at);

  /// <summary>The IR type of a type of the subset.</summary>
  private IrType Ir(RType type) => this._module.Ir(type);

  /// <summary>Whether a type is a <c>&amp;mut</c> to a value the IR copies, which the lowering tracks as a place.</summary>
  private static bool IsExclusiveValue(RType type) {
    var resolved = type.Resolved;
    return resolved is { Kind: RKind.Reference, Mutable: true } && resolved.Element!.Resolved.Kind is not (RKind.Slice or RKind.Array or RKind.Vec or RKind.Map or RKind.Set);
  }

  /// <summary>The struct a function with <c>&amp;mut</c> value parameters returns: its result and the final values of those parameters.</summary>
  public static IReadOnlyList<int> Outputs(FunctionSignature signature) {
    var outputs = new List<int>();
    if (signature.Receiver is { } receiver && IsExclusiveValue(receiver))
      outputs.Add(-1);

    for (var index = 0; index < signature.Parameters.Count; ++index)
      if (IsExclusiveValue(signature.Parameters[index]))
        outputs.Add(index);

    return outputs;
  }

  public IrFunction Lower(FunctionSignature signature) {
    this._signature = signature;
    var item = signature.Item;
    var parameters = new List<IrLocal>();
    var unpack = new List<(Pattern Pattern, IrLocal Local, RType Type)>();
    if (signature.Receiver is { } receiver) {
      var binding = this._typed.Bindings[item];
      var local = new IrLocal(this.Name("self"), this.Ir(receiver));
      parameters.Add(local);
      if (IsExclusiveValue(receiver)) {
        this._places[binding] = new LocalPlace(local);
        this._outputs.Add(("self", new LocalPlace(local)));
      } else {
        this._values[binding] = new IrLocalRef(local);
      }
    }

    for (var index = 0; index < item.Parameters.Count; ++index) {
      var parameter = item.Parameters[index];
      var type = signature.Parameters[index];
      if (parameter.Pattern is IdentifierPattern identifier) {
        var binding = this._typed.Bindings[identifier];
        var local = new IrLocal(this.Name(identifier.Name), this.Ir(type));
        parameters.Add(local);
        if (IsExclusiveValue(type)) {
          this._places[binding] = new LocalPlace(local);
          this._outputs.Add((identifier.Name, new LocalPlace(local)));
        } else {
          this._values[binding] = new IrLocalRef(local);
        }

        continue;
      }

      var argument = new IrLocal(this.Name("arg" + index), this.Ir(type));
      parameters.Add(argument);
      unpack.Add((parameter.Pattern, argument, type));
    }

    var result = signature.Result.Resolved.Kind is RKind.Unit or RKind.Never ? IrType.Void : this.Ir(signature.Result);
    if (this._outputs.Count > 0)
      this._outputType = this._module.OutputOf(signature);

    foreach (var (pattern, local, type) in unpack)
      this.Bind(pattern, new ValueItem(new IrLocalRef(local)), type);

    var value = this.Block(item.Body);
    if (!this._diverged && (result != IrType.Void || this._outputs.Count > 0))
      this.EmitReturn(result == IrType.Void ? null : this.Converted(value!, signature.Result, this._typed.Types.GetValueOrDefault(item.Body.Tail ?? (Expr)new TupleExpr(item.Body.At, []))));

    var returns = this._outputType is null ? result : IrType.Struct(this._outputType);
    return new IrFunction(signature.IrName, signature.IsExported, returns, parameters, this._locals, new IrBlock(this._statements));
  }

  /// <summary>Leaves the function, or the closure being inlined, with a value; a function with <c>&amp;mut</c> parameters returns them with it.</summary>
  private void EmitReturn(IrExpr? value) {
    if (this._closureFrames.Count > 0) {
      var frame = this._closureFrames[^1];
      if (value is not null && frame.Value is not null)
        this.Emit(new IrAssign(frame.Value, value));

      this.Jump(frame, false);
      return;
    }

    if (this._outputType is { } output) {
      var fields = new List<IrExpr>();
      if (value is not null)
        fields.Add(value);

      foreach (var (_, place) in this._outputs)
        fields.Add(this.Read(place));

      this.Emit(new IrReturn(new IrStructNew(IrType.Struct(output), fields)));
    } else {
      this.Emit(new IrReturn(value));
    }

    this._diverged = true;
  }

  // ---------------------------------------------------------------- locals and statements

  /// <summary>A name no other local and no function of the module has.</summary>
  private string Name(string wanted) {
    var name = wanted;
    for (var suffix = 2; !this._names.Add(name); ++suffix)
      name = wanted + suffix;

    return name;
  }

  private IrLocal NewLocal(string name, IrType type) {
    var local = new IrLocal(this.Name(name), type);
    this._locals.Add(local);
    return local;
  }

  /// <summary>Declares a local with a first value and returns a reference to it.</summary>
  private IrLocalRef Temporary(IrExpr value, string name = "value") {
    var local = this.NewLocal(name, value.Type);
    this.Emit(new IrDeclare(local, value));
    return new IrLocalRef(local);
  }

  /// <summary>A local of a type that starts at its zero, for a value control flow assigns.</summary>
  private IrLocal Slot(IrType type, string name) {
    var local = this.NewLocal(name, type);
    this.Emit(new IrDeclare(local, this.Zero(type)));
    return local;
  }

  /// <summary>The zero of a type: 0, false, none, the empty string, the default struct, a new empty list or array.</summary>
  private IrExpr Zero(IrType type) {
    if (type == IrType.Void)
      return new IrConst(IrType.I32, 0);

    if (type.IsOptional)
      return new IrNone(type);

    if (type.IsString)
      return new IrStrConst("");

    if (type.IsStruct)
      return type.StructType!.HasDefault ? new IrStructDefault(type) : new IrStructNew(type, [.. type.StructType.Fields.Select(field => this.Zero(field.Type))]);

    if (type.IsList)
      return new IrCollection(IrCollectionOp.ListNew, type, []);

    if (type.Kind == IrTypeKind.Map)
      return new IrCollection(IrCollectionOp.MapNew, type, []);

    if (type.Kind == IrTypeKind.Set)
      return new IrCollection(IrCollectionOp.SetNew, type, []);

    if (type.IsArray)
      return new IrNewArray(type, new IrConst(IrType.I32, 0));

    if (type.IsView)
      return new IrIntrinsic(IrIntrinsicOp.ViewOf, type, [new IrNewArray(IrType.Array(type.Element!), new IrConst(IrType.I32, 0))]);

    if (type.IsFunction)
      return new IrNull(type);

    if (type.IsEnum)
      return new IrConst(type, type.EnumType!.Members[0].Value);

    return new IrConst(type, 0);
  }

  private void Emit(IrStmt statement) {
    if (!this._diverged)
      this._statements.Add(statement);
  }

  /// <summary>Lowers into a statement list of its own: the statements, the value, and whether control can leave them at the end.</summary>
  private (List<IrStmt> Statements, IrExpr? Value, bool Diverged) Capture(Func<IrExpr?> lower) {
    var (statements, diverged) = (this._statements, this._diverged);
    this._statements = [];
    this._diverged = false;
    try {
      var value = lower();
      return (this._statements, value, this._diverged);
    } finally {
      this._statements = statements;
      this._diverged = diverged;
    }
  }

  /// <summary>Appends statements lowered elsewhere; control does not go on when they never end.</summary>
  private void Splice(List<IrStmt> statements, bool diverged) {
    foreach (var statement in statements)
      this.Emit(statement);

    this._diverged |= diverged;
  }

  /// <summary>Whether reading an expression twice, or later, gives the same value without an effect: a constant or a local.</summary>
  private static bool IsSimple(IrExpr expression) => expression is IrConst or IrLocalRef;

  /// <summary>An expression that can be read twice or later: itself when simple, otherwise a local holding its value.</summary>
  private IrExpr Stable(IrExpr expression, string name = "value") => IsSimple(expression) ? expression : this.Temporary(expression, name);

  /// <summary>
  /// Lowers operands left to right. When one of them needs statements, every operand before it that
  /// is not a constant is read into a local before those statements, so that it is read first.
  /// </summary>
  private IrExpr[] Operands(params Expr[] operands) => this.Operands(operands, null);

  /// <summary>Lowers operands left to right, each converted to the IR type its parameter wants when one is given.</summary>
  private IrExpr[] Operands(IReadOnlyList<Expr> operands, IReadOnlyList<RType?>? targets) {
    var values = new IrExpr[operands.Count];
    for (var index = 0; index < operands.Count; ++index) {
      var mark = this._statements.Count;
      var target = targets?[index];
      values[index] = target is null ? this.Value(operands[index]) : this.Converted(this.Value(operands[index]), target, this._typed.Types.GetValueOrDefault(operands[index]));
      if (this._statements.Count == mark || this._diverged)
        continue;

      var inserted = 0;
      for (var earlier = 0; earlier < index; ++earlier) {
        if (values[earlier] is IrConst)
          continue;

        var local = this.NewLocal("operand", values[earlier].Type);
        this._statements.Insert(mark + inserted++, new IrDeclare(local, values[earlier]));
        values[earlier] = new IrLocalRef(local);
      }
    }

    return values;
  }

  // ---------------------------------------------------------------- blocks and statements

  private IrExpr? Block(Block block) {
    foreach (var statement in block.Statements)
      this.Statement(statement);

    return block.Tail is null ? null : this.Lower(block.Tail);
  }

  private void Statement(Statement statement) {
    switch (statement) {
      case LetStatement let:
        this.Let(let);
        break;
      case ExpressionStatement expression:
        this.Effect(expression.Expression);
        break;
    }

    // A constant is lowered where it is used, a nested function is a function of the module.
  }

  private void Let(LetStatement let) {
    if (let.Else is { } otherwise) {
      this.LetElse(let, otherwise);
      return;
    }

    // A split_at makes two views, never a pair.
    if (let is { Pattern: TuplePattern { Elements.Count: 2 } split, Initializer: MethodCallExpr { Name: "split_at" or "split_at_mut" } call }) {
      this.SplitAt(split, call);
      return;
    }

    // The pair of an overflowing_* method is two values, as phase one imported it.
    if (let is { Pattern: TuplePattern { Elements.Count: 2 } pair, Initializer: MethodCallExpr { Name: var name } overflowing } && name.StartsWith(OverflowingPrefix, StringComparison.Ordinal)
        && this._typed.TypeOf(overflowing.Receiver).IsNumber && pair.Elements.All(element => element is IdentifierPattern or WildcardPattern)) {
      var (value, flag) = this.Overflowing(overflowing);
      if (pair.Elements[0] is IdentifierPattern first)
        this.Declared(first, value);
      else
        this.Stable(value);

      if (pair.Elements[1] is IdentifierPattern second)
        this.Declared(second, flag);

      return;
    }

    if (let.Pattern is IdentifierPattern identifier && this._typed.Bindings.TryGetValue(identifier, out var binding) && binding.Constant is null) {
      var type = binding.Type;
      if (type.Resolved.Kind == RKind.Closure) {
        if (let.Initializer is not null)
          this.Lower(let.Initializer);

        return;
      }

      if (let.Initializer is null) {
        this.Declared(identifier, this.Zero(this.Ir(type)));
        return;
      }

      // `let r = &mut place;`: the binding is the place.
      if (IsExclusiveValue(type) && this.PlaceOfReference(let.Initializer) is { } place) {
        this._places[binding] = place;
        return;
      }

      this.Declared(identifier, this.Converted(this.Value(let.Initializer), type, this._typed.Types.GetValueOrDefault(let.Initializer)));
      return;
    }

    if (let.Initializer is null)
      return;

    var initializerType = this._typed.TypeOf(let.Initializer);
    var lowered = this.Lower(let.Initializer);
    if (let.Pattern is WildcardPattern) {
      if (lowered is not null && !this._diverged && !IsSimple(lowered))
        this.Effect(lowered);

      return;
    }

    if (lowered is null)
      return;

    this.Bind(let.Pattern, new ValueItem(lowered), initializerType);
  }

  /// <summary>Declares the local of a binding with its first value.</summary>
  private void Declared(IdentifierPattern pattern, IrExpr value) {
    var binding = this._typed.Bindings[pattern];
    var local = this.NewLocal(pattern.Name, value.Type == IrType.Void ? this.Ir(binding.Type) : value.Type);
    this.Emit(new IrDeclare(local, value));
    this._values[binding] = new IrLocalRef(local);
  }

  /// <summary>Lowers an expression for its effect: a value that may trap or call is still computed.</summary>
  private void Effect(Expr expression) {
    var value = this.Lower(expression);
    if (value is null || this._diverged)
      return;

    this.Effect(value);
  }

  private void Effect(IrExpr value) {
    if (IsSimple(value) || value is IrConstRef or IrNone)
      return;

    if (value is IrCall or IrInvoke)
      this.Emit(new IrExprStmt(value));
    else
      this.Temporary(value, "unused");
  }

  // ---------------------------------------------------------------- expressions

  /// <summary>
  /// The value of an expression that has one; after an expression that never yields, a stand-in
  /// nothing reads. A place of an array type is copied, as Rust copies the array (or moves it, which
  /// no one can tell from a copy).
  /// </summary>
  private IrExpr Value(Expr expression) {
    var value = this.Lower(expression) ?? this.Zero(this.Ir(this._typed.TypeOf(expression)));
    if (this._diverged)
      return value;

    var type = this._typed.TypeOf(expression);
    if (IsPlaceExpression(expression) && HoldsArray(type))
      return this.Duplicate(value, type);

    return value;
  }

  /// <summary>Whether an expression names a place rather than a new value.</summary>
  private bool IsPlaceExpression(Expr expression) => expression switch {
    PathExpr path => this._typed.Bindings.ContainsKey(path),
    FieldExpr or IndexExpr or DerefExpr => true,
    _ => false
  };

  /// <summary>Whether a value of a type holds an array the IR shares by reference: an array, or a struct or a tuple with one.</summary>
  private static bool HoldsArray(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind switch {
      RKind.Array => true,
      RKind.Struct => resolved.Struct!.Fields.Any(field => HoldsArray(field.Type)),
      RKind.Tuple => resolved.Elements.Any(HoldsArray),
      _ => false
    };
  }

  /// <summary>Lowers an expression: its value, or <see langword="null"/> for one of type <c>()</c> or <c>!</c>.</summary>
  private IrExpr? Lower(Expr expression) {
    var value = expression switch {
      IntegerLiteral literal => Integer(this._typed.TypeOf(literal), literal.Value),
      FloatLiteral literal => Float(this._typed.TypeOf(literal), literal.Text),
      BoolLiteral literal => new IrConst(IrType.Bool, literal.Value ? 1 : 0),
      StringLiteral literal => new IrStrConst(literal.Value),
      CharLiteral literal => new IrConst(IrType.U32, literal.Value),
      ByteStringLiteral literal => new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [.. literal.Bytes.Select(value => (IrExpr)new IrConst(IrType.U8, value))]),
      PathExpr path => this.Path(path),
      UnaryExpr unary => this.Unary(unary),
      ReferenceExpr reference => this.Reference(reference),
      DerefExpr deref => this.Deref(deref),
      BinaryExpr binary => this.Binary(binary),
      AssignExpr assign => this.Assign(assign),
      CastExpr cast => this.Cast(cast),
      MethodCallExpr call => this.Method(call),
      CallExpr call => this.Call(call),
      FieldExpr field => this.Field(field),
      IndexExpr index => this.Index(index),
      TupleExpr tuple => this.Tuple(tuple),
      ArrayExpr array => this.Array(array),
      ArrayRepeatExpr array => this.ArrayRepeat(array),
      StructExpr structure => this.StructExpression(structure),
      BlockExpr block => this.BlockExpression(block),
      IfExpr conditional => this.If(conditional),
      MatchExpr match => this.Match(match),
      WhileExpr loop => this.While(loop),
      LoopExpr loop => this.Loop(loop),
      ForExpr loop => this.For(loop),
      BreakExpr jump => this.Break(jump),
      ContinueExpr jump => this.Continue(jump),
      TryExpr attempt => this.Try(attempt),
      ClosureExpr closure => this.ClosureValue(closure),
      MacroExpr macro => this.Macro(macro),
      // The checker lets no other expression through: a refused one stops the import.
      _ => this.Return((ReturnExpr)expression)
    };
    if (this._typed.TypeOf(expression).Kind == RKind.Never)
      this._diverged = true;

    return value;
  }

}
