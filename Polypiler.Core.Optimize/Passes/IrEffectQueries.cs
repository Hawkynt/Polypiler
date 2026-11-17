using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// The effect classes of PRD §17.2 over the places of an effect summary (#114, ADR 0009), and the rule a
/// pass that reorders follows: two pieces of code may change places only when their summaries do not
/// interfere (<see cref="Interferes(IrEffects, IrEffects)"/>). Ordering inside a function is the tree's
/// evaluation order; this is what proves an exception to it.
/// <para>
/// Places overlap when they are of one kind and have one name, or one of them is named <c>*</c>. An
/// <see cref="IrPlaceKind.External"/> place overlaps every other external place and an
/// <see cref="IrPlaceKind.Exception"/> place every other one: there is one world outside the program and
/// one exception being handled. Unknown memory (<see cref="IrPlaceKind.Memory"/> named <c>*</c>) overlaps
/// all storage of the program - variables, globals, fields, elements, every memory region - and volatile
/// and memory-mapped storage, but no device port, no external state, no exception state and no machine
/// register or flag. An effect that reads or writes everything overlaps every place.
/// </para>
/// <para>
/// Volatile, memory-mapped, device and external places are <b>strict</b>: a read of one is as
/// observable as a write, so two accesses of overlapping strict places never change places, two reads
/// included, and code with such an access is never merged, repeated or dropped
/// (<see cref="IsStrict(IrEffects)"/>). Unknown memory may be strict storage, so its accesses count as
/// strict accesses too.
/// </para>
/// </summary>
public static class IrEffectQueries {

  /// <summary>The name of a place that stands for every place of its kind.</summary>
  public const string Any = "*";

  /// <summary>The kinds whose places hold storage of the program, which unknown memory may be.</summary>
  private static readonly IReadOnlySet<IrPlaceKind> _memory = new HashSet<IrPlaceKind> {
    IrPlaceKind.Local, IrPlaceKind.Global, IrPlaceKind.Field, IrPlaceKind.Elements, IrPlaceKind.Memory, IrPlaceKind.Volatile, IrPlaceKind.Mmio
  };

  /// <summary>The kinds whose every access is observable.</summary>
  private static readonly IReadOnlySet<IrPlaceKind> _strict = new HashSet<IrPlaceKind> {
    IrPlaceKind.Volatile, IrPlaceKind.Mmio, IrPlaceKind.Device, IrPlaceKind.External
  };

  /// <summary>Unknown memory: a store or a load through a pointer nothing resolves.</summary>
  public static IrPlace UnknownMemory { get; } = new(IrPlaceKind.Memory, Any);

  /// <summary>Whether two places may be the same storage.</summary>
  /// <param name="first">One place.</param>
  /// <param name="second">The other.</param>
  /// <returns><see langword="true"/> unless the two are told apart by their kinds and names.</returns>
  public static bool Overlaps(IrPlace first, IrPlace second) {
    ArgumentNullException.ThrowIfNull(first);
    ArgumentNullException.ThrowIfNull(second);
    if (first.Kind == second.Kind)
      return first.Kind is IrPlaceKind.External or IrPlaceKind.Exception || first.Name == second.Name || first.Name == Any || second.Name == Any;

    return (IsUnknownMemory(first) && _memory.Contains(second.Kind)) || (IsUnknownMemory(second) && _memory.Contains(first.Kind));
  }

  /// <summary>Whether every access of a place is observable: a volatile, memory-mapped, device or external place, or unknown memory, which may be one.</summary>
  /// <param name="place">The place.</param>
  public static bool IsStrict(IrPlace place) {
    ArgumentNullException.ThrowIfNull(place);
    return _strict.Contains(place.Kind) || IsUnknownMemory(place);
  }

  /// <summary>
  /// Whether code with a summary must run exactly as often as the program says, in its order: it reads or
  /// writes a strict place, or anything at all. Such code is never merged, repeated or dropped, even
  /// when nothing reads what it gives.
  /// </summary>
  /// <param name="effects">The summary.</param>
  public static bool IsStrict(IrEffects effects) {
    ArgumentNullException.ThrowIfNull(effects);
    return effects.ReadsEverything || effects.WritesEverything || effects.Reads.Concat(effects.Writes).Any(IsStrict);
  }

  /// <summary>
  /// Whether two pieces of code with these summaries may give another result when they change places;
  /// <see langword="false"/> only where the summaries prove it cannot. They interfere when
  /// <list type="bullet">
  /// <item>one may trap or run forever and the other writes, traps or makes a strict access - a trap
  /// happens at its place, with every effect before it and none after it, and of two traps the earlier
  /// wins;</item>
  /// <item>one writes a place the other reads or writes;</item>
  /// <item>both access overlapping strict places, reads included.</item>
  /// </list>
  /// Two pieces that only read, or that touch places told apart, do not interfere; neither do two that
  /// may only run forever, or one that may trap and one that only reads ordinary storage.
  /// </summary>
  /// <param name="first">The summary of the code that runs first.</param>
  /// <param name="second">The summary of the code that runs second.</param>
  /// <returns><see langword="true"/> when the order must be kept.</returns>
  public static bool Interferes(IrEffects first, IrEffects second) {
    ArgumentNullException.ThrowIfNull(first);
    ArgumentNullException.ThrowIfNull(second);
    if ((Stops(first) && Observable(second)) || (Stops(second) && Observable(first)))
      return true;

    var (firstAll, secondAll) = (Accessed(first), Accessed(second));
    if (Overlap(first.WritesEverything, first.Writes, second.ReadsEverything || second.WritesEverything, secondAll)
      || Overlap(second.WritesEverything, second.Writes, first.ReadsEverything || first.WritesEverything, firstAll))
      return true;

    return Overlap(Everything(first), [.. firstAll.Where(IsStrict)], Everything(second), [.. secondAll.Where(IsStrict)]);
  }

  /// <summary>Whether two expressions of a function may change places: what each evaluates, its calls' summaries included, does not interfere with the other.</summary>
  /// <param name="table">The summaries of the functions of the module.</param>
  /// <param name="first">The expression evaluated first.</param>
  /// <param name="second">The expression evaluated second.</param>
  /// <returns><see langword="true"/> when the order may be swapped.</returns>
  public static bool MayReorder(IrEffectTable table, IrExpr first, IrExpr second) {
    ArgumentNullException.ThrowIfNull(table);
    ArgumentNullException.ThrowIfNull(first);
    ArgumentNullException.ThrowIfNull(second);
    return !Interferes(table.Expression(first), table.Expression(second));
  }

  private static bool IsUnknownMemory(IrPlace place) => place.Kind == IrPlaceKind.Memory && place.Name == Any;

  /// <summary>Whether the code may end the run where it stands: by a trap, or by running forever.</summary>
  private static bool Stops(IrEffects effects) => effects.MayTrap || effects.MayNotTerminate;

  /// <summary>Whether running the code or not can be told from outside: it writes, traps or makes a strict access.</summary>
  private static bool Observable(IrEffects effects) => effects.WritesAnything || effects.MayTrap || IsStrict(effects);

  private static bool Everything(IrEffects effects) => effects.ReadsEverything || effects.WritesEverything;

  private static IReadOnlyList<IrPlace> Accessed(IrEffects effects) => [.. effects.Reads.Concat(effects.Writes)];

  /// <summary>Whether two sets of places, each possibly every place there is, share one.</summary>
  private static bool Overlap(bool firstEverything, IReadOnlyCollection<IrPlace> first, bool secondEverything, IReadOnlyCollection<IrPlace> second) {
    if (firstEverything)
      return secondEverything || second.Count > 0;

    if (secondEverything)
      return first.Count > 0;

    return first.Any(place => second.Any(other => Overlaps(place, other)));
  }

}
