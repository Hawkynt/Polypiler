using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Polypiler.Core.IR;

/// <summary>
/// The name of an operation of the tree IR as the evaluator registry knows it (#119): <c>Intrinsic.&lt;op&gt;</c>,
/// <c>Collection.&lt;op&gt;</c>, <c>Binary.&lt;op&gt;</c> and <c>Unary.&lt;op&gt;</c> for the nodes that carry an
/// operator, the name of the node without its <c>Ir</c> prefix for any other (<c>NewArray</c>, <c>Call</c>).
/// </summary>
/// <param name="Name">The name.</param>
public readonly record struct OpcodeId(string Name) {

  /// <summary>The operation a node of the tree performs.</summary>
  /// <param name="node">The node.</param>
  /// <returns>Its name.</returns>
  public static OpcodeId Of(IrExpr node) {
    ArgumentNullException.ThrowIfNull(node);
    return node switch {
      IrIntrinsic intrinsic => Of(intrinsic.Op),
      IrCollection collection => new($"Collection.{collection.Op}"),
      IrBinary binary => new($"Binary.{binary.Op}"),
      IrUnary unary => new($"Unary.{unary.Op}"),
      _ => new(node.GetType().Name.StartsWith("Ir", StringComparison.Ordinal) ? node.GetType().Name[2..] : node.GetType().Name)
    };
  }

  /// <summary>The operation of an intrinsic.</summary>
  /// <param name="op">The intrinsic.</param>
  /// <returns><c>Intrinsic.&lt;op&gt;</c>.</returns>
  public static OpcodeId Of(IrIntrinsicOp op) => new($"Intrinsic.{op}");

  /// <summary>The name.</summary>
  /// <returns><see cref="Name"/>.</returns>
  public override string ToString() => this.Name;

}

/// <summary>
/// The evaluation of one operation, supplied by the family that owns it or by a test (#119). The interpreter has
/// evaluated the operands, left to right, before it calls the evaluator.
/// </summary>
public interface IOpcodeEvaluator {

  /// <summary>Evaluates a node.</summary>
  /// <param name="context">The node, its operands and the run it is evaluated in.</param>
  /// <returns>The value, of the type of the node; <see langword="null"/> for a node of type <see cref="IrType.Void"/>.</returns>
  IrValue? Evaluate(in EvaluationContext context);

}

/// <summary>What an <see cref="IOpcodeEvaluator"/> sees of the node it evaluates and of the run (#119).</summary>
public readonly struct EvaluationContext {

  private readonly IrInterpreter.Probe _run;

  internal EvaluationContext(IrExpr node, ImmutableArray<IrValue> operands, IrInterpreter.Probe run) {
    this.Node = node;
    this.Operands = operands;
    this._run = run;
  }

  /// <summary>The node evaluated.</summary>
  public IrExpr Node { get; }

  /// <summary>The operation of the node.</summary>
  public OpcodeId Opcode => OpcodeId.Of(this.Node);

  /// <summary>The values of the operands, in the order <see cref="IrTree.Operands"/> lists them.</summary>
  public ImmutableArray<IrValue> Operands { get; }

  /// <summary>The value of one operand.</summary>
  /// <param name="index">Its position.</param>
  /// <returns>The value.</returns>
  public IrValue Operand(int index) => this.Operands[index];

  /// <summary>The options of the run, unchanged.</summary>
  public InterpreterOptions Options => this._run.Options;

  /// <summary>The byte order of plain multi-byte loads and stores.</summary>
  public ByteOrder ByteOrder => this.Options.ByteOrder;

  /// <summary>The memory model of the run: the one of the options, or the flat model.</summary>
  public IMemoryModel Memory => this.Options.Memory ?? FlatMemoryModel.Instance;

  /// <summary>The schedule of the run: the one of the options, or <see cref="Schedules.Ascending"/>.</summary>
  public ISchedule Schedule => this.Options.Schedule ?? Schedules.Ascending;

  /// <summary>Appends an event to the trace of the run.</summary>
  /// <param name="effect">The event.</param>
  public void Emit(EffectEvent effect) => this._run.Emit(effect);

  /// <summary>Counts an allocation the operation makes, as growing a region does.</summary>
  public void Allocated() => this._run.Allocated();

  /// <summary>Traps: the operation raises the exception of the trap, which the program may catch.</summary>
  /// <param name="trap">What went wrong.</param>
  /// <exception cref="IrTrapException">Always.</exception>
  [DoesNotReturn]
  public void Trap(IrTrap trap) => throw this._run.Trapped(trap, this.Node);

  /// <summary>
  /// Reports that the operation violated an <c>[undefined]</c> contract (#122): the run ends
  /// <see cref="ExecutionOutcome.UndefinedBehaviourReached"/> at this node, with no value and no trap.
  /// </summary>
  /// <param name="reason">What was violated.</param>
  /// <exception cref="IrUndefinedBehaviourException">Always.</exception>
  [DoesNotReturn]
  public void Undefined(string reason) => throw new IrUndefinedBehaviourException(this.Node, reason);

  /// <summary>
  /// Checks an access of <paramref name="count"/> elements at <paramref name="offset"/> of a region of
  /// <paramref name="length"/> elements against <see cref="InterpreterOptions.Bounds"/>. Within the region it passes.
  /// Outside it, under <see cref="BoundsMode.CheckAll"/> the run aborts with <c>PLY0142</c>; under
  /// <see cref="BoundsMode.Contract"/> a checked access traps <see cref="IrTrap.IndexOutOfRange"/> and one whose contract
  /// promises it in bounds is not checked: the method returns <see langword="false"/> and the evaluator goes on as
  /// the contract says.
  /// </summary>
  /// <param name="region">The name of the region, for the diagnostic.</param>
  /// <param name="length">The elements of the region.</param>
  /// <param name="offset">The first element accessed.</param>
  /// <param name="count">The elements accessed.</param>
  /// <param name="promised">Whether the contract of the access promises it in bounds (<c>[inbounds]</c>, <c>[unchecked]</c>).</param>
  /// <returns>Whether the access lies in the region.</returns>
  public bool Access(string region, long length, long offset, long count, bool promised) {
    if (offset >= 0 && count >= 0 && offset <= length - count)
      return true;

    if (this.Options.Bounds == BoundsMode.CheckAll)
      throw new IrInterpreter.Abort(Diagnostic.Create(InterpreterDiagnostics.OutOfRegion, null, count, offset, region, length), this.Node);

    if (!promised)
      this.Trap(IrTrap.IndexOutOfRange);

    return false;
  }

}

/// <summary>
/// The evaluators of a run (#119): registered ones by operation, and resolvers asked, in the order they were added,
/// for an operation none is registered for - the plug-in point through which a family such as the intrinsic tables of
/// #106 supplies evaluations without the interpreter naming its types. An evaluator found replaces the interpreter's
/// own evaluation of the node; an operation <see cref="Withhold">withheld</see> has no evaluation but one found here.
/// </summary>
public sealed class OpcodeEvaluatorRegistry {

  private readonly Dictionary<OpcodeId, IOpcodeEvaluator> _registered = [];
  private readonly List<Func<OpcodeId, IOpcodeEvaluator?>> _resolvers = [];
  private readonly HashSet<OpcodeId> _withheld = [];

  /// <summary>Whether the registry has nothing to say about any operation, so that the interpreter need not ask it.</summary>
  public bool IsEmpty => this._registered.Count == 0 && this._resolvers.Count == 0 && this._withheld.Count == 0;

  /// <summary>Registers the evaluator of an operation, replacing one registered before.</summary>
  /// <param name="opcode">The operation.</param>
  /// <param name="evaluator">Its evaluator.</param>
  /// <returns>This registry.</returns>
  public OpcodeEvaluatorRegistry Register(OpcodeId opcode, IOpcodeEvaluator evaluator) {
    ArgumentNullException.ThrowIfNull(evaluator);
    this._registered[opcode] = evaluator;
    return this;
  }

  /// <summary>Adds a resolver, asked for an operation that has no registered evaluator after the resolvers added before it.</summary>
  /// <param name="resolver">The resolver: the evaluator of an operation, or <see langword="null"/> when it has none.</param>
  /// <returns>This registry.</returns>
  public OpcodeEvaluatorRegistry AddResolver(Func<OpcodeId, IOpcodeEvaluator?> resolver) {
    ArgumentNullException.ThrowIfNull(resolver);
    this._resolvers.Add(resolver);
    return this;
  }

  /// <summary>
  /// Withholds the interpreter's own evaluation of an operation: only an evaluator of this registry evaluates it, and
  /// a run that reaches it without one aborts with <c>PLY0120</c> - so that a test of an evaluator cannot pass by
  /// the interpreter's own evaluation.
  /// </summary>
  /// <param name="opcode">The operation.</param>
  /// <returns>This registry.</returns>
  public OpcodeEvaluatorRegistry Withhold(OpcodeId opcode) {
    this._withheld.Add(opcode);
    return this;
  }

  /// <summary>Whether the interpreter's own evaluation of an operation is withheld.</summary>
  /// <param name="opcode">The operation.</param>
  /// <returns><see langword="true"/> when <see cref="Withhold"/> named it.</returns>
  public bool IsWithheld(OpcodeId opcode) => this._withheld.Contains(opcode);

  /// <summary>The evaluator of an operation: the registered one, else the first a resolver supplies, else none.</summary>
  /// <param name="opcode">The operation.</param>
  /// <returns>The evaluator, or <see langword="null"/>.</returns>
  public IOpcodeEvaluator? Resolve(OpcodeId opcode) {
    if (this._registered.TryGetValue(opcode, out var registered))
      return registered;

    foreach (var resolver in this._resolvers)
      if (resolver(opcode) is { } resolved)
        return resolved;

    return null;
  }

}
