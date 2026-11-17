using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Passes;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// The effect classes of PRD §17.2 as place kinds of the effect summary, and the rule that a reordering
/// proves non-interference (#114, ADR 0009). <see cref="IrEffectQueries.Interferes(IrEffects, IrEffects)"/>
/// is checked against a reference machine written here, which shares no code with it: it knows only
/// concrete cells, which cells a place stands for, that a strict cell records every access in its own
/// trace, and that a trap or an endless run ends the run. Two pieces of code may change places exactly
/// when no outcome of either makes the two orders differ.
/// </summary>
[TestFixture]
internal sealed class EffectInterferenceTests {

  // ---------------------------------------------------------------- the reference machine

  /// <summary>The cells of the machine: kind and name.</summary>
  private static readonly (IrPlaceKind Kind, string Name)[] _cells = [
    (IrPlaceKind.Local, "x"), (IrPlaceKind.Local, "y"), (IrPlaceKind.Global, "g"), (IrPlaceKind.Field, "f"), (IrPlaceKind.Elements, "i32"),
    (IrPlaceKind.Memory, "heap"), (IrPlaceKind.Memory, "stack0"), (IrPlaceKind.Volatile, "v1"), (IrPlaceKind.Volatile, "v2"), (IrPlaceKind.Mmio, "d020"),
    (IrPlaceKind.Device, "vic2"), (IrPlaceKind.Device, "sid"), (IrPlaceKind.External, "world"), (IrPlaceKind.Exception, "handled"),
    (IrPlaceKind.MachineRegister, "A"), (IrPlaceKind.MachineRegister, "X"), (IrPlaceKind.MachineFlags, "carry")
  ];

  /// <summary>The cells a place stands for: one world, one exception state; <c>*</c> every cell of its kind; unknown memory every cell of storage.</summary>
  private static IEnumerable<int> Cells(IrPlace place) {
    IrPlaceKind[] storage = [IrPlaceKind.Local, IrPlaceKind.Global, IrPlaceKind.Field, IrPlaceKind.Elements, IrPlaceKind.Memory, IrPlaceKind.Volatile, IrPlaceKind.Mmio];
    for (var cell = 0; cell < _cells.Length; ++cell) {
      var (kind, name) = _cells[cell];
      var whole = place.Kind is IrPlaceKind.External or IrPlaceKind.Exception || place.Name == "*";
      if (place.Kind == IrPlaceKind.Memory && place.Name == "*" ? storage.Contains(kind) : kind == place.Kind && (whole || name == place.Name))
        yield return cell;
    }
  }

  private static bool Strict(int cell) => _cells[cell].Kind is IrPlaceKind.Volatile or IrPlaceKind.Mmio or IrPlaceKind.Device or IrPlaceKind.External;

  /// <summary>How a piece of code ends in one run.</summary>
  private enum Outcome { Ends, Traps, Hangs }

  /// <summary>Runs pieces of code in an order, each with its outcome, and tells what can be seen of the run.</summary>
  private static string Run(IReadOnlyList<(int Id, IrEffects Effects)> order, IReadOnlyDictionary<int, Outcome> outcomes) {
    var versions = new int[_cells.Length];
    var traces = new List<string>[_cells.Length];
    for (var cell = 0; cell < traces.Length; ++cell)
      traces[cell] = [];

    var reads = new List<string>();
    string? stop = null;
    foreach (var (id, effects) in order) {
      var read = effects.ReadsEverything ? Enumerable.Range(0, _cells.Length) : effects.Reads.SelectMany(Cells);
      foreach (var cell in read.Distinct()) {
        if (Strict(cell))
          traces[cell].Add($"r{id}");

        reads.Add($"{id}:{cell}={versions[cell]}");
      }

      var written = effects.WritesEverything ? Enumerable.Range(0, _cells.Length) : effects.Writes.SelectMany(Cells);
      foreach (var cell in written.Distinct()) {
        if (Strict(cell))
          traces[cell].Add($"w{id}");

        versions[cell] = id;
      }

      if (outcomes[id] != Outcome.Ends) {
        stop = outcomes[id] == Outcome.Traps ? $"trap {id}" : "hang";
        break;
      }
    }

    // What a run that stopped read is lost with it; the state, the traces of the strict cells and the stop remain.
    // What each piece read is compared as a set: the order in which two pieces read is no observation of its own.
    return $"{string.Join(",", versions)}|{string.Join("|", traces.Select(trace => string.Join(" ", trace)))}|{stop ?? string.Join(";", reads.Order(StringComparer.Ordinal))}";
  }

  private static IEnumerable<Outcome> OutcomesOf(IrEffects effects) {
    yield return Outcome.Ends;
    if (effects.MayTrap)
      yield return Outcome.Traps;

    if (effects.MayNotTerminate)
      yield return Outcome.Hangs;
  }

  /// <summary>Every assignment of an outcome to each piece of code.</summary>
  private static IEnumerable<Dictionary<int, Outcome>> Inputs(IReadOnlyList<(int Id, IrEffects Effects)> pieces) {
    IEnumerable<Dictionary<int, Outcome>> inputs = [[]];
    foreach (var (id, effects) in pieces)
      inputs = inputs.SelectMany(input => OutcomesOf(effects).Select(outcome => new Dictionary<int, Outcome>(input) { [id] = outcome })).ToList();

    return inputs;
  }

  /// <summary>Whether two orders of the same pieces can be told apart in some run.</summary>
  private static bool Differ(IReadOnlyList<(int Id, IrEffects Effects)> one, IReadOnlyList<(int Id, IrEffects Effects)> other)
    => Inputs(one).Any(input => Run(one, input) != Run(other, input));

  // ---------------------------------------------------------------- the catalogue

  private static IrPlace P(IrPlaceKind kind, string name) => new(kind, name);

  private static IrEffects Reads(params IrPlace[] places) => IrEffects.None with { Reads = places.ToHashSet() };

  private static IrEffects Writes(params IrPlace[] places) => IrEffects.None with { Writes = places.ToHashSet() };

  /// <summary>One summary per class of effect and per kind of place: reads and writes, named, wildcard and unknown places, traps, endless runs and the worst case.</summary>
  private static readonly (string Name, IrEffects Effects)[] _catalogue = [
    ("pure", IrEffects.None),
    ("read x", Reads(P(IrPlaceKind.Local, "x"))),
    ("write x", Writes(P(IrPlaceKind.Local, "x"))),
    ("read y", Reads(P(IrPlaceKind.Local, "y"))),
    ("write g", Writes(P(IrPlaceKind.Global, "g"))),
    ("read f", Reads(P(IrPlaceKind.Field, "f"))),
    ("write i32[]", Writes(P(IrPlaceKind.Elements, "i32"))),
    ("read i32[]", Reads(P(IrPlaceKind.Elements, "i32"))),
    ("read heap", Reads(P(IrPlaceKind.Memory, "heap"))),
    ("write heap", Writes(P(IrPlaceKind.Memory, "heap"))),
    ("write stack0", Writes(P(IrPlaceKind.Memory, "stack0"))),
    ("write unknown memory", Writes(IrEffectQueries.UnknownMemory)),
    ("read unknown memory", Reads(IrEffectQueries.UnknownMemory)),
    ("read v1", Reads(P(IrPlaceKind.Volatile, "v1"))),
    ("write v1", Writes(P(IrPlaceKind.Volatile, "v1"))),
    ("read v2", Reads(P(IrPlaceKind.Volatile, "v2"))),
    ("read any volatile", Reads(P(IrPlaceKind.Volatile, "*"))),
    ("poke d020", Writes(P(IrPlaceKind.Mmio, "d020"))),
    ("peek d020", Reads(P(IrPlaceKind.Mmio, "d020"))),
    ("write vic2", Writes(P(IrPlaceKind.Device, "vic2"))),
    ("read vic2", Reads(P(IrPlaceKind.Device, "vic2"))),
    ("write sid", Writes(P(IrPlaceKind.Device, "sid"))),
    ("write any device", Writes(P(IrPlaceKind.Device, "*"))),
    ("external read", Reads(P(IrPlaceKind.External, "getenv"))),
    ("external write", Writes(P(IrPlaceKind.External, "puts"))),
    ("catch", Writes(P(IrPlaceKind.Exception, "handled"))),
    ("rethrow", Reads(P(IrPlaceKind.Exception, "handled")) with { MayTrap = true }),
    ("set A", Writes(P(IrPlaceKind.MachineRegister, "A"))),
    ("use A", Reads(P(IrPlaceKind.MachineRegister, "A"))),
    ("use X", Reads(P(IrPlaceKind.MachineRegister, "X"))),
    ("set carry", Writes(P(IrPlaceKind.MachineFlags, "carry"))),
    ("use carry", Reads(P(IrPlaceKind.MachineFlags, "carry"))),
    ("trap", IrEffects.None with { MayTrap = true }),
    ("trapping read x", Reads(P(IrPlaceKind.Local, "x")) with { MayTrap = true }),
    ("endless", IrEffects.None with { MayNotTerminate = true }),
    ("reads everything", IrEffects.None with { ReadsEverything = true }),
    ("unknown call", IrEffects.Unknown)
  ];

  private static IEnumerable<TestCaseData> Pairs()
    => from first in _catalogue
       from second in _catalogue
       select new TestCaseData(first.Effects, second.Effects).SetName($"Interferes({first.Name}, {second.Name})");

  /// <summary>
  /// Measurement "Interferes against the oracle": over every pair of the catalogue, the two orders can be
  /// told apart exactly when <see cref="IrEffectQueries.Interferes(IrEffects, IrEffects)"/> says so - no
  /// unsafe swap allowed, no safe swap forbidden.
  /// </summary>
  [TestCaseSource(nameof(Pairs))]
  public void Interferes_ExactlyWhenTheOrdersDiffer(IrEffects first, IrEffects second) {
    var differ = Differ([(1, first), (2, second)], [(2, second), (1, first)]);
    Assert.That(IrEffectQueries.Interferes(first, second), Is.EqualTo(differ));
    Assert.That(IrEffectQueries.Interferes(second, first), Is.EqualTo(differ), "the relation is symmetric");
  }

  /// <summary>
  /// The permutation oracle: every order of a block that swaps only neighbours that do not interfere
  /// gives the run of the block as written, for every outcome of every piece.
  /// </summary>
  [TestCase("write x", "write vic2", "read x", "read heap", "trap")]
  [TestCase("poke d020", "read i32[]", "write heap", "read v1", "read v1")]
  [TestCase("unknown call", "pure", "read y", "set A", "use carry")]
  [TestCase("write unknown memory", "write vic2", "read f", "endless", "external read")]
  [TestCase("catch", "rethrow", "use X", "set carry", "write sid")]
  public void ReachableOrders_RunAsWritten(params string[] names) {
    var block = names.Select((name, index) => (Id: index + 1, Effects: _catalogue.Single(entry => entry.Name == name).Effects)).ToList();
    var seen = new HashSet<string> { string.Join(",", block.Select(piece => piece.Id)) };
    var pending = new Queue<List<(int Id, IrEffects Effects)>>([block]);
    var reached = 0;
    while (pending.TryDequeue(out var order)) {
      ++reached;
      Assert.That(Differ(block, order), Is.False, string.Join(", ", order.Select(piece => names[piece.Id - 1])));
      for (var index = 0; index + 1 < order.Count; ++index) {
        if (IrEffectQueries.Interferes(order[index].Effects, order[index + 1].Effects))
          continue;

        var swapped = new List<(int Id, IrEffects Effects)>(order);
        (swapped[index], swapped[index + 1]) = (swapped[index + 1], swapped[index]);
        if (seen.Add(string.Join(",", swapped.Select(piece => piece.Id))))
          pending.Enqueue(swapped);
      }
    }

    Assert.That(reached, Is.GreaterThan(1), "some reordering is allowed");
  }

  // ---------------------------------------------------------------- the classes and the places

  [Test]
  public void Overlaps_ByKindNameWildcardAndUnknownMemory() {
    Assert.That(IrEffectQueries.Overlaps(P(IrPlaceKind.Memory, "heap"), P(IrPlaceKind.Memory, "stack0")), Is.False, "same class, other region");
    Assert.That(IrEffectQueries.Overlaps(P(IrPlaceKind.Device, "*"), P(IrPlaceKind.Device, "vic2")), Is.True, "a wildcard region");
    Assert.That(IrEffectQueries.Overlaps(P(IrPlaceKind.Device, "vic2"), P(IrPlaceKind.Device, "*")), Is.True);
    Assert.That(IrEffectQueries.Overlaps(P(IrPlaceKind.External, "puts"), P(IrPlaceKind.External, "getenv")), Is.True, "one world outside");
    Assert.That(IrEffectQueries.Overlaps(P(IrPlaceKind.Exception, "a"), P(IrPlaceKind.Exception, "b")), Is.True);
    Assert.That(IrEffectQueries.Overlaps(P(IrPlaceKind.Global, "g"), P(IrPlaceKind.Local, "g")), Is.False, "kinds tell places apart");
    foreach (var kind in Enum.GetValues<IrPlaceKind>()) {
      var touched = kind is IrPlaceKind.Local or IrPlaceKind.Global or IrPlaceKind.Field or IrPlaceKind.Elements or IrPlaceKind.Memory or IrPlaceKind.Volatile or IrPlaceKind.Mmio;
      Assert.That(IrEffectQueries.Overlaps(IrEffectQueries.UnknownMemory, P(kind, "n")), Is.EqualTo(touched), $"unknown memory and {kind}");
      Assert.That(IrEffectQueries.Overlaps(P(kind, "n"), IrEffectQueries.UnknownMemory), Is.EqualTo(touched), $"{kind} and unknown memory");
    }
  }

  [Test]
  public void Strictness_OfPlacesAndSummaries() {
    Assert.That(Enum.GetValues<IrPlaceKind>().Where(kind => IrEffectQueries.IsStrict(P(kind, "n"))),
      Is.EqualTo(new[] { IrPlaceKind.Volatile, IrPlaceKind.Mmio, IrPlaceKind.Device, IrPlaceKind.External }));
    Assert.That(IrEffectQueries.IsStrict(IrEffectQueries.UnknownMemory), Is.True, "unknown memory may be a device register");
    Assert.That(IrEffectQueries.IsStrict(Reads(P(IrPlaceKind.Volatile, "v1"))), Is.True);
    Assert.That(IrEffectQueries.IsStrict(Writes(P(IrPlaceKind.Global, "g"))), Is.False);
    Assert.That(IrEffectQueries.IsStrict(IrEffects.None with { ReadsEverything = true }), Is.True);
    Assert.That(IrEffectQueries.IsStrict(IrEffects.None with { WritesEverything = true }), Is.True);
    Assert.That(IrEffectQueries.IsStrict(IrEffects.None), Is.False);
  }

  [Test]
  public void Queries_RefuseNull() {
    var place = P(IrPlaceKind.Global, "g");
    Assert.That(() => IrEffectQueries.Overlaps(null!, place), Throws.ArgumentNullException);
    Assert.That(() => IrEffectQueries.Overlaps(place, null!), Throws.ArgumentNullException);
    Assert.That(() => IrEffectQueries.IsStrict((IrPlace)null!), Throws.ArgumentNullException);
    Assert.That(() => IrEffectQueries.IsStrict((IrEffects)null!), Throws.ArgumentNullException);
    Assert.That(() => IrEffectQueries.Interferes(null!, IrEffects.None), Throws.ArgumentNullException);
    Assert.That(() => IrEffectQueries.Interferes(IrEffects.None, null!), Throws.ArgumentNullException);
    var table = IrEffects.Of(new IrModule("M", []));
    Assert.That(() => IrEffectQueries.MayReorder(null!, new IrConst(IrType.I32, 0), new IrConst(IrType.I32, 0)), Throws.ArgumentNullException);
    Assert.That(() => IrEffectQueries.MayReorder(table, null!, new IrConst(IrType.I32, 0)), Throws.ArgumentNullException);
    Assert.That(() => IrEffectQueries.MayReorder(table, new IrConst(IrType.I32, 0), null!), Throws.ArgumentNullException);
  }

  /// <summary>A reordering of two expressions of a function: their nodes and the summaries of the functions they call.</summary>
  [Test]
  public void MayReorder_ExpressionsByTheirSummaries() {
    var g = new IrGlobal("g", IrType.I32, null);
    var set = new IrFunction("Set", false, IrType.I32, [], [], new IrBlock([new IrGlobalAssign(g, new IrConst(IrType.I32, 1)), new IrReturn(new IrConst(IrType.I32, 1))]));
    var module = new IrModule("M", [set]) { Globals = [g] };
    var table = IrEffects.Of(module);
    var x = new IrLocal("x", IrType.I32);
    var call = new IrCall("Set", IrType.I32, []);
    Assert.That(IrEffectQueries.MayReorder(table, new IrLocalRef(x), new IrGlobalRef(g)), Is.True, "two reads");
    Assert.That(IrEffectQueries.MayReorder(table, call, new IrGlobalRef(g)), Is.False, "the call writes what the other reads");
    Assert.That(IrEffectQueries.MayReorder(table, call, new IrBinary(IrBinaryOp.Add, new IrLocalRef(x), new IrConst(IrType.I32, 2))), Is.True, "independent arithmetic");
    Assert.That(IrEffectQueries.MayReorder(table, new IrBinary(IrBinaryOp.Divide, new IrLocalRef(x), new IrLocalRef(x)), call), Is.False, "a trap before a write");
    Assert.That(IrEffectQueries.MayReorder(table, new IrCall("Missing", IrType.I32, []), new IrLocalRef(x)), Is.False, "an unknown function may write the variable");
  }

  // ---------------------------------------------------------------- the negative tests of PRD §17.5

  /// <summary>§17.5: a store into an unknown pointer is never assumed to miss an array, and stays where it is.</summary>
  [Test]
  public void StoreThroughAnUnknownPointer_IsNoPrivateStore() {
    var store = Writes(IrEffectQueries.UnknownMemory);
    Assert.That(IrEffectQueries.Interferes(store, Reads(P(IrPlaceKind.Elements, "i32"))), Is.True);
    Assert.That(IrEffectQueries.Interferes(store, Reads(P(IrPlaceKind.Local, "x"))), Is.True);
    Assert.That(IrEffectQueries.IsStrict(store), Is.True, "it may be a device register, so it is never dropped");
    Assert.That(IrEffectQueries.Interferes(store, Writes(P(IrPlaceKind.Device, "vic2"))), Is.False, "a device port is no memory");
  }

  /// <summary>§17.5: a POKE to a memory-mapped register is not folded away because later ordinary reads miss its address.</summary>
  [Test]
  public void PokeToMmio_StaysAndKeepsItsOrder() {
    var poke = Writes(P(IrPlaceKind.Mmio, "d020"));
    Assert.That(IrEffectQueries.IsStrict(poke), Is.True);
    Assert.That(IrEffectQueries.Interferes(poke, poke), Is.True, "two pokes keep their order");
    Assert.That(IrEffectQueries.Interferes(poke, Reads(IrEffectQueries.UnknownMemory)), Is.True, "a read through any pointer may see it");
    Assert.That(IrEffectQueries.Interferes(poke, Reads(P(IrPlaceKind.Elements, "u8"))), Is.False, "an array of the program is another place");
  }

  /// <summary>§17.5: an unknown runtime call invalidates what is known of arrays, but not independent arithmetic.</summary>
  [Test]
  public void UnknownCall_BlocksArrayFactsButNotIndependentFolding() {
    Assert.That(IrEffectQueries.Interferes(IrEffects.Unknown, Reads(P(IrPlaceKind.Elements, "i32"))), Is.True);
    Assert.That(IrEffectQueries.Interferes(IrEffects.Unknown, IrEffects.None), Is.False);
  }

  /// <summary>§17.5: repeated volatile reads and writes stay separately observable and in order.</summary>
  [Test]
  public void VolatileReadsAndWrites_StaySeparateAndInOrder() {
    var read = Reads(P(IrPlaceKind.Volatile, "v1"));
    var write = Writes(P(IrPlaceKind.Volatile, "v1"));
    Assert.That(IrEffectQueries.Interferes(read, read), Is.True, "two reads of one volatile place");
    Assert.That(IrEffectQueries.Interferes(write, write), Is.True);
    Assert.That(IrEffectQueries.Interferes(read, Reads(P(IrPlaceKind.Volatile, "v2"))), Is.False, "two volatile places told apart");
    Assert.That(IrEffectQueries.IsStrict(read), Is.True, "never merged into one read");
  }

  /// <summary>§17.5: writes a raster interrupt can see are never fused, whatever the final state.</summary>
  [Test]
  public void DeviceWrites_AreNeverFused() {
    var write = Writes(P(IrPlaceKind.Device, "vic2"));
    Assert.That(IrEffectQueries.Interferes(write, write), Is.True);
    Assert.That(IrEffectQueries.Interferes(write, Writes(P(IrPlaceKind.Device, "*"))), Is.True);
    Assert.That(IrEffectQueries.Interferes(write, Writes(P(IrPlaceKind.Device, "sid"))), Is.False);
    Assert.That(IrEffectQueries.IsStrict(write), Is.True);
  }

}
