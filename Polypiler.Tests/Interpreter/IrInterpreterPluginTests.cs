using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Interpreter;

/// <summary>
/// What plugs into a run of the reference interpreter (#119): the external model that foreign calls are resolved
/// through and that records their effects, and the evaluator registry with its resolvers, its withheld operations and
/// the context it hands an evaluator - options, bounds, traps and undefined behaviour.
/// </summary>
[TestFixture]
public sealed class IrInterpreterPluginTests {

  private const string Poke = """
    using System.Runtime.InteropServices;
    public static class Snippet {
      [DllImport("c64")] private static extern void Poke(ushort address, byte value);
      [DllImport("c64")] private static extern byte Peek(ushort address);
      [DllImport("c64")] private static extern uint Mystery();
      public static void Border() { Poke(53280, 0); Poke(53281, 6); }
      public static byte Read() => Peek(53280);
      public static uint Ask() => Mystery();
    }
    """;

  private static IrModule Module(string source) => PrototypeFixture.Import("Snippet.cs", source);

  private static IrModule Methods(string methods) => Module(PrototypeFixture.Wrap(methods));

  private static IrValue I32(int value) => IrValue.Integer(IrType.I32, value);

  /// <summary>The scenario of the external call: two calls of @Poke, traced through the model as two events in order.</summary>
  [Test]
  public void AnExternalCall_IsTracedThroughItsModel() {
    var model = new ExternalModel().AddEvent("Poke", "vic2", "Device.Write");
    var result = IrInterpreter.Run(Module(Poke), "Border", [], new() { Externals = model });
    Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Returned));
    Assert.That(result.Trace.Select(effect => effect.ToString()), Is.EqualTo(new[] { "Device.Write:vic2 53280 0", "Device.Write:vic2 53281 6" }));
    Assert.That(result.Trace[0], Is.EqualTo(new EffectEvent("vic2", "Device.Write", [IrValue.Integer(IrType.U16, 53280), IrValue.Integer(IrType.U8, 0)])));
  }

  /// <summary>A model returns the value of the call.</summary>
  [Test]
  public void AModel_ReturnsTheValueOfTheCall() {
    var model = new ExternalModel().Add("Peek", (arguments, effects) => {
      effects.Emit(new("vic2", "Device.Read", arguments));
      return IrValue.Integer(IrType.U8, 14);
    });
    var result = IrInterpreter.Run(Module(Poke), "Read", [], new() { Externals = model });
    Assert.That(result.Value, Is.EqualTo(IrValue.Integer(IrType.U8, 14)));
    Assert.That(result.Trace.Single().ToString(), Is.EqualTo("Device.Read:vic2 53280"));
  }

  /// <summary>Test design 13: a call of a foreign function no model models aborts with PLY0123, and nothing is traced for it.</summary>
  [Test]
  public void AnUnmodelledExternal_Aborts() {
    var without = IrInterpreter.Run(Module(Poke), "Ask", []);
    var other = IrInterpreter.Run(Module(Poke), "Ask", [], new() { Externals = new ExternalModel().AddEvent("Poke", "vic2", "Device.Write") });
    Assert.Multiple(() => {
      Assert.That(without.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(without.Diagnostic!.Code, Is.EqualTo("PLY0123"));
      Assert.That(without.Message, Is.EqualTo("external '@Mystery' has no model"));
      Assert.That(without.At!.Node, Is.InstanceOf<IrForeignCall>());
      Assert.That(without.Trace, Is.Empty);
      Assert.That(other.Message, Is.EqualTo("external '@Mystery' has no model"));
    });
  }

  /// <summary>The exception API still traps on a foreign call, which it never runs.</summary>
  [Test]
  public void TheExceptionApi_StillTrapsOnAForeignCall()
    => Assert.That(Assert.Throws<IrTrapException>(() => IrInterpreter.Call(Module(Poke), "Border", []))!.Trap, Is.EqualTo(IrTrap.InvalidOperation));

  /// <summary>A model that returns no value, or one of another type, for a call that returns one is a defect of the model.</summary>
  [TestCase(false)]
  [TestCase(true)]
  public void AModelReturningAWrongValue_IsADefect(bool typed) {
    var model = new ExternalModel().Add("Peek", (_, _) => typed ? IrValue.Integer(IrType.U16, 1) : null);
    Assert.That(Assert.Throws<InvalidOperationException>(() => IrInterpreter.Run(Module(Poke), "Read", [], new() { Externals = model }))!.Message,
      Does.Contain("The model of 'Peek' returned " + (typed ? "u16" : "nothing")));
  }

  /// <summary>The model by handlers refuses nothing it is not given and models only what it was given.</summary>
  [Test]
  public void TheModelByHandlers_ModelsWhatItWasGiven() {
    var model = new ExternalModel();
    Assert.Throws<ArgumentNullException>(() => model.Add(null!, (_, _) => null));
    Assert.Throws<ArgumentNullException>(() => model.Add("f", null!));
    Assert.Throws<ArgumentNullException>(() => model.TryCall(null!, [], new Sink(), out _));
    var declaration = new IrForeignFunction("f", true, "lib", IrType.Void, []);
    Assert.That(model.TryCall(declaration, [], new Sink(), out var none), Is.False);
    Assert.That(none, Is.Null);
  }

  private sealed class Sink : IEffectSink {
    public List<EffectEvent> Events { get; } = [];
    public void Emit(EffectEvent effect) => this.Events.Add(effect);
  }

  /// <summary>An event equals another of the same channel, kind and payload, whatever array holds it.</summary>
  [Test]
  public void AnEvent_EqualsByItsContent() {
    var one = new EffectEvent("c", "k", [I32(1), I32(2)]);
    var same = new EffectEvent("c", "k", [.. new[] { I32(1), I32(2) }]);
    Assert.Multiple(() => {
      Assert.That(same, Is.EqualTo(one));
      Assert.That(same.GetHashCode(), Is.EqualTo(one.GetHashCode()));
      Assert.That(new EffectEvent("c", "k", [I32(1)]), Is.Not.EqualTo(one));
      Assert.That(new EffectEvent("d", "k", [I32(1), I32(2)]), Is.Not.EqualTo(one));
      Assert.That(new EffectEvent("c", "j", [I32(1), I32(2)]), Is.Not.EqualTo(one));
      Assert.That(one.Equals(null), Is.False);
      Assert.That(new EffectEvent("c", "k", []).ToString(), Is.EqualTo("k:c"));
    });
  }

  /// <summary>An evaluator that records what its context says and returns a value it computes from the operands.</summary>
  private sealed class Recording(Func<EvaluationContext, IrValue?> evaluate) : IOpcodeEvaluator {
    public List<(OpcodeId Opcode, ImmutableArray<IrValue> Operands, InterpreterOptions Options, ByteOrder Order, IMemoryModel Memory, ISchedule Schedule)> Seen { get; } = [];
    public IrValue? Evaluate(in EvaluationContext context) {
      this.Seen.Add((context.Opcode, context.Operands, context.Options, context.ByteOrder, context.Memory, context.Schedule));
      return evaluate(context);
    }
  }

  private static readonly IrModule _add = Methods("public static int F(int a, int b) => a + b;");

  private static ExecutionResult Add(OpcodeEvaluatorRegistry registry, InterpreterOptions? options = null)
    => IrInterpreter.Run(_add, "F", [I32(6), I32(7)], (options ?? InterpreterOptions.Default) with { Evaluators = registry });

  private static Recording Multiplying() => new(context => I32((int)(context.Operand(0).Number * context.Operand(1).Number)));

  /// <summary>A registered evaluator replaces the interpreter's own evaluation and sees the operands left to right.</summary>
  [Test]
  public void ARegisteredEvaluator_ReplacesTheInterpretersOwn() {
    var evaluator = Multiplying();
    var result = Add(new OpcodeEvaluatorRegistry().Register(new OpcodeId("Binary.Add"), evaluator));
    Assert.That(result.Value, Is.EqualTo(I32(42)));
    Assert.That(evaluator.Seen.Single().Opcode, Is.EqualTo(new OpcodeId("Binary.Add")));
    Assert.That(evaluator.Seen.Single().Operands, Is.EqualTo(new[] { I32(6), I32(7) }));
  }

  /// <summary>Test design 18: a resolver supplies an evaluator when none is registered, a registered one wins, the first resolver that answers wins.</summary>
  [Test]
  public void AResolver_SuppliesWhatIsNotRegistered() {
    var resolved = Multiplying();
    var registered = new Recording(_ => I32(-1));
    var asked = new List<OpcodeId>();
    Func<OpcodeId, IOpcodeEvaluator?> resolver = opcode => {
      asked.Add(opcode);
      return opcode.Name == "Binary.Add" ? resolved : null;
    };
    Assert.Multiple(() => {
      Assert.That(Add(new OpcodeEvaluatorRegistry().AddResolver(_ => null).AddResolver(resolver)).Value, Is.EqualTo(I32(42)));
      Assert.That(asked, Does.Contain(new OpcodeId("LocalRef")).And.Contain(new OpcodeId("Binary.Add")));
      Assert.That(Add(new OpcodeEvaluatorRegistry().AddResolver(resolver).Register(new OpcodeId("Binary.Add"), registered)).Value, Is.EqualTo(I32(-1)));
      Assert.That(Add(new OpcodeEvaluatorRegistry().AddResolver(_ => registered).AddResolver(resolver)).Value, Is.EqualTo(I32(-1)));
    });
  }

  /// <summary>Test design 11: an operation whose own evaluation is withheld and that has no evaluator aborts with PLY0120.</summary>
  [Test]
  public void AWithheldOperationWithoutEvaluator_Aborts() {
    var registry = new OpcodeEvaluatorRegistry().Withhold(new OpcodeId("Binary.Add"));
    var result = Add(registry);
    Assert.Multiple(() => {
      Assert.That(registry.IsWithheld(new OpcodeId("Binary.Add")), Is.True);
      Assert.That(registry.IsWithheld(new OpcodeId("Binary.Subtract")), Is.False);
      Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(result.Diagnostic!.Code, Is.EqualTo("PLY0120"));
      Assert.That(result.Message, Is.EqualTo("no evaluator is registered for 'Binary.Add'"));
      Assert.That(result.At!.Node, Is.InstanceOf<IrBinary>());
      Assert.That(Add(registry.Register(new OpcodeId("Binary.Add"), Multiplying())).Value, Is.EqualTo(I32(42)));
    });
  }

  /// <summary>An empty registry is not consulted; a registry refuses what is no evaluator or resolver.</summary>
  [Test]
  public void TheRegistry_IsEmptyUntilGivenSomething() {
    Assert.That(new OpcodeEvaluatorRegistry().IsEmpty, Is.True);
    Assert.That(new OpcodeEvaluatorRegistry().Withhold(new OpcodeId("x")).IsEmpty, Is.False);
    Assert.That(new OpcodeEvaluatorRegistry().AddResolver(_ => null).IsEmpty, Is.False);
    Assert.That(new OpcodeEvaluatorRegistry().Register(new OpcodeId("x"), Multiplying()).IsEmpty, Is.False);
    Assert.Throws<ArgumentNullException>(() => new OpcodeEvaluatorRegistry().Register(new OpcodeId("x"), null!));
    Assert.Throws<ArgumentNullException>(() => new OpcodeEvaluatorRegistry().AddResolver(null!));
    Assert.That(Add(new OpcodeEvaluatorRegistry()).Value, Is.EqualTo(I32(13)));
    Assert.That(new OpcodeEvaluatorRegistry().Resolve(new OpcodeId("x")), Is.Null);
  }

  /// <summary>The name of each kind of node.</summary>
  [Test]
  public void EveryNode_HasTheNameOfItsOperation() {
    Assert.Multiple(() => {
      Assert.That(OpcodeId.Of(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [])).Name, Is.EqualTo("Intrinsic.ArrayOf"));
      Assert.That(OpcodeId.Of(new IrCollection(IrCollectionOp.ListNew, IrType.List(IrType.I32), [])).Name, Is.EqualTo("Collection.ListNew"));
      Assert.That(OpcodeId.Of(new IrBinary(IrBinaryOp.Multiply, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2))).Name, Is.EqualTo("Binary.Multiply"));
      Assert.That(OpcodeId.Of(new IrUnary(IrUnaryOp.Negate, new IrConst(IrType.I32, 1))).Name, Is.EqualTo("Unary.Negate"));
      Assert.That(OpcodeId.Of(new IrConst(IrType.I32, 1)).ToString(), Is.EqualTo("Const"));
      Assert.That(OpcodeId.Of(new Plain(IrType.I32)).Name, Is.EqualTo("Plain"));
      Assert.Throws<ArgumentNullException>(() => OpcodeId.Of((IrExpr)null!));
    });
  }

  private sealed record Plain(IrType Type) : IrExpr(Type);

  /// <summary>Test design 17: the options reach the evaluator's context unchanged, with the flat model and the ascending schedule by default.</summary>
  [Test]
  public void TheOptions_ReachTheEvaluatorUnchanged() {
    var evaluator = Multiplying();
    var registry = new OpcodeEvaluatorRegistry().Register(new OpcodeId("Binary.Add"), evaluator);
    var memory = new Segments();
    var schedule = Schedules.Seeded(7);
    Add(registry);
    Add(registry, new() { ByteOrder = ByteOrder.Big, Memory = memory, Schedule = schedule });
    Assert.Multiple(() => {
      Assert.That((evaluator.Seen[0].Order, evaluator.Seen[0].Memory, evaluator.Seen[0].Schedule), Is.EqualTo((ByteOrder.Little, (IMemoryModel)FlatMemoryModel.Instance, Schedules.Ascending)));
      Assert.That((evaluator.Seen[1].Order, evaluator.Seen[1].Memory, evaluator.Seen[1].Schedule), Is.EqualTo((ByteOrder.Big, (IMemoryModel)memory, schedule)));
      Assert.That(evaluator.Seen[1].Options.Evaluators, Is.SameAs(registry));
    });
  }

  private sealed class Segments : IMemoryModel {
    public long Address(long allocation, long offset) => (allocation << 16) | offset;
    public (long Allocation, long Offset)? Locate(long address) => (address >> 16, address & 0xFFFF);
  }

  private static ExecutionResult Accessing(BoundsMode mode, long offset, bool promised, long count = 1) {
    var registry = new OpcodeEvaluatorRegistry().Register(new OpcodeId("Binary.Add"), new Recording(context => {
      context.Allocated();
      return I32(context.Access("buffer", 8, offset, count, promised) ? 1 : 0);
    }));
    return Add(registry, new() { Bounds = mode });
  }

  /// <summary>Test design 17: an access outside its region aborts with PLY0142 only under CheckAll; under Contract a checked one traps and a promised one is not checked.</summary>
  [Test]
  public void BoundsMode_DecidesWhatAnAccessOutsideItsRegionDoes() {
    var checkedAll = Accessing(BoundsMode.CheckAll, 8, promised: true);
    var trapped = Accessing(BoundsMode.Contract, 8, promised: false);
    Assert.Multiple(() => {
      Assert.That(checkedAll.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(checkedAll.Diagnostic!.Code, Is.EqualTo("PLY0142"));
      Assert.That(checkedAll.Message, Is.EqualTo("access of 1 elements at offset 8 exceeds region 'buffer' of 8 elements"));
      Assert.That(checkedAll.At!.Node, Is.InstanceOf<IrBinary>());
      Assert.That(checkedAll.Allocations, Is.EqualTo(1));
      Assert.That(trapped.Outcome, Is.EqualTo(ExecutionOutcome.Trapped));
      Assert.That(trapped.Trap, Is.EqualTo(IrTrap.IndexOutOfRange));
      Assert.That(trapped.At, Is.EqualTo(new InstructionRef("F", trapped.At!.Node)));
      Assert.That(trapped.At.Node, Is.InstanceOf<IrBinary>());
      Assert.That(Accessing(BoundsMode.Contract, 8, promised: true).Value, Is.EqualTo(I32(0)));
      Assert.That(Accessing(BoundsMode.CheckAll, 7, promised: false).Value, Is.EqualTo(I32(1)));
      Assert.That(Accessing(BoundsMode.CheckAll, 0, promised: false).Value, Is.EqualTo(I32(1)));
      Assert.That(Accessing(BoundsMode.CheckAll, -1, promised: false).Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(Accessing(BoundsMode.CheckAll, 0, promised: false, count: 8).Value, Is.EqualTo(I32(1)));
      Assert.That(Accessing(BoundsMode.CheckAll, 0, promised: false, count: 9).Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
      Assert.That(Accessing(BoundsMode.CheckAll, 0, promised: false, count: -1).Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
    });
  }

  /// <summary>A trap an evaluator raises is the program's to catch.</summary>
  [Test]
  public void ATrapOfAnEvaluator_CanBeCaught() {
    var module = Methods("public static int F(int a) { try { return a + 1; } catch (System.OverflowException) { return -1; } }");
    var registry = new OpcodeEvaluatorRegistry().Register(new OpcodeId("Binary.Add"), new Recording(context => {
      context.Trap(IrTrap.IntegerOverflow);
      return null;
    }));
    Assert.That(IrInterpreter.Run(module, "F", [I32(1)], new() { Evaluators = registry }).Value, Is.EqualTo(I32(-1)));
  }

  /// <summary>Test design 20: a violated [undefined] contract ends the run at that node, with no value and no trap, past every catch.</summary>
  [Test]
  public void AViolatedUndefinedContract_EndsTheRun() {
    var module = Methods("public static int F(int a) { try { return a + 1; } catch (System.Exception) { return -1; } }");
    var registry = new OpcodeEvaluatorRegistry().Register(new OpcodeId("Binary.Add"), new Recording(context => {
      context.Undefined("signed overflow");
      return null;
    }));
    var result = IrInterpreter.Run(module, "F", [I32(1)], new() { Evaluators = registry });
    Assert.Multiple(() => {
      Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.UndefinedBehaviourReached));
      Assert.That(result.Message, Is.EqualTo("signed overflow"));
      Assert.That(result.At!.Function, Is.EqualTo("F"));
      Assert.That(result.At.Node, Is.InstanceOf<IrBinary>());
      Assert.That(result.Value, Is.Null);
      Assert.That(result.Trap, Is.Null);
    });
  }

  /// <summary>The hook the [undefined] contracts of #122 raise from inside the interpreter's own evaluation reaches the outcome too.</summary>
  [Test]
  public void TheUndefinedBehaviourException_CarriesItsNode() {
    var node = new IrConst(IrType.I32, 1);
    var exception = new IrUndefinedBehaviourException(node, "why");
    Assert.That((exception.Node, exception.Message), Is.EqualTo(((IrExpr?)node, "why")));
  }

  /// <summary>An evaluator returning nothing, or a value of another type, for a node that has a value is a defect, thrown to the caller.</summary>
  [TestCase(false, "returned nothing for a node of type i32")]
  [TestCase(true, "returned u8 for a node of type i32")]
  public void AnEvaluatorReturningAWrongValue_IsADefect(bool typed, string expected) {
    var registry = new OpcodeEvaluatorRegistry().Register(new OpcodeId("Binary.Add"), new Recording(_ => typed ? IrValue.Integer(IrType.U8, 1) : null));
    Assert.That(Assert.Throws<InvalidOperationException>(() => Add(registry))!.Message, Does.Contain(expected));
  }

  /// <summary>An evaluator of a node of type void returns nothing; a value is a defect.</summary>
  [TestCase(false)]
  [TestCase(true)]
  public void AnEvaluatorOfAVoidNode_ReturnsNothing(bool valued) {
    var module = Methods("public static int F(int[] a) { System.Array.Fill(a, 3); return a[0]; }");
    var registry = new OpcodeEvaluatorRegistry().Register(OpcodeId.Of(IrIntrinsicOp.ArrayFill), new Recording(_ => valued ? I32(1) : null));
    var run = () => IrInterpreter.Run(module, "F", [IrValue.Referring(IrType.Array(IrType.I32), new int[1])], new() { Evaluators = registry });
    if (valued)
      Assert.That(Assert.Throws<InvalidOperationException>(() => run())!.Message, Does.Contain("returned a value for a node of type void"));
    else
      Assert.That(run().Value, Is.EqualTo(I32(0)));
  }

  /// <summary>An event that is none is a defect of the evaluator, thrown to the caller.</summary>
  [Test]
  public void AnEvaluatorEmittingNothing_IsADefect() {
    var registry = new OpcodeEvaluatorRegistry().Register(new OpcodeId("Binary.Add"), new Recording(context => {
      context.Emit(null!);
      return I32(0);
    }));
    Assert.Throws<ArgumentNullException>(() => Add(registry));
  }

  /// <summary>An evaluator appends to the trace in execution order.</summary>
  [Test]
  public void AnEvaluator_AppendsToTheTrace() {
    var module = Methods("public static int F(int a) => (a + 1) + (a + 2);");
    var registry = new OpcodeEvaluatorRegistry().Register(new OpcodeId("Binary.Add"), new Recording(context => {
      var sum = I32((int)(context.Operand(0).Number + context.Operand(1).Number));
      context.Emit(new("log", "Add", [sum]));
      return sum;
    }));
    var result = IrInterpreter.Run(module, "F", [I32(1)], new() { Evaluators = registry });
    Assert.That(result.Value, Is.EqualTo(I32(5)));
    Assert.That(result.Trace.Select(effect => effect.ToString()), Is.EqualTo(new[] { "Add:log 2", "Add:log 3", "Add:log 5" }));
  }

}
