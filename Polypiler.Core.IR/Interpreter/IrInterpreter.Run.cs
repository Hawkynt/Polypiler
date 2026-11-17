using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>The bytes of stack a run reserves besides those of its frames.</summary>
  private const int BaseStack = 16 << 20;

  /// <summary>The bytes of stack a run reserves per frame it may have: generous, since one call of the IR nests several of the interpreter.</summary>
  private const int StackPerFrame = 64 << 10;

  /// <summary>
  /// Runs a function of a module on concrete arguments (#119) and reports how it ended, never by an exception: the
  /// value it returned, the trap or the object it threw, or the diagnostic the interpreter gave up with; with the
  /// effects it had, the steps it took, the allocations it made and the most frames it had at once. The module is
  /// verified first, and one that does not verify is not run (<c>PLY0124</c>). The globals are initialised for the
  /// run, in the run: their initialisers count against its budgets too. The run takes a thread of its own, whose
  /// stack has room for <see cref="InterpreterOptions.MaxCallDepth"/> frames.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="function">The name of the function to run.</param>
  /// <param name="arguments">One value per parameter, each of the parameter's type exactly.</param>
  /// <param name="options">The options; <see langword="null"/> for <see cref="InterpreterOptions.Default"/>.</param>
  /// <returns>What the run did.</returns>
  /// <exception cref="ArgumentNullException">The module or the name is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">The module has no function of the name.</exception>
  public static ExecutionResult Run(IrModule module, string function, ImmutableArray<IrValue> arguments, InterpreterOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(function);
    options ??= InterpreterOptions.Default;
    var verified = IrVerifier.Verify(module);
    if (verified.Count > 0)
      return new() {
        Outcome = ExecutionOutcome.Aborted,
        Diagnostic = Diagnostic.Create(InterpreterDiagnostics.Unverified, null, verified.Count),
        Message = Diagnostic.Create(InterpreterDiagnostics.Unverified, null, verified.Count).Message,
        Diagnostics = [.. verified]
      };

    var program = _programs.GetValue(module, Index);
    if (!program.Functions.TryGetValue(function, out var target))
      throw new ArgumentException($"The module '{module.Name}' has no function '{function}'.", nameof(function));

    if (Bound(target, arguments.IsDefault ? [] : arguments) is not { } values) {
      var mismatch = Diagnostic.Create(InterpreterDiagnostics.Arguments, null, function,
        string.Join(", ", target.Parameters.Select(parameter => parameter.Type)),
        string.Join(", ", (arguments.IsDefault ? [] : arguments).Select(argument => argument.Type)));
      return new() { Outcome = ExecutionOutcome.Aborted, Diagnostic = mismatch, Message = mismatch.Message };
    }

    ExecutionResult? result = null;
    Exception? failure = null;
    var thread = new Thread(() => {
      try {
        result = Execute(module, program, target, values, options);
      } catch (Exception exception) {
        failure = exception;
      }
    }, BaseStack + (int)Math.Min((long)options.MaxCallDepth * StackPerFrame, int.MaxValue - BaseStack));
    thread.Start();
    thread.Join();
    if (failure is not null)
      ExceptionDispatchInfo.Throw(failure);

    return result!;
  }

  /// <summary>The arguments as the interpreter carries them, or <see langword="null"/> when they do not fit the parameters.</summary>
  private static object[]? Bound(IrFunction target, ImmutableArray<IrValue> arguments) {
    if (arguments.Length != target.Parameters.Count)
      return null;

    var values = new object[arguments.Length];
    for (var index = 0; index < values.Length; ++index)
      if (FromValue(target.Parameters[index].Type, arguments[index]) is { } value)
        values[index] = value;
      else
        return null;

    return values;
  }

  private static ExecutionResult Execute(IrModule module, Program program, IrFunction target, object[] values, InterpreterOptions options) {
    var probe = new Probe(options, module);
    var globals = new Dictionary<IrGlobal, object>(ReferenceEqualityComparer.Instance);
    var machine = new Machine(program, globals) { Probe = probe };
    machine.Limit(options.MaxSteps);
    machine.Nest(options.MaxCallDepth);
    _recording = options.RecordValues ? probe : null;
    ExecutionResult ended;
    try {
      Initialise(machine, program, module, globals);
      var value = machine.Invoke(target, values);
      ended = new() { Outcome = ExecutionOutcome.Returned, Value = target.ReturnType == IrType.Void ? null : ToValue(target.ReturnType, value) };
    } catch (IrTrapException trap) when (probe.Locate(machine.Depth, trap)) {
      ended = new() { Outcome = ExecutionOutcome.Trapped, Trap = trap.Trap, Thrown = trap.Class, Message = trap.Message, At = probe.Where(trap) };
    } catch (IrException thrown) when (probe.Locate(machine.Depth, thrown)) {
      ended = new() { Outcome = ExecutionOutcome.Trapped, Thrown = thrown.Class, Message = thrown.Message, At = probe.Where(thrown) };
    } catch (IrBudgetException budget) when (probe.Locate(machine.Depth, budget)) {
      var at = probe.Where(budget);
      var diagnostic = machine.OutOfSteps
        ? Diagnostic.Create(InterpreterDiagnostics.Budget, null, $"step budget of {options.MaxSteps} exceeded in '@{at?.Function}'")
        : Diagnostic.Create(InterpreterDiagnostics.Budget, null, $"call depth {options.MaxCallDepth} exceeded");
      ended = new() { Outcome = ExecutionOutcome.Aborted, Diagnostic = diagnostic, Message = diagnostic.Message, At = at };
    } catch (Abort abort) when (probe.Locate(machine.Depth, abort)) {
      ended = new() { Outcome = ExecutionOutcome.Aborted, Diagnostic = abort.Diagnostic, Message = abort.Diagnostic.Message, At = probe.Where(abort) };
    } catch (IrUndefinedBehaviourException undefined) when (probe.Locate(machine.Depth, undefined)) {
      ended = new() { Outcome = ExecutionOutcome.UndefinedBehaviourReached, Message = undefined.Message, At = probe.Where(undefined) };
    } finally {
      _recording = null;
    }

    return ended with {
      Trace = [.. probe.Trace],
      Steps = machine.StepsTaken(options.MaxSteps),
      Allocations = probe.Allocations,
      MaxFrameDepth = probe.Deepest,
      RecordedValues = probe.Recorded()
    };
  }

  /// <summary>
  /// Initialises the globals of a module in a machine, in order, and runs the initializer of the module after them, as
  /// a module is loaded (<see cref="Instantiate"/>, <see cref="Run"/>).
  /// </summary>
  private static void Initialise(Machine machine, Program program, IrModule module, Dictionary<IrGlobal, object> globals) {
    foreach (var global in module.Globals)
      globals[global] = global.Initializer is { } initializer ? machine.Initial(initializer)
        : global.Type.IsObject || global.Type.IsCollection ? NullReference.Value
        : Constant(global.Type, 0);

    if (module.Initializer is { } start)
      machine.Invoke(program.Functions[start], []);
  }

  /// <summary>A value as the interpreter carries it, as an <see cref="IrValue"/> of a type.</summary>
  private static IrValue ToValue(IrType type, object? value) {
    if (!IrValue.IsScalarType(type))
      return IrValue.Referring(type, ToHost(type, value));

    return value switch {
      bool truth => IrValue.FromHost(type, truth),
      float single => IrValue.FromHost(type, single),
      double number => IrValue.FromHost(type, number),
      BigInteger big => IrValue.FromHost(type, big),
      _ => IrValue.Integer(type, (long)value!)
    };
  }

  /// <summary>An <see cref="IrValue"/> as the interpreter carries a value of a type, or <see langword="null"/> when it is none of that type.</summary>
  private static object? FromValue(IrType type, IrValue value)
    => value.Type != type ? null
      : value.IsScalar ? Internal(value.ToHost()!)
      : Accepted(type, value.Reference);

  /// <summary>The run whose values the frames of this thread record, or <see langword="null"/>.</summary>
  [ThreadStatic]
  private static Probe? _recording;

  /// <summary>Records the value a local takes, when the run on this thread records values.</summary>
  private static void Recorded(IrLocal local, object? value) {
    if (_recording is { } probe && value is not null)
      probe.Record(local, ToValue(local.Type, value));
  }

  /// <summary>An abort of a run with a diagnostic, which no catch of the program stops.</summary>
  /// <param name="diagnostic">Why the run was given up.</param>
  /// <param name="node">The node it was given up at, or <see langword="null"/>.</param>
  internal sealed class Abort(Diagnostic diagnostic, IrExpr? node) : Exception(diagnostic.Message) {

    /// <summary>Why the run was given up.</summary>
    public Diagnostic Diagnostic { get; } = diagnostic;

    /// <summary>The node it was given up at, or <see langword="null"/>.</summary>
    public IrExpr? Node { get; } = node;

  }

  /// <summary>What a run observes of itself: the trace, the allocations, the frames, the recorded values.</summary>
  internal sealed class Probe(InterpreterOptions options, IrModule module) : IEffectSink {

    private readonly Dictionary<string, IrForeignFunction> _foreign = module.ForeignFunctions.ToDictionary(declaration => declaration.Name, StringComparer.Ordinal);

    /// <summary>The function of each frame in progress, the outermost first; slots beyond the depth are stale.</summary>
    private readonly List<IrFunction> _frames = [];

    private readonly Dictionary<IrLocal, ImmutableArray<IrValue>.Builder> _recorded = new(ReferenceEqualityComparer.Instance);

    /// <summary>Where each exception that ended or may end the run was raised, by the exception.</summary>
    private readonly Dictionary<Exception, InstructionRef?> _sites = new(ReferenceEqualityComparer.Instance);

    /// <summary>The node each trap an evaluator raised was raised at.</summary>
    private readonly Dictionary<Exception, IrExpr> _nodes = new(ReferenceEqualityComparer.Instance);

    public InterpreterOptions Options { get; } = options;

    /// <summary>The registry of the run, or <see langword="null"/> when it has nothing to say.</summary>
    public OpcodeEvaluatorRegistry? Evaluators { get; } = options.Evaluators is { IsEmpty: false } registry ? registry : null;

    public List<EffectEvent> Trace { get; } = [];

    public long Allocations { get; private set; }

    public int Deepest { get; private set; }

    public void Emit(EffectEvent effect) {
      ArgumentNullException.ThrowIfNull(effect);
      this.Trace.Add(effect);
    }

    public void Allocated() => ++this.Allocations;

    /// <summary>A trap an evaluator raises at a node, which the result names.</summary>
    public IrTrapException Trapped(IrTrap trap, IrExpr node) {
      var exception = new IrTrapException(trap);
      this._nodes[exception] = node;
      return exception;
    }

    public IrForeignFunction Foreign(string name) => this._foreign[name];

    /// <summary>A frame entered at a depth, counted from 1.</summary>
    public void Enter(int depth, IrFunction function) {
      if (depth > this._frames.Count)
        this._frames.Add(function);
      else
        this._frames[depth - 1] = function;

      this.Deepest = Math.Max(this.Deepest, depth);
    }

    /// <summary>
    /// Notes where an exception was raised, in the exception filter that catches it: the first pass of .NET runs it
    /// before any frame between the throw and the catch has unwound, so the innermost frame is still the depth of the
    /// machine. Always <see langword="true"/>.
    /// </summary>
    public bool Locate(int depth, Exception exception) {
      depth = Math.Min(depth, this._frames.Count);
      var node = exception switch {
        IrUndefinedBehaviourException undefined => undefined.Node,
        Abort abort => abort.Node,
        _ => this._nodes.GetValueOrDefault(exception)
      };
      this._sites[exception] = depth == 0 ? null : new InstructionRef(this._frames[depth - 1].Name, node);
      return true;
    }

    public InstructionRef? Where(Exception exception) => this._sites[exception];

    public void Record(IrLocal local, IrValue value) {
      if (!this._recorded.TryGetValue(local, out var values))
        this._recorded[local] = values = ImmutableArray.CreateBuilder<IrValue>();

      values.Add(value);
    }

    public IReadOnlyDictionary<IrLocal, ImmutableArray<IrValue>>? Recorded() {
      if (!this.Options.RecordValues)
        return null;

      var recorded = new Dictionary<IrLocal, ImmutableArray<IrValue>>(ReferenceEqualityComparer.Instance);
      foreach (var (local, values) in this._recorded)
        recorded[local] = values.ToImmutable();

      return recorded.AsReadOnly();
    }

  }

  private sealed partial class Machine {

    /// <summary>The run the machine runs for, which observes it; <see langword="null"/> for a call of <see cref="Call(IrModule, string, object[])"/>.</summary>
    public Probe? Probe { get; init; }

    /// <summary>The calls in progress.</summary>
    public int Depth => this._depth;

    /// <summary>Whether the step budget ran out.</summary>
    public bool OutOfSteps => this._steps < 0;

    /// <summary>Sets the most frames a run within a budget has at once.</summary>
    public void Nest(int depth) => this._depthLimit = depth;

    /// <summary>The steps taken of a budget, at most the budget.</summary>
    public long StepsTaken(long budget) => budget - Math.Max(this._steps, 0);

    /// <summary>
    /// Evaluates a node by an evaluator of the run's registry, after counting it when it allocates: <see langword="true"/>
    /// with its value when an evaluator evaluated it, <see langword="false"/> when the interpreter's own evaluation does.
    /// </summary>
    private bool Intercepted(IrExpr expression, Frame frame, out object? value) {
      var probe = this.Probe!;
      if (Allocates(expression))
        probe.Allocated();

      value = null;
      if (probe.Evaluators is not { } registry)
        return false;

      var opcode = OpcodeId.Of(expression);
      if (registry.Resolve(opcode) is not { } evaluator) {
        if (registry.IsWithheld(opcode))
          throw new Abort(Diagnostic.Create(InterpreterDiagnostics.NoEvaluation, null, $"no evaluator is registered for '{opcode}'"), expression);

        return false;
      }

      var operands = IrTree.Operands(expression).Select(operand => ToValue(operand.Type, this.Evaluate(operand, frame))).ToImmutableArray();
      var result = evaluator.Evaluate(new EvaluationContext(expression, operands, probe));
      if (expression.Type == IrType.Void)
        return result is null ? true : throw new InvalidOperationException($"The evaluator of {opcode} returned a value for a node of type void.");

      value = (result is null ? null : FromValue(expression.Type, result))
        ?? throw new InvalidOperationException($"The evaluator of {opcode} returned {result?.Type.ToString() ?? "nothing"} for a node of type {expression.Type}.");
      return true;
    }

    /// <summary>Whether evaluating a node allocates: a new array or object, an array of values, a new collection.</summary>
    private static bool Allocates(IrExpr expression) => expression switch {
      IrNewArray or IrNew => true,
      IrIntrinsic intrinsic => intrinsic.Op == IrIntrinsicOp.ArrayOf,
      IrCollection collection => IrCollections.Constructs(collection.Op),
      _ => false
    };

    /// <summary>The value of a local; in a run, reading one nothing was assigned to aborts with <c>PLY0120</c> rather than inventing a value.</summary>
    private object Read(Frame frame, IrLocal local) {
      var value = frame.Get(local);
      if (value is null && this.Probe is not null)
        throw new Abort(Diagnostic.Create(InterpreterDiagnostics.NoEvaluation, null, $"'Undef' value '%{local.Name}' was evaluated"), null);

      return value!;
    }

    /// <summary>A call of a foreign function in a run, resolved through the external model of the run.</summary>
    private object? Modelled(Probe probe, IrForeignCall call, IReadOnlyList<object> arguments) {
      var declaration = probe.Foreign(call.Function);
      var values = arguments.Select((argument, index) => ToValue(declaration.Parameters[index].Type, argument)).ToImmutableArray();
      if (probe.Options.Externals is not { } model || !model.TryCall(declaration, values, probe, out var result))
        throw new Abort(Diagnostic.Create(InterpreterDiagnostics.NoModel, null, call.Function), call);

      if (call.Type == IrType.Void)
        return null;

      return (result is null ? null : FromValue(call.Type, result))
        ?? throw new InvalidOperationException($"The model of '{call.Function}' returned {result?.Type.ToString() ?? "nothing"} for a result of type {call.Type}.");
    }

  }

}
