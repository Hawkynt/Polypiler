using System.Collections.Generic;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>A name a scope binds: a parameter or local, or a constant.</summary>
internal sealed class Binding(string name, RType type, bool mutable, ConstItem? constant = null) {

  public string Name { get; } = name;

  public RType Type { get; } = type;

  public bool Mutable { get; } = mutable;

  /// <summary>The constant the name stands for, or <see langword="null"/> for a variable.</summary>
  public ConstItem? Constant { get; } = constant;

  /// <summary>Whether a variable has been given a value: a <c>let</c> without one may be assigned once.</summary>
  public bool Initialized { get; set; }

}

/// <summary>The signature of a function of the file.</summary>
internal sealed record FunctionSignature(FunctionItem Item, IReadOnlyList<RType> Parameters, RType Result);

/// <summary>
/// A <c>for</c> over an integer range: the bounds, whether the end is included, and the adapters in
/// source order, which do not commute (<c>(0..11).step_by(3).rev()</c> yields 9, 6, 3, 0 and
/// <c>(0..11).rev().step_by(3)</c> 10, 7, 4, 1).
/// </summary>
internal sealed record RangeLoop(Expr Start, Expr End, bool Inclusive, bool Reversed, Expr? Step, bool StepBeforeReverse);

/// <summary>What the checker found out about the functions of a file, for the lowering to read.</summary>
internal sealed class Typed {

  /// <summary>The type of every expression checked.</summary>
  public Dictionary<Expr, RType> Types { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The binding every path, identifier pattern and parameter names.</summary>
  public Dictionary<object, Binding> Bindings { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The loop or labelled block every <c>break</c> and <c>continue</c> leaves.</summary>
  public Dictionary<Expr, Expr> Targets { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The range every <c>for</c> iterates over.</summary>
  public Dictionary<ForExpr, RangeLoop> Ranges { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The signatures of the functions of the file, by name.</summary>
  public Dictionary<string, FunctionSignature> Functions { get; } = [];

  /// <summary>The type of an expression, resolved.</summary>
  public RType TypeOf(Expr expression) => this.Types[expression].Resolved;

}
