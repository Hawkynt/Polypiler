using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>The first difference between two traces under a projection, or none.</summary>
/// <param name="Equal">Whether the traces agree on everything the projection observes.</param>
/// <param name="What">What differs: <c>outcome</c>, <c>register r0</c>, <c>flag c</c>, <c>memory 0x1000+2</c>, <c>event 3</c> or <c>cycles</c>.</param>
/// <param name="Expected">The expected side as text; <c>none</c> where it has nothing.</param>
/// <param name="Actual">The actual side as text; <c>none</c> where it has nothing, <c>undefined</c> for an undefined flag.</param>
/// <param name="EventIndex">The position in the compared event sequence when an event differs.</param>
public sealed record TraceDiff(bool Equal, string? What, string? Expected, string? Actual, int? EventIndex) {

  /// <summary>No difference.</summary>
  public static TraceDiff Same { get; } = new(true, null, null, null, null);

  /// <summary>A difference.</summary>
  /// <param name="what">What differs.</param>
  /// <param name="expected">The expected side.</param>
  /// <param name="actual">The actual side.</param>
  /// <param name="eventIndex">The event's position, for an event.</param>
  /// <returns>The difference.</returns>
  public static TraceDiff Differs(string what, string expected, string actual, int? eventIndex = null) => new(false, what, expected, actual, eventIndex);

}

/// <summary>
/// Compares two traces on what a projection observes, stopping at the first difference, in this
/// order: the outcome; the observed registers; the observed flags (an expected undefined flag matches
/// anything, an actual undefined flag never matches a defined expectation); the final bytes of the
/// observed memory ranges; the events of the observed kinds in order (memory writes only in
/// <see cref="TraceMode.Raw"/> mode); the cycle count when it is observed. Nothing outside the
/// projection influences the verdict.
/// </summary>
public static class TraceComparer {

  /// <summary><c>PLY4031</c>: a trace differs from its reference. Arguments: the fixture, the vector, what, expected, actual.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Mismatch = new("PLY4031", DiagnosticSeverity.Error, ExitCode.SemanticMismatch, "'{0}' vector '{1}': {2} expected {3}, actual {4}", "machine.trace.mismatch");

  /// <summary><c>PLY4033</c>: the reference reports events of a kind the projection does not observe. Arguments: the fixture, the vector, the count, the kind.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnobservedEvents = new("PLY4033", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "'{0}' vector '{1}': the reference produced {2} event(s) of kind '{3}' but the projection does not observe that kind", "machine.trace.unobserved-events");

  private static readonly ImmutableArray<string> _checkedKinds = [TraceEvent.DeviceKind, TraceEvent.ExternalKind, TraceEvent.TrapKind];

  /// <summary>Compares two traces.</summary>
  /// <param name="expected">The reference trace.</param>
  /// <param name="actual">The trace under test.</param>
  /// <param name="projection">What is observed.</param>
  /// <returns>The first difference, or <see cref="TraceDiff.Same"/>.</returns>
  public static TraceDiff Compare(ObservableTrace expected, ObservableTrace actual, TraceProjection projection) {
    ArgumentNullException.ThrowIfNull(expected);
    ArgumentNullException.ThrowIfNull(actual);
    ArgumentNullException.ThrowIfNull(projection);
    if (expected.Outcome != actual.Outcome)
      return TraceDiff.Differs("outcome", Name(expected.Outcome), Name(actual.Outcome));

    foreach (var register in projection.Registers) {
      var want = Register(expected.Final, register);
      var have = Register(actual.Final, register);
      if (want != have)
        return TraceDiff.Differs($"register {register}", TraceEvent.Hex(want), TraceEvent.Hex(have));
    }

    foreach (var flag in projection.Flags) {
      if (Flag(expected.Final, flag) is not { } want)
        continue;
      var have = Flag(actual.Final, flag);
      if (have != want)
        return TraceDiff.Differs($"flag {flag}", FlagText(want), FlagText(have));
    }

    foreach (var span in projection.Memory)
      for (var offset = 0; offset < span.Length; ++offset) {
        var address = span.Address + (ulong)offset;
        var want = expected.Final.ByteAt(address);
        var have = actual.Final.ByteAt(address);
        if (want != have)
          return TraceDiff.Differs($"memory {TraceEvent.Hex(span.Address)}+{offset}", ByteText(want), ByteText(have));
      }

    var wantEvents = Observed(expected, projection);
    var haveEvents = Observed(actual, projection);
    for (var index = 0; index < Math.Max(wantEvents.Length, haveEvents.Length); ++index) {
      var want = index < wantEvents.Length ? wantEvents[index] : null;
      var have = index < haveEvents.Length ? haveEvents[index] : null;
      if (!Equals(want, have))
        return TraceDiff.Differs($"event {index}", want?.Text ?? "none", have?.Text ?? "none", index);
    }

    if (projection.Cycles && expected.Cycles is { } cycles && actual.Cycles != cycles)
      return TraceDiff.Differs("cycles", Count(cycles), Count(actual.Cycles));

    return TraceDiff.Same;
  }

  /// <summary>The events of a trace that a projection compares, in order.</summary>
  /// <param name="trace">The trace.</param>
  /// <param name="projection">The projection.</param>
  /// <returns>The events.</returns>
  public static ImmutableArray<TraceEvent> Observed(ObservableTrace trace, TraceProjection projection) {
    ArgumentNullException.ThrowIfNull(trace);
    ArgumentNullException.ThrowIfNull(projection);
    return [.. trace.Events.Where(item => projection.Compares(item.EventKind))];
  }

  /// <summary>A difference as <c>PLY4031</c>.</summary>
  /// <param name="fixture">The fixture.</param>
  /// <param name="vector">The vector.</param>
  /// <param name="diff">The difference.</param>
  /// <returns>The diagnostic.</returns>
  /// <exception cref="ArgumentException">The diff reports no difference.</exception>
  public static Diagnostic ToDiagnostic(string fixture, string vector, TraceDiff diff) {
    ArgumentNullException.ThrowIfNull(fixture);
    ArgumentNullException.ThrowIfNull(vector);
    ArgumentNullException.ThrowIfNull(diff);
    if (diff.Equal)
      throw new ArgumentException("equal traces have no difference to report", nameof(diff));
    return Diagnostic.Create(Mismatch, null, fixture, vector, diff.What!, diff.Expected!, diff.Actual!);
  }

  /// <summary>
  /// The <c>PLY4033</c> of every device, external or trap kind a reference trace has events of while
  /// the projection does not list the kind: such a reference shows behaviour the fixture would let pass
  /// unseen, which is a fixture error.
  /// </summary>
  /// <param name="fixture">The fixture.</param>
  /// <param name="vector">The vector.</param>
  /// <param name="reference">The reference trace.</param>
  /// <param name="projection">The projection.</param>
  /// <returns>The diagnostics; empty when the reference is consistent with the projection.</returns>
  public static ImmutableArray<Diagnostic> CheckReference(string fixture, string vector, ObservableTrace reference, TraceProjection projection) {
    ArgumentNullException.ThrowIfNull(fixture);
    ArgumentNullException.ThrowIfNull(vector);
    ArgumentNullException.ThrowIfNull(reference);
    ArgumentNullException.ThrowIfNull(projection);
    var problems = ImmutableArray.CreateBuilder<Diagnostic>();
    foreach (var kind in _checkedKinds.Where(kind => !projection.EventKinds.Contains(kind))) {
      var count = reference.Events.Count(item => item.EventKind == kind);
      if (count > 0)
        problems.Add(Diagnostic.Create(UnobservedEvents, null, fixture, vector, count, kind));
    }

    return problems.ToImmutable();
  }

  /// <summary>An outcome as the vector file spells it: <c>returned</c>, <c>trapped</c>, <c>aborted</c>, <c>budget-exceeded</c>.</summary>
  /// <param name="outcome">The outcome.</param>
  /// <returns>The spelling.</returns>
  public static string Name(TraceOutcome outcome) => outcome == TraceOutcome.BudgetExceeded ? "budget-exceeded" : outcome.ToString().ToLowerInvariant();

  private static ulong? Register(MachineSnapshot snapshot, string name) => snapshot.Registers.TryGetValue(name, out var value) ? value : null;

  private static bool? Flag(MachineSnapshot snapshot, string name) => snapshot.Flags.GetValueOrDefault(name);

  private static string FlagText(bool? value) => value switch { true => "1", false => "0", null => "undefined" };

  private static string ByteText(byte? value) => value is { } some ? "0x" + some.ToString("X2", CultureInfo.InvariantCulture) : "none";

  private static string Count(long? value) => value is { } some ? some.ToString(CultureInfo.InvariantCulture) : "none";

}
