using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>What the verifier says of a pointer kept where it could outlive its region: a field, a global, a constant or a result.</summary>
  internal const string PointerStored = "a pointer is stored outside the stack";

  /// <summary>A pointer lives on the stack only: no constant, global or field of a struct or a class is of a pointer type.</summary>
  private static void Pointers(IrModule module, List<Diagnostic> diagnostics) {
    var places = module.Constants.Select(constant => (constant.Name, constant.Type))
      .Concat(module.Globals.Select(global => (global.Name, global.Type)))
      .Concat(module.Types.Where(type => type is IrStructType or IrClassType).SelectMany(type => type.Fields.Select(field => ($"{type.Name}.{field.Name}", field.Type))));
    foreach (var (name, type) in places.Where(place => place.Type.IsPointer))
      diagnostics.Add(Error(name, PointerStored));
  }

  private sealed partial class FunctionCheck {

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
    private void Pointers() {
      this.Require(!function.ReturnType.IsPointer, PointerStored);
      foreach (var parameter in function.Parameters)
        this._depths[parameter] = 0;

      var captured = IrTree.Nodes(function.Body).OfType<IrLambda>().SelectMany(lambda => lambda.Captures).ToHashSet();
      foreach (var lambda in IrTree.Nodes(function.Body).OfType<IrLambda>())
        this.Require(!lambda.Captures.Concat(lambda.Parameters).Concat(lambda.Locals).Any(local => local.Type.IsPointer) && !lambda.ReturnType.IsPointer
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
