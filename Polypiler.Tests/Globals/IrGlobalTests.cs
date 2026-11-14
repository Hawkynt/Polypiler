using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Globals;

/// <summary>
/// The globals of the IR built by hand: the verifier's rules for them and their initializers, the
/// interpreter's instances, which share the globals between calls, the byte form of a module, and the
/// passes that rebuild a function, which carry an assignment to a global along.
/// </summary>
[TestFixture]
public sealed class IrGlobalTests {

  private static readonly IrLocal _a = new("a", IrType.I32);

  private static IrConst I(int value) => new(IrType.I32, value);

  private static IrGlobalRef Ref(IrGlobal global) => new(global);

  private static IrFunction Function(string name, IrType returns, params IrStmt[] body) => new(name, true, returns, [], [], new IrBlock(body));

  private static IrModule Module(IrGlobal[] globals, params IrFunction[] functions) => new("M", functions) { Globals = globals };

  private static IEnumerable<TestCaseData> IllTyped() {
    TestCaseData Case(string name, IrModule module, string message) => new TestCaseData(module, message).SetName("IllTyped: " + name);
    var counter = new IrGlobal("counter", IrType.I32, I(0));
    var table = new IrGlobal("table", IrType.Array(IrType.U8), new IrNewArray(IrType.Array(IrType.U8), I(4)));
    var reader = Function("reader", IrType.I32, new IrReturn(Ref(counter)));
    var indirect = Function("indirect", IrType.I32, new IrReturn(new IrCall("reader", IrType.I32, [])));
    var vec = new IrStructType("Vec", [new IrField("X", IrType.F64, false)]);

    yield return Case("global named like a function", Module([new IrGlobal("F", IrType.I32, null)], Function("F", IrType.Void)), "ill-typed IR in 'F': the module declares the name twice");
    yield return Case("global named like a constant", new IrModule("M", [], [new IrModuleConst("K", IrType.I32, [1])]) { Globals = [new IrGlobal("K", IrType.I32, null)] },
      "ill-typed IR in 'K': the module declares the name twice");
    yield return Case("two globals of one name", Module([new IrGlobal("g", IrType.I32, null), new IrGlobal("g", IrType.U8, null)]), "ill-typed IR in 'g': the module declares the name twice");
    yield return Case("global of void", Module([new IrGlobal("g", IrType.Void, null)]), "a global of the type void");
    yield return Case("global of a struct", new IrModule("M", []) { Types = [vec], Globals = [new IrGlobal("g", IrType.Struct(vec), null)] }, "a global of the type Vec");
    yield return Case("array global without initializer", Module([new IrGlobal("g", IrType.Array(IrType.I32), null)]), "an array global without an initializer");
    yield return Case("initializer of another type", Module([new IrGlobal("g", IrType.U32, I(1))]), "the initializer has the type i32, expected u32");
    yield return Case("initializer reading itself", Module([new IrGlobal("g", IrType.I32, Ref(counter))]), "the global 'counter' is not among the globals it may read");
    yield return Case("initializer reading a later global", Module([new IrGlobal("first", IrType.I32, Ref(counter)), counter]), "the global 'counter' is not among the globals it may read");
    yield return Case("initializer reading an array element", Module([table, new IrGlobal("g", IrType.U8, new IrArrayLoad(Ref(table), I(0)))]), "the initializer holds a IrArrayLoad, which is not static");
    yield return Case("initializer with an operation of the library", Module([new IrGlobal("g", IrType.I32, new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [I(3)]))]),
      "the initializer holds a IrIntrinsic, which is not static");
    yield return Case("initializer calling a function that reads a global", Module([counter, new IrGlobal("g", IrType.I32, new IrCall("reader", IrType.I32, []))], reader),
      "the initializer calls 'reader', which reads or writes a global");
    yield return Case("initializer calling a function that calls one that reads a global", Module([counter, new IrGlobal("g", IrType.I32, new IrCall("indirect", IrType.I32, []))], reader, indirect),
      "the initializer calls 'indirect', which reads or writes a global");
    yield return Case("global of another module", Module([], Function("F", IrType.I32, new IrReturn(Ref(counter)))), "the global 'counter' is not among the globals it may read");
    yield return Case("assignment to an array global", Module([table], Function("F", IrType.Void, new IrGlobalAssign(table, new IrNewArray(table.Type, I(1))))), "the array global 'table' is assigned");
    yield return Case("assignment of another type", Module([counter], Function("F", IrType.Void, new IrGlobalAssign(counter, new IrConst(IrType.U8, 1)))),
      "the value assigned to 'counter' has the type u8, expected i32");
    yield return Case("assignment to a global of another module", Module([], Function("F", IrType.Void, new IrGlobalAssign(counter, I(1)))), "the global 'counter' is not among the globals it may read");
  }

  [TestCaseSource(nameof(IllTyped))]
  public void IllTypedModule_IsRefusedByTheVerifier(IrModule module, string message)
    => Assert.That(IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message), Has.Some.Contains(message));

  /// <summary>Test design 3: every class of initializer the IR allows is static, and so is a module with them.</summary>
  [Test]
  public void EveryKindOfStaticInitializer_IsAccepted() {
    var pure = new IrFunction("Twice", false, IrType.I32, [_a], [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.Add, new IrLocalRef(_a), new IrLocalRef(_a)))]));
    var constant = new IrModuleConst("K", IrType.I32, [5]);
    var none = new IrGlobal("none", IrType.Bool, null);
    var literal = new IrGlobal("literal", IrType.I32, I(3));
    var expression = new IrGlobal("expression", IrType.I64, new IrConvert(IrType.I64, new IrBinary(IrBinaryOp.Multiply, I(6), new IrConstRef(constant))));
    var earlier = new IrGlobal("earlier", IrType.I32, new IrConditional(new IrConst(IrType.Bool, 1), new IrUnary(IrUnaryOp.Negate, Ref(literal)), I(0)));
    var call = new IrGlobal("call", IrType.I32, new IrCall("Twice", IrType.I32, [Ref(earlier)]));
    var list = new IrGlobal("list", IrType.Array(IrType.U16), new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U16), [new IrConst(IrType.U16, 7)]));
    var created = new IrGlobal("created", IrType.Array(IrType.F64), new IrNewArray(IrType.Array(IrType.F64), Ref(literal)));
    var module = new IrModule("M", [pure, Function("F", IrType.I32, new IrReturn(Ref(call)))], [constant]) { Globals = [none, literal, expression, earlier, call, list, created] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);

    var instance = IrInterpreter.Instantiate(module);
    Assert.That(instance.Call("F"), Is.EqualTo(-6));
    Assert.That(IrVerifier.Impure(module), Is.EquivalentTo(new[] { "F" }));
  }

  /// <summary>Test design 2 and 6: the calls of one instance share the globals; every instance and every call of the module alone starts afresh.</summary>
  [Test]
  public void TheCallsOfAnInstance_ShareTheGlobals() {
    var counter = new IrGlobal("counter", IrType.I32, I(10));
    var step = Function("Step", IrType.I32, new IrReturn(new IrStep(Ref(counter), true, false, false)));
    var module = Module([counter], step);
    var instance = IrInterpreter.Instantiate(module);
    Assert.That(new[] { instance.Call("Step"), instance.Call("Step"), instance.Call("Step") }, Is.EqualTo(new object[] { 11, 12, 13 }));
    Assert.That(IrInterpreter.Instantiate(module).Call("Step"), Is.EqualTo(11));
    Assert.That(IrInterpreter.Call(module, "Step", []), Is.EqualTo(11));
    Assert.That(IrInterpreter.Call(module, "Step", []), Is.EqualTo(11));
    Assert.That(instance.Module, Is.SameAs(module));
  }

  /// <summary>Test design 2: a global of every type starts at zero without an initializer, and reads, writes and steps as a local does.</summary>
  [TestCase(IrTypeKind.Bool, false)]
  [TestCase(IrTypeKind.I8, (sbyte)0)]
  [TestCase(IrTypeKind.U8, (byte)0)]
  [TestCase(IrTypeKind.I16, (short)0)]
  [TestCase(IrTypeKind.U16, (ushort)0)]
  [TestCase(IrTypeKind.I32, 0)]
  [TestCase(IrTypeKind.U32, 0u)]
  [TestCase(IrTypeKind.I64, 0L)]
  [TestCase(IrTypeKind.U64, 0UL)]
  [TestCase(IrTypeKind.F32, 0f)]
  [TestCase(IrTypeKind.F64, 0d)]
  public void AGlobalWithoutInitializer_StartsAtZero(IrTypeKind kind, object zero) {
    var type = new[] { IrType.Bool, IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.F32, IrType.F64 }.Single(type => type.Kind == kind);
    var global = new IrGlobal("g", type, null);
    var module = Module([global], Function("Get", type, new IrReturn(Ref(global))));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "Get", []), Is.EqualTo(zero));
  }

  /// <summary>Test design 4: a step and an addition wrap around a global as they do a local; a trapping step traps.</summary>
  [Test]
  public void ArithmeticOnAGlobal_WrapsAround() {
    var wide = new IrGlobal("wide", IrType.U64, new IrConst(IrType.U64, -1));
    var word = new IrGlobal("word", IrType.I32, I(int.MaxValue));
    var narrow = new IrGlobal("narrow", IrType.I32, I(int.MaxValue));
    var module = Module([wide, word, narrow],
      Function("StepWide", IrType.U64, new IrExprStmt(new IrStep(Ref(wide), true, true, false)), new IrReturn(Ref(wide))),
      Function("AddWord", IrType.I32, new IrGlobalAssign(word, new IrBinary(IrBinaryOp.Add, Ref(word), I(1))), new IrReturn(Ref(word))),
      Function("TrapNarrow", IrType.I32, new IrReturn(new IrStep(Ref(narrow), true, true, true))));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var instance = IrInterpreter.Instantiate(module);
    Assert.That(instance.Call("StepWide"), Is.EqualTo(0UL));
    Assert.That(instance.Call("AddWord"), Is.EqualTo(int.MinValue));
    Assert.That(() => instance.Call("TrapNarrow"), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.IntegerOverflow));
  }

  /// <summary>Test design 2: the elements of an array global change from call to call; the array, also as an argument, stays the same.</summary>
  [Test]
  public void TheElementsOfAnArrayGlobal_PersistBetweenCalls() {
    var buckets = new IrGlobal("buckets", IrType.Array(IrType.U32), new IrNewArray(IrType.Array(IrType.U32), I(4)));
    var element = new IrArrayLoad(Ref(buckets), new IrBinary(IrBinaryOp.And, new IrLocalRef(_a), I(3)));
    var add = new IrFunction("Add", true, IrType.U32, [_a], [], new IrBlock([new IrExprStmt(new IrStep(element, true, false, false)), new IrReturn(element)]));
    var array = new IrLocal("array", IrType.Array(IrType.U32));
    var first = new IrFunction("First", false, IrType.U32, [array], [], new IrBlock([new IrReturn(new IrArrayLoad(new IrLocalRef(array), I(1)))]));
    var read = Function("Read", IrType.U32, new IrReturn(new IrCall("First", IrType.U32, [Ref(buckets)])));
    var module = Module([buckets], add, first, read);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var instance = IrInterpreter.Instantiate(module);
    Assert.That(new[] { instance.Call("Add", 1), instance.Call("Add", 5), instance.Call("Add", 2), instance.Call("Read") }, Is.EqualTo(new object[] { 1u, 2u, 1u, 2u }));
  }

  /// <summary>An initializer that traps makes the instance trap before any function runs.</summary>
  [Test]
  public void AnInitializerThatTraps_TrapsTheInstance() {
    var module = Module([new IrGlobal("g", IrType.I32, new IrBinary(IrBinaryOp.Divide, I(1), I(0)))], Function("F", IrType.Void));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(() => IrInterpreter.Instantiate(module), Throws.TypeOf<IrTrapException>());
  }

  /// <summary>A global in the step of a for, which is one statement there as everywhere.</summary>
  [Test]
  public void AnAssignmentToAGlobal_StandsInTheStepOfAFor() {
    var total = new IrGlobal("total", IrType.I32, I(0));
    var loop = new IrFor(new IrBinary(IrBinaryOp.Less, Ref(total), I(5)), new IrBlock([new IrContinue()]),
      new IrBlock([new IrGlobalAssign(total, new IrBinary(IrBinaryOp.Add, Ref(total), I(2)))]));
    var module = Module([total], Function("F", IrType.I32, loop, new IrReturn(Ref(total))));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(6));
  }

  /// <summary>The byte form keeps every global, its initializer and every read and write of it as one and the same object.</summary>
  [Test]
  public void TheGlobals_ReadBackFromBytes() {
    var seed = new IrGlobal("seed", IrType.U32, new IrConst(IrType.U32, 7));
    var state = new IrGlobal("state", IrType.U32, new IrBinary(IrBinaryOp.Multiply, Ref(seed), new IrConst(IrType.U32, 3)));
    var none = new IrGlobal("none", IrType.Bool, null);
    var module = Module([seed, state, none], Function("F", IrType.U32,
      new IrGlobalAssign(state, new IrBinary(IrBinaryOp.Add, Ref(state), Ref(seed))), new IrReturn(Ref(state))));
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(read.Globals.Select(global => (global.Name, global.Type)), Is.EqualTo(module.Globals.Select(global => (global.Name, global.Type))));
    Assert.That(read.Globals[2].Initializer, Is.Null);
    Assert.That(((IrGlobalRef)((IrBinary)read.Globals[1].Initializer!).Left).Global, Is.SameAs(read.Globals[0]));
    var assignment = (IrGlobalAssign)read.Functions[0].Body.Statements[0];
    Assert.That(assignment.Global, Is.SameAs(read.Globals[1]));
    Assert.That(IrInterpreter.Call(read, "F", []), Is.EqualTo(28u));
    Assert.That(() => IrModuleFormat.Write(Module([], Function("F", IrType.U32, new IrReturn(Ref(seed))))), Throws.ArgumentException.With.Message.Contains("global 'seed'"));
    Assert.That(() => IrModuleFormat.Write(Module([], Function("F", IrType.Void, new IrGlobalAssign(seed, new IrConst(IrType.U32, 1))))), Throws.ArgumentException.With.Message.Contains("global 'seed'"));
  }

  /// <summary>Test design 10: a read of a global is an effect to the recognition rules, which may neither move nor merge it.</summary>
  [Test]
  public void AReadOfAGlobal_IsAnEffectToTheRules() {
    var global = new IrGlobal("g", IrType.I32, null);
    Assert.That(IrShape.IsSideEffectFree(Ref(global)), Is.False);
    Assert.That(IrShape.IsTrapFree(Ref(global)), Is.False);
    Assert.That(IrShape.Same(Ref(global), Ref(global)), Is.False);
    var assignment = new IrGlobalAssign(global, I(1));
    Assert.That(IrStatements.Expressions(assignment), Is.EqualTo(new IrExpr[] { I(1) }));
    Assert.That(IrTree.Expressions(assignment), Is.EqualTo(new IrExpr[] { I(1) }));
  }

  /// <summary>The optimizer rewrites the value of an assignment to a global and keeps the assignment: a rotate spelled out becomes the rotation.</summary>
  [Test]
  public void TheOptimizer_RewritesTheValueAssignedToAGlobal() {
    var global = new IrGlobal("g", IrType.U32, null);
    var x = new IrLocal("x", IrType.U32);
    var rotate = new IrBinary(IrBinaryOp.Or,
      new IrBinary(IrBinaryOp.ShiftLeft, new IrLocalRef(x), I(3)),
      new IrBinary(IrBinaryOp.ShiftRightLogical, new IrLocalRef(x), I(29)));
    var function = new IrFunction("F", true, IrType.U32, [x], [], new IrBlock([new IrGlobalAssign(global, rotate), new IrReturn(Ref(global))]));
    var module = Module([global], function);
    var optimized = Optimizer.Run(module, new OptimizerOptions { Verify = true, Target = new RuleTarget(true) });
    Assert.That(optimized.Module.Globals, Is.EqualTo(module.Globals));
    var assigned = (IrGlobalAssign)optimized.Module.Functions[0].Body.Statements[0];
    Assert.That(assigned.Value, Is.TypeOf<IrIntrinsic>().With.Property(nameof(IrIntrinsic.Op)).EqualTo(IrIntrinsicOp.RotateLeft));
    Assert.That(IrInterpreter.Call(optimized.Module, "F", [0x80000001u]), Is.EqualTo(IrInterpreter.Call(module, "F", [0x80000001u])));
  }

  /// <summary>The legalization replaces an operation in the value of an assignment to a global, and takes no name of a global for a helper.</summary>
  [Test]
  public void TheLegalization_ReplacesAnOperationAssignedToAGlobal() {
    var global = new IrGlobal("FloatExp", IrType.F64, null);
    var x = new IrLocal("x", IrType.F64);
    var function = new IrFunction("F", true, IrType.F64, [x], [], new IrBlock([
      new IrGlobalAssign(global, new IrIntrinsic(IrIntrinsicOp.Exp, IrType.F64, [new IrLocalRef(x)])),
      new IrReturn(Ref(global))
    ]));
    var lowered = FloatLegalization.Lower(Module([global], function), FloatImplementations.Module, _ => false);
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    Assert.That(((IrGlobalAssign)lowered.Functions[0].Body.Statements[0]).Value, Is.TypeOf<IrCall>().With.Property(nameof(IrCall.Function)).EqualTo("FloatExp_"));
    Assert.That(IrInterpreter.Call(lowered, "F", [1.0]), Is.EqualTo(FloatOperations.Exp(1.0)));
  }

}
