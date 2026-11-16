using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>Where the memory of a pointer lives.</summary>
internal enum RegionKind {

  /// <summary>An array or a view variable a pin names: the pointer indexes it.</summary>
  Variable,

  /// <summary>The memory of a <c>stackalloc</c> or of a pin of a new array, an array of its own the pointer's declaration makes.</summary>
  Stack,

  /// <summary>One local whose address is taken: the pointer stands for the local itself.</summary>
  Address,

  /// <summary>The region a pointer parameter brings along: a slice parameter of its own.</summary>
  Parameter

}

/// <summary>
/// The region of a pointer: its kind, the variable that holds it (the pinned array or view, the pointer whose
/// declaration allocates, the local whose address is taken, the pointer parameter), the type of its elements,
/// and whether it is the null pointer when it is empty - a pin of a whole array or view, and a parameter.
/// </summary>
internal sealed record PointerRegion(RegionKind Kind, IrLocal Owner, IrType Element, bool Nullable);

/// <summary>
/// The pointers of a function as safe Rust sees them (#460): every pointer is a position in one region the
/// function knows statically. A pin of a variable indexes that variable, a <c>stackalloc</c> an array of its
/// own, the address of a local stands for the local, and a pointer parameter brings its region as a slice
/// parameter; arithmetic, comparisons and differences work on positions in the region's elements. What has
/// no such form is a problem the backend refuses: a pointer given pointers into two regions, a pin of
/// something that is no variable, arithmetic on the address of a local.
/// </summary>
internal sealed class PointerRegions {

  private readonly Dictionary<IrLocal, PointerRegion> _regions = [];

  private readonly List<string> _problems = [];

  /// <summary>Why the pointers of the function have no safe Rust form; empty when they have one.</summary>
  public IReadOnlyList<string> Problems => this._problems;

  /// <summary>The region of every pointer variable of the function.</summary>
  public IReadOnlyDictionary<IrLocal, PointerRegion> Regions => this._regions;

  /// <summary>The regions the function writes through a pointer.</summary>
  public HashSet<PointerRegion> Written { get; } = [];

  /// <summary>The regions the function reads or writes through a pointer, or passes on.</summary>
  public HashSet<PointerRegion> Accessed { get; } = [];

  /// <summary>The regions the function compares a pointer of with the null pointer.</summary>
  public HashSet<PointerRegion> Compared { get; } = [];

  private PointerRegions() { }

  /// <summary>Finds the region of every pointer of a function.</summary>
  /// <param name="function">The function.</param>
  public static PointerRegions Of(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var found = new PointerRegions();
    foreach (var parameter in function.Parameters.Where(parameter => parameter.Type.IsPointer))
      found._regions[parameter] = new PointerRegion(RegionKind.Parameter, parameter, Element(parameter.Type), true);

    foreach (var statement in ArrayOwnership.Statements(function.Body)) {
      switch (statement) {
        case IrDeclare { Local.Type.IsPointer: true, Initializer: var initializer } declare:
          found.Declared(declare.Local, initializer);
          break;
        case IrAssign { Local.Type.IsPointer: true } assign:
          if (found.Of(assign.Value) is { } region && found._regions.GetValueOrDefault(assign.Local) is { } known && !Same(known, region))
            found.Problem($"the pointer '{assign.Local.Name}' given pointers into two regions");
          if (found._regions.GetValueOrDefault(assign.Local) is { Kind: RegionKind.Address })
            found.Problem("arithmetic on the address of a local");
          break;
      }

      foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
        switch (node) {
          case IrIntrinsic { Op: IrIntrinsicOp.PointerStore } store when found.Of(store.Arguments[0]) is { } written:
            found.Written.Add(written);
            found.Accessed.Add(written);
            break;
          case IrIntrinsic { Op: IrIntrinsicOp.PointerLoad } load when found.Of(load.Arguments[0]) is { } read:
            found.Accessed.Add(read);
            break;
          case IrIntrinsic comparison when IrPointers.IsComparison(comparison.Op) && comparison.Arguments.Any(argument => argument is IrNull):
            foreach (var argument in comparison.Arguments.Where(argument => argument is not IrNull))
              if (found.Of(argument, false) is { } compared)
                found.Compared.Add(compared);
            break;
          case IrIntrinsic operation when (operation.Op is IrIntrinsicOp.PointerAdd or IrIntrinsicOp.PointerDifference || IrPointers.IsComparison(operation.Op)) && found.AtAddress(operation.Arguments):
            found.Problem("arithmetic on the address of a local");
            break;
          case IrCall call when found.AtAddress(call.Arguments):
            found.Problem("the address of a local passed to a function");
            break;
          case IrCall call:
            foreach (var argument in call.Arguments.Where(argument => argument.Type.IsPointer))
              if (found.Of(argument, false) is { } passed)
                found.Accessed.Add(passed);
            break;
        }
      }
    }

    return found;
  }

  /// <summary>Whether one of some values is a pointer to the address of a local.</summary>
  private bool AtAddress(IReadOnlyList<IrExpr> values) => values.Any(value => value.Type.IsPointer && this.Of(value, false) is { Kind: RegionKind.Address });

  /// <summary>The element a pointer type reads; a byte for <c>void*</c>, which nothing reads through.</summary>
  private static IrType Element(IrType pointer) => pointer.Element is { IsNumeric: true } element ? element : IrType.U8;

  /// <summary>Whether two regions are one.</summary>
  public static bool Same(PointerRegion first, PointerRegion second) => first.Kind == second.Kind && first.Owner == second.Owner;

  private void Problem(string message) {
    if (!this._problems.Contains(message))
      this._problems.Add(message);
  }

  private void Declared(IrLocal local, IrExpr? initializer) {
    switch (initializer) {
      case IrIntrinsic { Op: IrIntrinsicOp.Pin, Arguments: [IrLocalRef { Local: var pinned }, ..] } pin:
        this._regions[local] = new PointerRegion(RegionKind.Variable, pinned, pinned.Type.Element!, pin.Arguments.Count == 1);
        break;
      case IrIntrinsic { Op: IrIntrinsicOp.Pin, Arguments: [IrNewArray or IrIntrinsic { Op: IrIntrinsicOp.ArrayOf }] }:
        // A pin of a new array: memory of its own, as a stackalloc's, that is null when it is empty.
        this._regions[local] = new PointerRegion(RegionKind.Stack, local, Element(local.Type), true);
        break;
      case IrIntrinsic { Op: IrIntrinsicOp.Pin }:
        this.Problem("a pin of something that is no variable or new array");
        break;
      case IrIntrinsic { Op: IrIntrinsicOp.StackAlloc }:
        this._regions[local] = new PointerRegion(RegionKind.Stack, local, Element(local.Type), false);
        break;
      case null:
        this.Problem($"the pointer '{local.Name}' declared without a value");
        break;
      default:
        if (this.Of(initializer) is { } region)
          this._regions[local] = region;
        break;
    }
  }

  /// <summary>The region of a pointer expression; <see langword="null"/> for the null pointer, and for an expression with no region, which is a problem.</summary>
  /// <param name="pointer">The expression, of a pointer type.</param>
  /// <param name="report">Whether an expression with no region is reported.</param>
  public PointerRegion? Of(IrExpr pointer, bool report = true) {
    switch (pointer) {
      case IrLocalRef { Local: var local } when this._regions.TryGetValue(local, out var region):
        return region;
      case IrIntrinsic { Op: IrIntrinsicOp.PointerAdd or IrIntrinsicOp.PointerCast, Arguments: [var inner, ..] }:
        return this.Of(inner, report);
      case IrIntrinsic { Op: IrIntrinsicOp.AddressOf, Arguments: [IrLocalRef { Local: var local }] }:
        return new PointerRegion(RegionKind.Address, local, local.Type, false);
      case IrNull:
        return null;
      default:
        if (report)
          this.Problem($"a pointer of the form {pointer.GetType().Name[2..]}");
        return null;
    }
  }

}
