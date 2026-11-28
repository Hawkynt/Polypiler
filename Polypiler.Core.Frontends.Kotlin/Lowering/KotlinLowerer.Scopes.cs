using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Kotlin.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Kotlin.Lowering;

internal sealed partial class KotlinLowerer {

  /// <summary>A variable in scope: its local, and whether it is a <c>var</c>.</summary>
  private sealed record Variable(IrLocal Local, bool Mutable);

  /// <summary>A scope of names: a block, a function's parameters.</summary>
  private sealed class Names(Names? outer) {

    private readonly Dictionary<string, Variable> _variables = new(StringComparer.Ordinal);

    public Names? Outer { get; } = outer;

    public void Add(string name, Variable variable) => this._variables[name] = variable;

    public Variable? Find(string name) => this._variables.TryGetValue(name, out var variable) ? variable : this.Outer?.Find(name);

  }

  /// <summary>A loop the statements are inside of: its Kotlin label, and the flags a labelled jump from an inner loop sets.</summary>
  private sealed class Loop(string? label) {

    public string? Label { get; } = label;

    /// <summary>The flag a <c>break@label</c> of an inner loop sets, once one does.</summary>
    public IrLocal? BreakFlag { get; set; }

    /// <summary>The flag a <c>continue@label</c> of an inner loop sets, once one does.</summary>
    public IrLocal? ContinueFlag { get; set; }

  }

  private Names _scope = new(null);
  private readonly List<IrLocal> _locals = [];
  private readonly HashSet<string> _localNames = new(StringComparer.Ordinal);
  private readonly List<IrLocal> _flags = [];
  private readonly List<Loop> _loops = [];

  /// <summary>Statements an expression needs before the statement it stands in: the temporaries of a value evaluated once.</summary>
  private readonly List<IrStmt> _pending = [];

  private Signature? _function;

  private Names Scope => this._scope;

  private void BeginFunction(Signature? function) {
    this._function = function;
    this._scope = new Names(null);
    this._locals.Clear();
    this._localNames.Clear();
    this._flags.Clear();
    this._loops.Clear();
    this._pending.Clear();
  }

  /// <summary>A local with a name no other local of the function has, so that a Kotlin variable that shadows another is a variable of its own.</summary>
  private IrLocal NewLocal(string wanted, IrType type, bool declare = true) {
    var name = wanted;
    for (var suffix = 2; !this._localNames.Add(name); ++suffix)
      name = $"{wanted}_{suffix}";

    var local = new IrLocal(name, type);
    if (declare)
      this._locals.Add(local);

    return local;
  }

  /// <summary>Runs a step for what it finds out alone: every local, statement and helper it made is forgotten.</summary>
  private T Probe<T>(Func<T> step) {
    var (locals, names, pending, helpers) = (this._locals.Count, this._localNames.ToList(), this._pending.Count, this._helpers.Keys.ToList());
    try {
      return step();
    } finally {
      this._locals.RemoveRange(locals, this._locals.Count - locals);
      this._localNames.Clear();
      this._localNames.UnionWith(names);
      this._pending.RemoveRange(pending, this._pending.Count - pending);
      foreach (var helper in this._helpers.Keys.Except(helpers).ToList())
        this._helpers.Remove(helper);
    }
  }

  /// <summary>Runs a step inside a scope of its own.</summary>
  private T Scoped<T>(Func<T> step) {
    var outer = this._scope;
    this._scope = new Names(outer);
    try {
      return step();
    } finally {
      this._scope = outer;
    }
  }

  /// <summary>
  /// A value evaluated once: the expression itself where it is a variable or a constant, otherwise a
  /// temporary declared before the statement, which the value initialises.
  /// </summary>
  private IrExpr Once(IrExpr value, string name) {
    if (value is IrConst or IrLocalRef or IrConstRef or IrStrConst)
      return value;

    var temporary = this.NewLocal(name, value.Type);
    this._pending.Add(new IrDeclare(temporary, value));
    return new IrLocalRef(temporary);
  }

  /// <summary>The statements an expression of a statement needed, then the statement's own.</summary>
  private void Flush(List<IrStmt> into) {
    into.AddRange(this._pending);
    this._pending.Clear();
  }

  #region Types

  /// <summary>The IR type of a Kotlin type the subset has.</summary>
  private IrType TypeOf(KTypeRef type) {
    if (type.Nullable)
      throw Refuse(KotlinFrontend.Type, type, $"the nullable type '{type.Name}?' is outside the imported subset");

    var name = type.Name.StartsWith("kotlin.", StringComparison.Ordinal) ? type.Name[7..] : type.Name;
    if (Scalar(name) is { } scalar)
      return scalar;

    if (name == "CharArray")
      return IrType.Array(IrType.U16);

    if (name == "BooleanArray")
      throw Refuse(KotlinFrontend.Memory, type, "a BooleanArray is outside the imported subset: the IR has no array of truth values");

    if (name.EndsWith("Array", StringComparison.Ordinal) && name != "Array" && Scalar(name[..^5]) is { } element)
      return element.IsInteger && !element.IsSigned
        ? throw Refuse(KotlinFrontend.Memory, type, $"the array '{name}' of unsigned elements needs an opt-in of Kotlin and is outside the imported subset")
        : IrType.Array(element);

    if (name == "Array" && type.Arguments is [{ } argument])
      return IrType.Array(this.TypeOf(argument));

    throw Refuse(KotlinFrontend.Type, type, $"the type '{type.Name}' is outside the imported subset");
  }

  /// <summary>The IR type of a Kotlin scalar type, <c>Char</c> as the <c>u16</c> of its code unit and <c>String</c> as <c>str</c>.</summary>
  private static IrType? Scalar(string name) => name switch {
    "Byte" => IrType.I8,
    "Short" => IrType.I16,
    "Int" => IrType.I32,
    "Long" => IrType.I64,
    "UByte" => IrType.U8,
    "UShort" => IrType.U16,
    "UInt" => IrType.U32,
    "ULong" => IrType.U64,
    "Float" => IrType.F32,
    "Double" => IrType.F64,
    "Boolean" => IrType.Bool,
    "Char" => IrType.U16,
    "String" => IrType.String,
    "Unit" => IrType.Void,
    _ => null
  };

  /// <summary>
  /// The type Kotlin's arithmetic gives two operands: <c>Double</c> with a <c>Double</c>, else
  /// <c>Float</c> with a <c>Float</c>, else <c>Long</c> with a <c>Long</c>, else <c>Int</c> for the
  /// signed types; <c>ULong</c> with a <c>ULong</c>, else <c>UInt</c> for the unsigned types. A signed
  /// and an unsigned operand have no operator.
  /// </summary>
  private static IrType? Arithmetic(IrType left, IrType right) {
    if (!left.IsNumeric || !right.IsNumeric)
      return null;

    if (left.IsFloat || right.IsFloat)
      return left == IrType.F64 || right == IrType.F64 ? IrType.F64 : IrType.F32;

    if (left.IsSigned != right.IsSigned)
      return null;

    if (left.IsSigned)
      return left == IrType.I64 || right == IrType.I64 ? IrType.I64 : IrType.I32;

    return left == IrType.U64 || right == IrType.U64 ? IrType.U64 : IrType.U32;
  }

  #endregion

}
