using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The typing rules of the bounded pointers (#1331): which types the operations on a
/// <see cref="IrTypeKind.Pointer"/> take. A pointer is a region - an array, a view, the memory of a
/// <c>stackalloc</c> or of one local - and a byte offset in it; <c>docs/reference/pointers.md</c> describes
/// every operation and what it traps with.
/// </summary>
public static class IrPointers {

  /// <summary>The operations on pointers, in the order of <see cref="IrIntrinsicOp"/>.</summary>
  public static IReadOnlyList<IrIntrinsicOp> Operations { get; } = [
    IrIntrinsicOp.Pin, IrIntrinsicOp.AddressOf, IrIntrinsicOp.PointerAdd, IrIntrinsicOp.PointerDifference,
    IrIntrinsicOp.PointerEqual, IrIntrinsicOp.PointerNotEqual, IrIntrinsicOp.PointerLess, IrIntrinsicOp.PointerLessOrEqual,
    IrIntrinsicOp.PointerGreater, IrIntrinsicOp.PointerGreaterOrEqual, IrIntrinsicOp.PointerLoad, IrIntrinsicOp.PointerStore,
    IrIntrinsicOp.PointerCast
  ];

  /// <summary>Whether an operation is one of the pointers.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for an operation of <see cref="Operations"/>.</returns>
  public static bool IsPointerOperation(IrIntrinsicOp op) => op is >= IrIntrinsicOp.Pin and <= IrIntrinsicOp.PointerCast;

  /// <summary>Whether a node works on pointers: an operation of <see cref="Operations"/>, or a <c>stackalloc</c> into a pointer.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns><see langword="true"/> for a node a backend writes as pointer code.</returns>
  public static bool IsPointerNode(IrIntrinsic intrinsic)
    => IsPointerOperation(intrinsic.Op) || (intrinsic.Op == IrIntrinsicOp.StackAlloc && intrinsic.Type.IsPointer);

  /// <summary>Whether an operation compares two pointers.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for the six comparisons.</returns>
  public static bool IsComparison(IrIntrinsicOp op) => op is >= IrIntrinsicOp.PointerEqual and <= IrIntrinsicOp.PointerGreaterOrEqual;

  /// <summary>Whether a comparison orders two pointers, which traps for pointers into two regions.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c>.</returns>
  public static bool Orders(IrIntrinsicOp op) => op is >= IrIntrinsicOp.PointerLess and <= IrIntrinsicOp.PointerGreaterOrEqual;

  /// <summary>
  /// What the rules say of a pointer in an iterator function: its locals live across a yield, where the region of a
  /// pin or of a local is no longer held, so no parameter, local or value of one is a pointer.
  /// </summary>
  public const string InIterator = "a pointer in an iterator function";

  /// <summary>Whether a type is a pointer to a number, through which values are read and written and which arithmetic moves.</summary>
  private static bool Typed(IrType type) => type.IsPointer && type.Element!.IsNumeric;

  /// <summary>
  /// Whether an operation on pointers, or a <c>stackalloc</c> into a pointer, takes the types; <see langword="null"/> when
  /// it is neither. Nothing is read, written or counted through a <c>void*</c>.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="result">The type of the node.</param>
  /// <param name="arguments">The types of the arguments, in order.</param>
  /// <returns>Whether the types fit; <see langword="null"/> for another operation.</returns>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (op == IrIntrinsicOp.StackAlloc && result.IsPointer)
      return arguments is [{ IsWord: true }] && Typed(result);

    if (!IsPointerOperation(op))
      return null;

    var first = arguments.Count > 0 ? arguments[0] : IrType.Void;
    bool Pair() => arguments.Count == 2 && first.IsPointer && arguments[1] == first;
    return op switch {
      IrIntrinsicOp.Pin => arguments.Count is 1 or 2 && (first.IsArray || first.IsView) && first.Element!.IsNumeric
        && arguments.Skip(1).All(type => type.IsWord) && result == IrType.Pointer(first.Element),
      IrIntrinsicOp.AddressOf => arguments is [{ IsNumeric: true } local] && result == IrType.Pointer(local),
      IrIntrinsicOp.PointerAdd => arguments is [_, { IsWord: true }] && Typed(first) && result == first,
      IrIntrinsicOp.PointerDifference => Pair() && Typed(first) && result == IrType.I64,
      IrIntrinsicOp.PointerLoad => arguments.Count == 1 && Typed(first) && result == first.Element,
      IrIntrinsicOp.PointerStore => arguments.Count == 2 && Typed(first) && arguments[1] == first.Element && result == IrType.Void,
      IrIntrinsicOp.PointerCast => arguments.Count == 1 && first.IsPointer && result.IsPointer && result.IsStorable,
      _ => Pair() && result == IrType.Bool
    };
  }

  /// <summary>
  /// What a function breaks of the rules of the bounded pointers (#1331), one message per rule: no pointer is a
  /// result, the value of a conditional, or held by a lambda; a pin and a <c>stackalloc</c> into a pointer only
  /// initialize a declaration, and the pinned local is never assigned; the address is taken only of a local that
  /// no lambda captures; no pointer outlives the block of the pin or of the local it points into; and an iterator
  /// function holds no pointer at all, <see cref="InIterator"/>.
  /// </summary>
  /// <param name="function">The function.</param>
  /// <returns>The messages, empty when the function keeps the rules.</returns>
  public static IReadOnlyList<string> Problems(IrFunction function) {
    var walk = new Lifetimes(function);
    walk.Run();
    return walk.Found;
  }

  /// <summary>Walks a function for the rules of <see cref="Problems"/>.</summary>
  private sealed class Lifetimes(IrFunction function) {

    public List<string> Found { get; } = [];

    private void Require(bool holds, string message) {
      if (!holds && !this.Found.Contains(message))
        this.Found.Add(message);
    }

    /// <summary>The depth of the block each local of the function is declared in; a parameter has 0.</summary>
    private readonly Dictionary<IrLocal, int> _depths = [];

    /// <summary>The depth of the innermost block whose end a pointer local may point past: 0 for the whole function.</summary>
    private readonly Dictionary<IrLocal, int> _lifetimes = [];

    /// <summary>The locals a <see cref="IrIntrinsicOp.Pin"/> declares, which nothing assigns.</summary>
    private readonly HashSet<IrLocal> _pinned = [];

    /// <summary>
    /// The rules of the bounded pointers (#1331): no pointer is a result, the value of a conditional, or held by
    /// a lambda; a pin and a <c>stackalloc</c> into a pointer only initialize a declaration, and the pinned local
    /// is never assigned; the address is taken only of a local that no lambda captures; and no pointer outlives
    /// the block of the pin or of the local it points into - a pointer local is assigned no pointer whose region
    /// ends before the block the local is declared in.
    /// </summary>
    public void Run() {
      this.Require(!function.ReturnType.IsPointer, IrVerifier.PointerStored);
      this.Require(!IrSequences.IsIterator(function) || !(function.Parameters.Concat(function.Locals).Any(local => local.Type.IsPointer)
        || IrTree.Nodes(function.Body).Any(node => node.Type.IsPointer)), InIterator);
      foreach (var parameter in function.Parameters)
        this._depths[parameter] = 0;

      var captured = IrTree.Nodes(function.Body).OfType<IrLambda>().SelectMany(lambda => lambda.Captures).ToHashSet();
      foreach (var lambda in IrTree.Nodes(function.Body).OfType<IrLambda>())
        this.Require(!lambda.Captures.Concat(lambda.Parameters).Concat(lambda.Locals).Any(local => local.Type.IsPointer) && lambda.Type.Signature?.Result.IsPointer != true
          && !IrTree.Nodes(lambda.Body).Any(node => node.Type.IsPointer), "a lambda holds a pointer");

      this.Pointers(function.Body, 1, captured);
    }

    private void Pointers(IrBlock block, int depth, HashSet<IrLocal> captured) {
      foreach (var statement in block.Statements) {
        switch (statement) {
          case IrDeclare declare:
            this._depths[declare.Local] = depth;
            if (declare.Initializer is IrIntrinsic { Op: IrIntrinsicOp.Pin } pin) {
              this._pinned.Add(declare.Local);
              this._lifetimes[declare.Local] = depth;
              this.Pointers(pin.Arguments, captured);
            } else if (declare.Initializer is IrIntrinsic { Op: IrIntrinsicOp.StackAlloc, Type.IsPointer: true } allocation) {
              this.Pointers(allocation.Arguments, captured);
            } else if (declare.Initializer is { } value) {
              this.Pointers([value], captured);
              this.Flows(declare.Local, value);
            }

            break;
          case IrAssign assign:
            this.Require(!this._pinned.Contains(assign.Local), $"the pinned pointer '{assign.Local.Name}' is assigned");
            this.Pointers([assign.Value], captured);
            this.Flows(assign.Local, assign.Value);
            break;
          default:
            this.Pointers(IrTree.Expressions(statement), captured);
            break;
        }

        foreach (var inner in statement is IrBlock nested ? [nested] : IrTree.Blocks(statement))
          this.Pointers(inner, depth + 1, captured);
      }
    }

    /// <summary>Checks the expressions of a statement: where a pin, a <c>stackalloc</c> into a pointer and an address may stand.</summary>
    private void Pointers(IEnumerable<IrExpr> expressions, HashSet<IrLocal> captured) {
      foreach (var node in expressions.SelectMany(IrTree.Nodes)) {
        switch (node) {
          case IrIntrinsic { Op: IrIntrinsicOp.Pin }:
            this.Require(false, "a pin that initializes no declaration");
            break;
          case IrIntrinsic { Op: IrIntrinsicOp.StackAlloc, Type.IsPointer: true }:
            this.Require(false, "a stackalloc into a pointer that initializes no declaration");
            break;
          case IrIntrinsic { Op: IrIntrinsicOp.AddressOf } address:
            var local = address.Arguments is [IrLocalRef reference] ? reference.Local : null;
            this.Require(local is not null, "the address is taken of something that is no local");
            this.Require(local is null || !captured.Contains(local), $"the address is taken of the captured local '{local?.Name}'");
            break;
          case IrConditional { Type.IsPointer: true }:
            this.Require(false, "a conditional expression of pointers");
            break;
        }
      }
    }

    /// <summary>A pointer value goes into a local: its region has to last as long as the block the local is declared in.</summary>
    private void Flows(IrLocal local, IrExpr value) {
      if (!local.Type.IsPointer)
        return;

      var lifetime = this.Lifetime(value);
      this.Require(lifetime <= this._depths.GetValueOrDefault(local), $"a pointer outlives the block of its region in '{local.Name}'");
      this._lifetimes[local] = Math.Max(this._lifetimes.GetValueOrDefault(local), lifetime);
    }

    /// <summary>The depth of the innermost block whose end the regions of the pointers an expression reads end with.</summary>
    private int Lifetime(IrExpr value) => IrTree.Nodes(value).Select(node => node switch {
      IrLocalRef { Type.IsPointer: true } reference => this._lifetimes.GetValueOrDefault(reference.Local),
      IrIntrinsic { Op: IrIntrinsicOp.AddressOf, Arguments: [IrLocalRef target] } => this._depths.GetValueOrDefault(target.Local),
      _ => 0
    }).DefaultIfEmpty().Max();

  }

}
