using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The bounded pointers of the IR in JavaScript (#1331), which has no pointers: a pointer is a region and a byte
/// offset in it (capability <c>*p, a pointer</c>, replaced by "view and index"). A pointer that stays in its function is two
/// variables, <c>p</c> for the region and <c>p$o</c> for the offset; one that is passed is the array
/// <c>[region, offset]</c>, which the function called takes apart first. A local whose address is taken lives in an
/// array of one element. The helpers of <c>Helpers/Pointers.cs</c> check every load and store against the region and
/// throw the classes of the traps: <c>IndexOutOfRangeError</c>, <c>TypeError</c> for the null pointer, and
/// <c>ArithmeticError</c> for an ordering of pointers into two regions.
/// </summary>
public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The locals of the function being written whose address is taken, each with the array of one element it lives in.</summary>
    private Dictionary<IrLocal, IrLocal> _boxes = [];

    /// <summary>Whether the module holds pointers, which the header then explains.</summary>
    private bool _pointers;

    /// <summary>The name of the variable that holds the offset of a pointer variable.</summary>
    private string Offset(IrLocal local) => this.Local(local) + "$o";

    /// <summary>A number literal of an <c>int</c>.</summary>
    private static JsNumber Count(long value) => Number(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>The size of a pointer's element type and the name of its kind as a <c>DataView</c> spells it.</summary>
    private static (JsNumber Size, JsString Kind) Kind(IrType element) => (Count(element.Size), new JsString(Accessor(element)));

    /// <summary>
    /// What a function needs before its body for its pointers: the address-taken locals noted, each pointer parameter
    /// taken apart into its region and its offset, and each address-taken parameter put into its array.
    /// </summary>
    private List<JsStatement> Pointers(IrFunction function, int skipped) {
      this._boxes = IrTree.Nodes(function.Body).OfType<IrIntrinsic>()
        .Where(node => node.Op == IrIntrinsicOp.AddressOf)
        .Select(node => ((IrLocalRef)node.Arguments[0]).Local)
        .Distinct()
        .ToDictionary(local => local, local => new IrLocal(local.Name, IrType.Array(local.Type)));
      var statements = new List<JsStatement>();
      foreach (var parameter in function.Parameters.Skip(skipped)) {
        var name = this.Local(parameter);
        if (parameter.Type.IsPointer) {
          this._pointers = true;
          statements.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(name + "$o", new JsIndex(Id(name), Count(1)))]));
          statements.Add(new JsExpressionStatement(new JsAssignment(Id(name), "=", new JsIndex(Id(name), Count(0)))));
        } else if (this._boxes.TryGetValue(parameter, out var box)) {
          this._renamed[box] = name;
          statements.Add(new JsExpressionStatement(new JsAssignment(Id(name), "=", this.ArrayOf(parameter.Type, [Id(name)]))));
        }
      }

      if (this._boxes.Count > 0 || function.Locals.Any(local => local.Type.IsPointer))
        this._pointers = true;

      return statements;
    }

    /// <summary>
    /// A statement about a pointer variable or an address-taken local: its declaration, which declares its region and
    /// its offset or its array, and an assignment. <see langword="false"/> for any other statement.
    /// </summary>
    private bool PointerStatement(IrStmt statement, List<JsStatement> statements) {
      switch (statement) {
        case IrDeclare declare when declare.Local.Type.IsPointer || this._boxes.ContainsKey(declare.Local):
          statements.Add(this.PointerDeclaration(declare));
          return true;
        case IrAssign assign when assign.Local.Type.IsPointer || this._boxes.ContainsKey(assign.Local):
          statements.AddRange(this.PointerEffects(assign).Select(effect => new JsExpressionStatement(effect)));
          return true;
        default:
          return false;
      }
    }

    /// <summary>
    /// A statement of the step of a loop about a pointer or an address-taken local: a declaration goes in front of the
    /// loop, its value and an assignment into the step's expressions. <see langword="false"/> for any other statement.
    /// </summary>
    private bool PointerStep(IrStmt statement, List<JsStatement> declarations, List<JsExpression> parts) {
      switch (statement) {
        case IrDeclare declare when declare.Local.Type.IsPointer || this._boxes.ContainsKey(declare.Local):
          declarations.Add(this.PointerDeclaration(declare with { Initializer = null }));
          if (declare.Initializer is { } value)
            parts.AddRange(this.PointerEffects(new IrAssign(declare.Local, value)));

          return true;
        case IrAssign assign when assign.Local.Type.IsPointer || this._boxes.ContainsKey(assign.Local):
          parts.AddRange(this.PointerEffects(assign));
          return true;
        default:
          return false;
      }
    }

    /// <summary>The declaration of a pointer variable - its region and its offset - or of the array an address-taken local lives in.</summary>
    private JsVariableDeclaration PointerDeclaration(IrDeclare declare) {
      var local = declare.Local;
      if (!local.Type.IsPointer) {
        var value = declare.Initializer is null ? this.Zero(local.Type) : this.Value(declare.Initializer);
        var array = this.ArrayOf(local.Type, [value]);
        var boxed = this.Declare(local);
        this._renamed[this._boxes[local]] = boxed;
        return new JsVariableDeclaration(this.Let, [new JsDeclarator(boxed, array)]);
      }

      if (declare.Initializer is IrIntrinsic { Op: IrIntrinsicOp.Pin } pin) {
        var pinned = this.Pinned(pin);
        var name = this.Declare(local);
        // A pin of an element keeps its offset in the region, which is read after it is made.
        var offset = pin.Arguments.Count == 2 ? new JsMember(Id(name), "i") : (JsExpression)Count(0);
        return new JsVariableDeclaration(this.Let, [new JsDeclarator(name, pinned), new JsDeclarator(name + "$o", offset)]);
      }

      var (region, at) = declare.Initializer is null ? (null, null) : this.Located(declare.Initializer);
      var declared = this.Declare(local);
      return new JsVariableDeclaration(this.Let, [new JsDeclarator(declared, region), new JsDeclarator(declared + "$o", at)]);
    }

    /// <summary>The region a pin makes: of an array or a typed view, or of a view over a plain array, from its first element or from one of them.</summary>
    private JsExpression Pinned(IrIntrinsic pin) {
      var source = pin.Arguments[0];
      var (size, kind) = Kind(pin.Type.Element!);
      var plain = source.Type.IsView && !this.Typed(source.Type);
      return pin.Arguments.Count == 2
        ? this.Helper(plain ? "$pinatv" : "$pinat", [this.Text(source), this.Text(pin.Arguments[1]), size, kind])
        : this.Helper(plain ? "$pinv" : "$pin", [this.Text(source), size, kind]);
    }

    /// <summary>The assignments of a pointer variable - its region where that changes, then its offset - or of the element an address-taken local lives in.</summary>
    private IEnumerable<JsExpression> PointerEffects(IrAssign assign) {
      var local = assign.Local;
      if (!local.Type.IsPointer) {
        yield return new JsAssignment(new JsIndex(this.Variable(this._boxes[local]), Count(0)), "=", this.Value(assign.Value));
        yield break;
      }

      var (region, offset) = this.Located(assign.Value);
      var name = this.Local(local);
      if (region is not JsIdentifier { Name: var same } || same != name)
        yield return new JsAssignment(Id(name), "=", region);

      if (offset is not JsIdentifier { Name: var kept } || kept != name + "$o")
        yield return new JsAssignment(Id(name + "$o"), "=", offset);
    }

    /// <summary>
    /// A pointer as its region and its offset, each an expression without an effect but the making of a region, which
    /// is evaluated once: a variable, the null pointer, a pointer moved or cast, the address of a local or a new
    /// <c>stackalloc</c>. A pointer that is no expression of these - one passed in - is a variable.
    /// </summary>
    private (JsExpression Region, JsExpression Offset) Located(IrExpr pointer) {
      switch (pointer) {
        case IrLocalRef reference:
          return (this.Variable(reference.Local), Id(this.Offset(reference.Local)));
        case IrNull:
          return (new JsNull(), Count(0));
        case IrIntrinsic { Op: IrIntrinsicOp.PointerCast } cast:
          return this.Located(cast.Arguments[0]);
        case IrIntrinsic { Op: IrIntrinsicOp.PointerAdd } moved:
          var (region, offset) = this.Located(moved.Arguments[0]);
          return (region, Moved(offset, this.Scaled(moved.Arguments[1], moved.Type.Element!.Size)));
        case IrIntrinsic { Op: IrIntrinsicOp.AddressOf } address:
          var local = ((IrLocalRef)address.Arguments[0]).Local;
          var (size, kind) = Kind(local.Type);
          return (this.Helper("$pin0", [this.Variable(this._boxes[local]), size, kind]), Count(0));
        default:
          var allocation = (IrIntrinsic)pointer;
          var element = allocation.Type.Element!;
          var (bytes, name) = Kind(element);
          return (this.Helper("$pin0", [this.Text(new IrNewArray(IrType.Array(element), allocation.Arguments[0])), bytes, name]), Count(0));
      }
    }

    /// <summary>A count of elements as bytes: the product, folded for a constant.</summary>
    private JsExpression Scaled(IrExpr count, int size)
      => count is IrConst constant ? Count(constant.Value * size)
        : size == 1 ? this.Text(count)
        : Infix(this.Operand(count), "*", Count(size));

    /// <summary>An offset moved by bytes, folded where both are numbers.</summary>
    private static JsExpression Moved(JsExpression offset, JsExpression bytes) => (offset, bytes) switch {
      (JsNumber { Value: 0 }, _) => bytes,
      (_, JsNumber { Value: 0 }) => offset,
      (JsNumber first, JsNumber second) => Count((long)(first.Value + second.Value)),
      (_, JsNumber { Value: < 0 } back) => Infix(offset, "-", Count((long)-back.Value)),
      _ => Infix(offset, "+", bytes)
    };

    /// <summary>
    /// An expression of the pointers that is a value: a load, a difference, a comparison, a pointer where it is passed,
    /// and a read or a step of an address-taken local. <see langword="null"/> for any other expression.
    /// </summary>
    private (JsExpression Node, bool Atomic)? PointerWrite(IrExpr expression) {
      switch (expression) {
        case IrLocalRef { Type.IsPointer: true } or IrNull { Type.IsPointer: true }:
        case IrIntrinsic { Op: IrIntrinsicOp.PointerAdd or IrIntrinsicOp.PointerCast or IrIntrinsicOp.AddressOf }:
        case IrIntrinsic { Op: IrIntrinsicOp.StackAlloc, Type.IsPointer: true }:
          // A pointer that is passed: the pair as one value.
          var (region, offset) = this.Located(expression);
          return (new JsArray([region, offset]), true);
        case IrLocalRef reference when this._boxes.TryGetValue(reference.Local, out var box):
          return (new JsIndex(this.Variable(box), Count(0)), true);
        case IrStep { Target: IrLocalRef target } step when this._boxes.TryGetValue(target.Local, out var stepped):
          return this.Expression(step with { Target = new IrArrayLoad(new IrLocalRef(stepped), new IrConst(IrType.I32, 0)) });
        case IrIntrinsic intrinsic when IrPointers.IsPointerOperation(intrinsic.Op):
          return this.PointerOperation(intrinsic);
        default:
          return null;
      }
    }

    /// <summary>A load, a store, a difference or a comparison of pointers, through the helpers that check the region.</summary>
    private (JsExpression Node, bool Atomic) PointerOperation(IrIntrinsic intrinsic) {
      this.Native("*p, a pointer");
      var arguments = intrinsic.Arguments;
      var (region, offset) = this.Located(arguments[0]);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.PointerLoad when Reinterpreted(arguments[0]) && this.Native("BigInt64Array"):
          // A reinterpreting read where every region is a typed array: a DataView of the bytes, lowest first, unaligned.
          var view = this.Helper("$pdv", [region, offset, Count(intrinsic.Type.Size)]);
          return (new JsCall(new JsMember(view, "get" + Accessor(intrinsic.Type)), [Count(0), new JsBoolean(true)]), true);
        case IrIntrinsicOp.PointerLoad:
          var (size, kind) = Kind(intrinsic.Type);
          return (this.Helper("$pget", [region, offset, size, kind]), true);
        case IrIntrinsicOp.PointerStore:
          var (width, name) = Kind(arguments[1].Type);
          return (this.Helper("$pset", [region, offset, width, name, this.Value(arguments[1])]), true);
      }

      var (other, at) = this.Located(arguments[1]);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.PointerEqual:
          return (this.Helper("$peq", [region, offset, other, at]), true);
        case IrIntrinsicOp.PointerNotEqual:
          return (new JsUnary("!", this.Helper("$peq", [region, offset, other, at])), false);
        case IrIntrinsicOp.PointerDifference:
          var bytes = this.Helper("$pcmp", [region, offset, other, at]);
          var size = arguments[0].Type.Element!.Size;
          // The bytes between two pointers of one region are a multiple of the size: the division is exact.
          JsExpression count = size == 1 ? bytes : Infix(Infix(bytes, "/", Count(size)), "|", Count(0));
          if (!this.Halves)
            return (Invoke("BigInt", count), true);

          // The pair of halves of the count, its sign extended: the count is kept in the variable a postfix step keeps its old value in.
          this._previous = true;
          return (Parenthesized(new JsSequence([new JsAssignment(Id(Previous), "=", count),
            Halves(Infix(Parenthesized(Infix(Id(Previous), ">>", Number("31"))), ">>>", Number("0")), Infix(Id(Previous), ">>>", Number("0")))])), true);
        default:
          var symbol = intrinsic.Op switch {
            IrIntrinsicOp.PointerLess => "<",
            IrIntrinsicOp.PointerLessOrEqual => "<=",
            IrIntrinsicOp.PointerGreater => ">",
            _ => ">="
          };
          return (Infix(this.Helper("$pcmp", [region, offset, other, at]), symbol, Count(0)), false);
      }
    }

    /// <summary>Whether a pointer is read as another type than it was made for: a cast, moved or not.</summary>
    private static bool Reinterpreted(IrExpr pointer) => pointer switch {
      IrIntrinsic { Op: IrIntrinsicOp.PointerCast } => true,
      IrIntrinsic { Op: IrIntrinsicOp.PointerAdd } moved => Reinterpreted(moved.Arguments[0]),
      _ => false
    };

  }

}
