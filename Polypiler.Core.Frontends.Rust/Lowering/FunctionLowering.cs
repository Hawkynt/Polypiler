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
/// </summary>
internal sealed partial class FunctionLowering {

  private readonly ModuleLowering _module;
  private readonly Typed _typed;
  private readonly OverflowChecks _mode;
  private readonly List<IrLocal> _locals = [];
  private readonly HashSet<string> _names = new(StringComparer.Ordinal);
  private readonly Dictionary<Binding, IrExpr> _values = new(ReferenceEqualityComparer.Instance);
  private List<IrStmt> _statements = [];
  private bool _diverged;

  public FunctionLowering(ModuleLowering module, Typed typed, OverflowChecks mode) {
    this._module = module;
    this._typed = typed;
    this._mode = mode;
    this._names.UnionWith(module.Names);
  }

  /// <summary>Whether <c>+ - *</c>, unary <c>-</c>, <c>abs</c>, <c>pow</c> and the shifts panic on overflow.</summary>
  private bool Checked => this._mode == OverflowChecks.Panic;

  public IrFunction Lower(FunctionSignature signature) {
    var item = signature.Item;
    var parameters = new List<IrLocal>();
    foreach (var parameter in item.Parameters) {
      var local = new IrLocal(this.Name(parameter.Name), this._typed.Bindings[parameter].Type.Ir);
      parameters.Add(local);
      this._values[this._typed.Bindings[parameter]] = new IrLocalRef(local);
    }

    var value = this.Block(item.Body);
    var result = signature.Result.Resolved.Kind is RKind.Unit or RKind.Never ? IrType.Void : signature.Result.Ir;
    if (result != IrType.Void)
      this.Emit(new IrReturn(value));

    return new IrFunction(item.Name, item.IsPublic, result, parameters, this._locals, new IrBlock(this._statements));
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

  /// <summary>A local of a type that starts at zero or false, for a value control flow assigns.</summary>
  private IrLocal Slot(IrType type, string name) {
    var local = this.NewLocal(name, type);
    this.Emit(new IrDeclare(local, Zero(type)));
    return local;
  }

  private static IrConst Zero(IrType type) => new(type == IrType.Void ? IrType.I32 : type, 0);

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
  private IrExpr[] Operands(params Expr[] operands) {
    var values = new IrExpr[operands.Length];
    for (var index = 0; index < operands.Length; ++index) {
      var mark = this._statements.Count;
      values[index] = this.Value(operands[index]);
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

    // A constant is lowered where it is used.
  }

  private void Let(LetStatement let) {
    if (let.Pattern is TuplePattern tuple) {
      var (value, flag) = this.Overflowing((MethodCallExpr)let.Initializer!);
      if (tuple.Elements[0] is IdentifierPattern first)
        this.Bind(first, value);
      else
        this.Stable(value);

      if (tuple.Elements[1] is IdentifierPattern second)
        this.Bind(second, flag);

      return;
    }

    if (let.Pattern is not IdentifierPattern identifier) {
      if (let.Initializer is not null)
        this.Effect(let.Initializer);

      return;
    }

    var binding = this._typed.Bindings[identifier];
    var initializer = let.Initializer is null ? Zero(binding.Type.Ir) : this.Value(let.Initializer);
    this.Bind(identifier, initializer);
  }

  /// <summary>Declares the local of a pattern's binding with its first value.</summary>
  private void Bind(IdentifierPattern pattern, IrExpr value) {
    var binding = this._typed.Bindings[pattern];
    var local = this.NewLocal(pattern.Name, binding.Type.Ir);
    this.Emit(new IrDeclare(local, value));
    this._values[binding] = new IrLocalRef(local);
  }

  /// <summary>Lowers an expression for its effect: a value that may trap or call is still computed.</summary>
  private void Effect(Expr expression) {
    var value = this.Lower(expression);
    if (value is null || this._diverged || IsSimple(value))
      return;

    if (value is IrCall)
      this.Emit(new IrExprStmt(value));
    else
      this.Temporary(value, "unused");
  }

  // ---------------------------------------------------------------- expressions

  /// <summary>The value of an expression that has one; after an expression that never yields, a stand-in nothing reads.</summary>
  private IrExpr Value(Expr expression) => this.Lower(expression) ?? Zero(this._typed.TypeOf(expression).Ir);

  /// <summary>Lowers an expression: its value, or <see langword="null"/> for one of type <c>()</c> or <c>!</c>.</summary>
  private IrExpr? Lower(Expr expression) {
    var value = expression switch {
      IntegerLiteral literal => Integer(this._typed.TypeOf(literal), literal.Value),
      FloatLiteral literal => Float(this._typed.TypeOf(literal), literal.Text),
      BoolLiteral literal => new IrConst(IrType.Bool, literal.Value ? 1 : 0),
      PathExpr path => this.Path(path),
      UnaryExpr unary => this.Unary(unary),
      BinaryExpr binary => this.Binary(binary),
      AssignExpr assign => this.Assign(assign),
      CastExpr cast => this.Cast(cast),
      MethodCallExpr call => this.Method(call),
      CallExpr call => this.Call(call),
      FieldExpr field => this.Field(field),
      BlockExpr block => this.BlockExpression(block),
      IfExpr conditional => this.If(conditional),
      MatchExpr match => this.Match(match),
      WhileExpr loop => this.While(loop),
      LoopExpr loop => this.Loop(loop),
      ForExpr loop => this.For(loop),
      BreakExpr jump => this.Break(jump),
      ContinueExpr jump => this.Continue(jump),
      // The checker lets no other expression through: a refused or a tuple one stops the import.
      _ => this.Return((ReturnExpr)expression)
    };
    if (this._typed.TypeOf(expression).Kind == RKind.Never)
      this._diverged = true;

    return value;
  }

}
