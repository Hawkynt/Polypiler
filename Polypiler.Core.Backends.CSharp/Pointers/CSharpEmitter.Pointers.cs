using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The bounded pointers of the IR in C# (#1331): unsafe code, which every profile has since C# 1. A pointer is a
/// pointer, a pin is <c>fixed</c> around the rest of its block, <c>stackalloc</c> into a pointer stays
/// <c>stackalloc</c> where the length is a constant of fewer than 1024 bytes outside every loop and is a pinned
/// array otherwise, and every method or constructor that holds pointer code is <c>unsafe</c>, no class is. Nothing is checked: an access out of
/// bounds is undefined in C#, and a check would change what the source's code costs.
/// </summary>
public static partial class CSharpEmitter {

  /// <summary>Whether a module holds pointer code in any of its functions.</summary>
  private static bool UsesPointers(IrModule module) => module.Functions.Any(UsesPointers);

  /// <summary>Whether a function holds pointer code: a variable of a pointer type or an operation on pointers.</summary>
  private static bool UsesPointers(IrFunction function)
    => function.Parameters.Concat(function.Locals).Any(local => local.Type.IsPointer)
      || IrTree.Nodes(function.Body).OfType<IrIntrinsic>().Any(IrPointers.IsPointerNode);

  /// <summary>The modifiers of the member a function becomes, with <c>unsafe</c> when it holds pointer code.</summary>
  private static IReadOnlyList<string> Unsafe(IReadOnlyList<string> modifiers, IrFunction function)
    => UsesPointers(function) ? [.. modifiers, "unsafe"] : modifiers;

  /// <summary>A profile without unsafe code cannot write a module with pointers: <see cref="Inexpressible"/>.</summary>
  private static IEnumerable<Diagnostic> PointerProblems(IrModule module, CSharpProfile profile, CSharpCapabilities table)
    => UsesPointers(module) && !table.Has(CSharpFeature.UnsafeCode, profile)
      ? [InexpressibleDescriptor.At($"'unsafe code' cannot be expressed in C# profile '{profile.Name}'")]
      : [];

  private sealed partial class Writer {

    /// <summary>The lines of the header a module with pointers has: its consuming project has to allow unsafe code.</summary>
    private IEnumerable<string> PointerNotes()
      => UsesPointers(this._module)
        ? ["Unsafe code: the project that compiles this file must set AllowUnsafeBlocks (the compiler's -unsafe)."]
        : [];

    /// <summary>Whether a <c>stackalloc</c> into a pointer stays on the stack: a constant length under the limit, outside every loop.</summary>
    private bool OnStack(IrIntrinsic allocation)
      => this.Has(CSharpFeature.StackAllocPointer) && this._steps.Count == 0
        && allocation.Arguments[0] is IrConst { Value: >= 0 } count && count.Value * allocation.Type.Element!.Size < StackLimit;

    /// <summary>
    /// A declaration whose pointer has to be pinned - a pin, or a <c>stackalloc</c> that does not stay on the stack -
    /// written as <c>fixed</c> with the rest of the block as its body; <see langword="false"/> for any other statement.
    /// </summary>
    private bool Pinned(IReadOnlyList<IrStmt> block, int index, List<CsStatement> statements) {
      if (block[index] is not IrDeclare { Initializer: IrIntrinsic { Op: IrIntrinsicOp.Pin or IrIntrinsicOp.StackAlloc, Type.IsPointer: true } pin } declare
          || (pin.Op == IrIntrinsicOp.StackAlloc && this.OnStack(pin)))
        return false;

      var local = declare.Local;
      var type = new CsType(this.TypeName(local.Type));
      var rest = block.Skip(index + 1).ToList();
      var source = pin.Arguments[0];
      var segment = pin.Op == IrIntrinsicOp.Pin && pin.Arguments.Count == 1 && this.Segmented(source);
      if (segment) {
        // The segment is read three times: a value that is no variable is held in one of its own first.
        CsExpression held = this.Text(source);
        if (source is not IrLocalRef) {
          var holder = this.Fresh(this.Spelling(local) + "Segment");
          statements.Add(new CsLocalDeclaration(new CsType(this.TypeName(source.Type)), holder, held));
          held = new CsName(holder) { Type = source.Type };
        }

        return this.PinnedSegment(held, local, type, rest, statements);
      }

      var initializer = pin.Op == IrIntrinsicOp.StackAlloc
        ? new CsArrayCreation(new CsType(this.TypeName(local.Type.Element!)), this.AtRunTime(Promoted(source), source is IrConst { Value: >= 0 }, Loose), null)
        : pin.Arguments.Count == 2 ? new CsPrefixUnary("&", this.Place(source, pin.Arguments[1]))
        : this.Text(source);
      // A fixed local is read-only; one the IR assigns is a copy of the pinned pointer.
      var assigned = rest.Any(statement => Assigns(statement, local));
      var scope = new Scope();
      this._scopes.Add(scope);
      var name = assigned ? this.Fresh(this.Spelling(local) + "Pinned") : this.Declare(local);
      var body = new List<CsStatement>();
      if (assigned)
        body.Add(new CsLocalDeclaration(type, this.Declare(local), new CsName(name)));

      this.Sequence(rest, body);
      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(scope.Within);
      statements.Add(new CsFixed(new CsLocalDeclaration(type, name, initializer), new CsBlock(body)));
      return true;
    }

    /// <summary>
    /// The pin of a view where the profile writes it as an <c>ArraySegment&lt;T&gt;</c>: the segment's array pinned,
    /// and the pointer the null pointer for a segment of no elements, as <c>fixed</c> of an empty span gives.
    /// </summary>
    private bool PinnedSegment(CsExpression segment, IrLocal local, CsType type, IReadOnlyList<IrStmt> rest, List<CsStatement> statements) {
      var scope = new Scope();
      this._scopes.Add(scope);
      var array = this.Fresh(this.Spelling(local) + "Pinned");
      var start = new CsBinary(new CsName(array), "+", new CsMemberAccess(segment, "Offset"));
      var empty = new CsBinary(new CsMemberAccess(segment, "Count"), "==", new CsIntegerLiteral(0) { Type = IrType.I32 });
      var body = new List<CsStatement> { new CsLocalDeclaration(type, this.Declare(local), new CsConditional(empty, new CsNullLiteral(), start)) };
      this.Sequence(rest, body);
      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(scope.Within);
      statements.Add(new CsFixed(new CsLocalDeclaration(type, array, new CsMemberAccess(segment, "Array")), new CsBlock(body)));
      return true;
    }

    /// <summary>The spelling of a local without an <c>@</c>, which a name made from it starts with.</summary>
    private string Spelling(IrLocal local) => this._spellings[local.Name].TrimStart('@');

    /// <summary>The element of an array or a view that <c>fixed (T* p = &amp;source[i])</c> pins.</summary>
    private CsExpression Place(IrExpr source, IrExpr index)
      => this.Segmented(source) ? this.SegmentPlace(new IrArrayLoad(source, index)) : this.Element(source, index);

    /// <summary>Whether a statement or one in it assigns a local.</summary>
    private static bool Assigns(IrStmt statement, IrLocal local)
      => (statement is IrAssign assign && assign.Local == local)
        || (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(inner => inner.Statements)).Any(inner => Assigns(inner, local));

    /// <summary>An operation on pointers as C# writes it; <see langword="null"/> for any other intrinsic.</summary>
    private (CsExpression Node, int Binding)? Pointer(IrIntrinsic intrinsic) {
      if (!IrPointers.IsPointerNode(intrinsic))
        return null;

      var arguments = intrinsic.Arguments;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.StackAlloc:
          var length = arguments[0];
          return (new CsArrayCreation(new CsType(this.TypeName(intrinsic.Type.Element!)), this.AtRunTime(Promoted(length), true, Loose), null) { OnStack = true }, Unary);
        case IrIntrinsicOp.AddressOf:
          return (new CsPrefixUnary("&", this.Operand(arguments[0], Unary)), Unary);
        case IrIntrinsicOp.PointerAdd when arguments[1] is IrConst { Value: < 0 and > int.MinValue } back:
          return (new CsBinary(this.Operand(arguments[0], Unary), "-", Bare(IrType.I32, -back.Value)), Loose);
        case IrIntrinsicOp.PointerAdd:
          return (new CsBinary(this.Operand(arguments[0], Unary), "+", this.Operand(Promoted(arguments[1]), Unary)), Loose);
        case IrIntrinsicOp.PointerCast when intrinsic.Type.Element == IrType.Void:
          // Every pointer converts to void* by itself.
          return this.Expression(arguments[0]);
        case IrIntrinsicOp.PointerCast:
          return (new CsCast(new CsType(this.TypeName(intrinsic.Type)), this.Operand(arguments[0], Cast)), Cast);
        case IrIntrinsicOp.PointerLoad when arguments[0] is IrIntrinsic { Op: IrIntrinsicOp.PointerAdd, Arguments: [{ } pointer and not IrIntrinsic { Op: IrIntrinsicOp.PointerCast }, var count] }:
          // p[i] is *(p + i).
          return (new CsElementAccess(this.Operand(pointer, Primary), this.Text(Promoted(count))) { Type = intrinsic.Type }, Primary);
        case IrIntrinsicOp.PointerLoad:
          return (new CsPrefixUnary("*", this.Operand(arguments[0], Unary)) { Type = intrinsic.Type }, Unary);
        case IrIntrinsicOp.PointerStore:
          var target = this.Pointer(new IrIntrinsic(IrIntrinsicOp.PointerLoad, arguments[1].Type, [arguments[0]]))!.Value.Node;
          return (new CsAssignment(target, "=", this.Stored(arguments[1])), Loose);
        default:
          var symbol = intrinsic.Op switch {
            IrIntrinsicOp.PointerDifference => "-",
            IrIntrinsicOp.PointerEqual => "==",
            IrIntrinsicOp.PointerNotEqual => "!=",
            IrIntrinsicOp.PointerLess => "<",
            IrIntrinsicOp.PointerLessOrEqual => "<=",
            IrIntrinsicOp.PointerGreater => ">",
            _ => ">="
          };
          return (new CsBinary(this.Operand(arguments[0], Unary), symbol, this.Operand(arguments[1], Unary)), Loose);
      }
    }

  }

}
