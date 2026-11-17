using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Polypiler.Core.IR;

/// <summary>How a run of <see cref="IrInterpreter.Run"/> ended (#119).</summary>
public enum ExecutionOutcome {

  /// <summary>The function returned, with <see cref="ExecutionResult.Value"/> or, for <see cref="IrType.Void"/>, none.</summary>
  Returned,

  /// <summary>An exception left the function: a trap (<see cref="ExecutionResult.Trap"/>) or an object the program threw (<see cref="ExecutionResult.Thrown"/>).</summary>
  Trapped,

  /// <summary>The interpreter gave up with a diagnostic (<see cref="ExecutionResult.Diagnostic"/>): a budget, a missing model or evaluator, wrong arguments, a module that does not verify.</summary>
  Aborted,

  /// <summary>An operation violated an <c>[undefined]</c> contract (#122) at <see cref="ExecutionResult.At"/>: no value, no trap.</summary>
  UndefinedBehaviourReached

}

/// <summary>Where in the module something happened: the function, and the node when it is known.</summary>
/// <param name="Function">The name of the function whose frame was innermost.</param>
/// <param name="Node">The node, when an evaluator or a contract named it; <see langword="null"/> for a trap of the interpreter's own operations, which the function locates.</param>
public sealed record InstructionRef(string Function, IrExpr? Node);

/// <summary>
/// What a run of <see cref="IrInterpreter.Run"/> did (#119): how it ended, with what, where, the effects it had and the
/// probes of the run. A run is deterministic: the same module, function, arguments and options give an equal result.
/// </summary>
public sealed record ExecutionResult {

  /// <summary>How the run ended.</summary>
  public required ExecutionOutcome Outcome { get; init; }

  /// <summary>The value returned; <see langword="null"/> for a function returning nothing and for every other outcome.</summary>
  public IrValue? Value { get; init; }

  /// <summary>The trap that left the function; <see langword="null"/> unless the run <see cref="ExecutionOutcome.Trapped"/> on one.</summary>
  public IrTrap? Trap { get; init; }

  /// <summary>The class of the object that left the function, a trap's class too; <see langword="null"/> unless the run trapped.</summary>
  public IrClassType? Thrown { get; init; }

  /// <summary>The message of what ended the run: the exception's, the reason of a violated contract, the diagnostic's.</summary>
  public string? Message { get; init; }

  /// <summary>Where the run trapped, aborted on a budget or reached undefined behaviour; <see langword="null"/> when it returned.</summary>
  public InstructionRef? At { get; init; }

  /// <summary>The diagnostic of an <see cref="ExecutionOutcome.Aborted"/> run.</summary>
  public Diagnostic? Diagnostic { get; init; }

  /// <summary>The verifier's diagnostics of a module that does not verify (<c>PLY0124</c>); empty otherwise.</summary>
  public ImmutableArray<Diagnostic> Diagnostics { get; init; } = [];

  /// <summary>The effects of the run, in execution order.</summary>
  public ImmutableArray<EffectEvent> Trace { get; init; } = [];

  /// <summary>The steps the run took, at most <see cref="InterpreterOptions.MaxSteps"/>.</summary>
  public long Steps { get; init; }

  /// <summary>The allocating operations the run executed (<see cref="IrNewArray"/>, <see cref="IrNew"/>, the intrinsic <c>ArrayOf</c>, the constructions of collections) and the allocations evaluators counted.</summary>
  public long Allocations { get; init; }

  /// <summary>The most frames the run had at once: 1 for a function that calls nothing.</summary>
  public int MaxFrameDepth { get; init; }

  /// <summary>
  /// Under <see cref="InterpreterOptions.RecordValues"/> every value each local took, in order - each parameter bound,
  /// each declaration with a value, each assignment - keyed by the local itself (by reference); otherwise <see langword="null"/>.
  /// </summary>
  public IReadOnlyDictionary<IrLocal, ImmutableArray<IrValue>>? RecordedValues { get; init; }

}

/// <summary>
/// Raised where an operation violates an <c>[undefined]</c> contract (#122): <see cref="IrInterpreter.Run"/> ends with
/// <see cref="ExecutionOutcome.UndefinedBehaviourReached"/>, and no catch of the program stops it, as no catch stops a budget.
/// </summary>
public sealed class IrUndefinedBehaviourException : Exception {

  /// <summary>Makes the exception.</summary>
  /// <param name="node">The node whose contract was violated, or <see langword="null"/> when it is not known.</param>
  /// <param name="reason">What was violated.</param>
  public IrUndefinedBehaviourException(IrExpr? node, string reason) : base(reason) => this.Node = node;

  /// <summary>The node whose contract was violated, or <see langword="null"/>.</summary>
  public IrExpr? Node { get; }

}

/// <summary>The diagnostics an <see cref="ExecutionOutcome.Aborted"/> run carries (#119), registered in <c>spec/diagnostics.yaml</c>.</summary>
public static class InterpreterDiagnostics {

  /// <summary><c>PLY0120</c>: an operation with no evaluation, or a local read before anything was assigned to it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoEvaluation = new("PLY0120", DiagnosticSeverity.Error, ExitCode.InternalError, "{0}", "ir.interpreter.evaluation");

  /// <summary><c>PLY0121</c>: the step budget or the call depth of the run exceeded.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Budget = new("PLY0121", DiagnosticSeverity.Error, ExitCode.BudgetExceeded, "{0}", "ir.interpreter.budget");

  /// <summary><c>PLY0122</c>: arguments that do not fit the parameters of the function run.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Arguments = new("PLY0122", DiagnosticSeverity.Error, ExitCode.UsageError, "'@{0}' expects ({1}), got ({2})", "ir.interpreter.arguments");

  /// <summary><c>PLY0123</c>: a foreign function the external model of the run does not model.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoModel = new("PLY0123", DiagnosticSeverity.Error, ExitCode.MissingDependency, "external '@{0}' has no model", "ir.interpreter.external");

  /// <summary><c>PLY0124</c>: a module the verifier refuses, which the interpreter does not run.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unverified = new("PLY0124", DiagnosticSeverity.Error, ExitCode.InternalError, "module does not verify: {0} diagnostics", "ir.interpreter.verified");

  /// <summary><c>PLY0142</c>: an access outside its region under <see cref="BoundsMode.CheckAll"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OutOfRegion = new("PLY0142", DiagnosticSeverity.Error, ExitCode.InternalError, "access of {0} elements at offset {1} exceeds region '{2}' of {3} elements", "ir.memory.bounds");

}
