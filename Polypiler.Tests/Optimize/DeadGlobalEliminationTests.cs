using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Interprocedural;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Verification.Generators;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Rust;

namespace Polypiler.Tests.Optimize;

/// <summary>O0383 marks code and data together, without deleting observable initialization.</summary>
[TestFixture]
public sealed class DeadGlobalEliminationTests {

  private static IrConst I(long value) => new(IrType.I32, value);
  private static IrFunction F(string name, bool exported, IrExpr value)
    => new(name, exported, value.Type, [], [], new IrBlock([new IrReturn(value)]));

  private static OptimizationResult Run(IrModule module, bool functions = true, bool globals = true,
    OptimizationLevel level = OptimizationLevel.Default, IReadOnlySet<string>? globalRoots = null, IReadOnlySet<string>? roots = null)
    => IrPassPipeline.Default.Run(module, new OptimizerOptions {
      Verify = true, Level = level, GlobalRoots = globalRoots ?? new HashSet<string>(), Roots = roots ?? new HashSet<string>(),
      Disabled = IrPassPipeline.Default.Ids.Where(id => !(id == "O0381" && functions || id == "O0383" && globals)).ToHashSet()
    });

  private static IrModule Tables() {
    var limit = new IrGlobal("limit", IrType.I32, I(320));
    var tableType = IrType.Array(IrType.U16);
    var table = new IrGlobal("table", tableType, new IrIntrinsic(IrIntrinsicOp.ArrayOf, tableType,
      Enumerable.Range(1, 4).Select(value => (IrExpr)new IrConst(IrType.U16, value)).ToArray()));
    var signature = IrType.Function(IrType.I32);
    var ops = new IrGlobal("ops", signature, new IrCall("factory", signature, []));
    return new("tables", [F("main", true, new IrGlobalRef(limit)), F("Unused", false, new IrArrayLoad(new IrGlobalRef(table), I(0))),
      F("factory", false, new IrFunctionRef(signature, "area")), F("area", false, I(7))]) { Globals = [limit, table, ops] };
  }

  [Test]
  public void JointSweepReleasesDeadDataAndItsDispatchFunctionsAtomically() {
    var before = Tables();
    Assert.That(IrVerifier.Verify(before), Is.Empty);
    var after = Run(before);
    Assert.That(after.Module.Functions.Select(f => f.Name), Is.EqualTo(new[] { "main" }));
    Assert.That(after.Module.Globals.Select(g => g.Name), Is.EqualTo(new[] { "limit" }));
    Assert.That(after.Module.Functions[0], Is.SameAs(before.Functions[0]));
    Assert.That(IrInterpreter.Instantiate(after.Module).Call("main"), Is.EqualTo(IrInterpreter.Instantiate(before).Call("main")));
    Assert.That(after.Applications.Count(a => a.Id == "O0383"), Is.EqualTo(2));
    Assert.That(after.Applications.Count(a => a.Id == "O0381"), Is.EqualTo(3));
    Assert.That(after.Notes, Does.Contain("O0383 applied: global @table removed (8 bytes): unreferenced [proof: structural]"));
  }

  [Test]
  public void WithoutTheFunctionSweepEveryExistingBodyProtectsItsGlobals() {
    var before = Tables();
    var after = Run(before, functions: false).Module;
    Assert.That(after.Functions, Is.SameAs(before.Functions));
    Assert.That(after.Globals.Select(g => g.Name), Is.EqualTo(new[] { "limit", "table" }));
  }

  [TestCase(true)]
  [TestCase(false)]
  public void InitializerChainsAreFollowedOnlyFromLiveData(bool live) {
    var inner = new IrGlobal("inner", IrType.I32, I(7));
    var middle = new IrGlobal("middle", IrType.I32, new IrGlobalRef(inner));
    var outer = new IrGlobal("outer", IrType.I32, new IrGlobalRef(middle));
    var module = new IrModule("chain", [F("main", true, live ? new IrGlobalRef(outer) : I(0))]) { Globals = [inner, middle, outer] };
    var after = Run(module).Module;
    Assert.That(after.Globals.Count, Is.EqualTo(live ? 3 : 0));
    Assert.That(IrInterpreter.Instantiate(after).Call("main"), Is.EqualTo(live ? 7 : 0));
    if (live)
      Assert.That(after, Is.SameAs(module));
  }

  [TestCase("external")]
  [TestCase("device")]
  public void ExplicitHostRootsProtectDataAndItsInitializerClosure(string kind) {
    var module = Tables();
    var after = Run(module, globalRoots: new HashSet<string> { "ops", "absent-" + kind }).Module;
    Assert.That(after.Globals.Select(g => g.Name), Is.EqualTo(new[] { "limit", "ops" }));
    Assert.That(after.Functions.Select(f => f.Name), Is.EqualTo(new[] { "main", "factory", "area" }));
    Assert.That(IrInterpreter.Instantiate(after).Call("main"), Is.EqualTo(320));
  }

  [Test]
  public void WritesIncludingNestedClosureWritesKeepTheGlobal() {
    var state = new IrGlobal("state", IrType.I32, null);
    var closureState = new IrGlobal("closureState", IrType.I32, I(1));
    var lambda = IrLambda.Of([], IrType.Void, [], new IrBlock([new IrGlobalAssign(closureState, I(2)), new IrReturn(null)]));
    var main = new IrFunction("main", true, IrType.I32, [], [], new IrBlock([
      new IrIf(new IrConst(IrType.Bool, 0), new IrBlock([new IrGlobalAssign(state, I(3)), new IrExprStmt(lambda)]), null),
      new IrReturn(I(7))
    ]));
    var module = new IrModule("writes", [main]) { Globals = [state, closureState] };
    Assert.That(Run(module).Module, Is.SameAs(module));
  }

  [Test]
  public void AdditionalEntryPointsAndModuleInitializerKeepTheirData() {
    var state = new IrGlobal("state", IrType.I32, I(1));
    var other = new IrGlobal("other", IrType.I32, I(2));
    var module = new IrModule("roots", [F("test", false, new IrGlobalRef(state)),
      new IrFunction("start", false, IrType.Void, [], [], new IrBlock([new IrGlobalAssign(other, I(3)), new IrReturn(null)]))]) {
      Globals = [state, other], Initializer = "start"
    };
    Assert.That(Run(module, roots: new HashSet<string> { "test" }).Module, Is.SameAs(module));
  }

  [Test]
  public void AnUnusedInitializerThatTrapsIsStillExecuted() {
    var trap = new IrGlobal("trap", IrType.I32, new IrBinary(IrBinaryOp.Divide, I(1), I(0)));
    var module = new IrModule("trap", [F("main", true, I(7))]) { Globals = [trap] };
    var after = Run(module).Module;
    Assert.That(after, Is.SameAs(module));
    Assert.That(() => IrInterpreter.Instantiate(after), Throws.TypeOf<IrTrapException>());
  }

  [Test]
  public void RecursionInAnUnusedInitializerKeepsItsEntireClosure() {
    var global = new IrGlobal("loop", IrType.I32, new IrCall("forever", IrType.I32, []));
    var module = new IrModule("loop", [F("main", true, I(7)), F("forever", false, new IrCall("forever", IrType.I32, []))]) { Globals = [global] };
    Assert.That(Run(module).Module, Is.SameAs(module));
  }

  [TestCase(OptimizationLevel.None, true)]
  [TestCase(OptimizationLevel.Default, false)]
  public void DisabledOrNonePreservesTheGlobalAndItsInitializerRoots(OptimizationLevel level, bool globals) {
    var signature = IrType.Function(IrType.I32);
    var global = new IrGlobal("dispatch", signature, new IrCall("factory", signature, []));
    var module = new IrModule("disabled", [F("main", true, I(1)), F("factory", false, new IrFunctionRef(signature, "target")), F("target", false, I(2))]) { Globals = [global] };
    var result = Run(module, globals: globals, level: level);
    Assert.That(result.Module, Is.SameAs(module));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
  }

  [Test]
  public void EmptyModulesAndAlreadyLiveModulesAreUnchanged() {
    var empty = new IrModule("empty", []);
    Assert.That(Run(empty).Module, Is.SameAs(empty));
    var global = new IrGlobal("state", IrType.I32, I(7));
    var module = new IrModule("live", [F("main", true, new IrGlobalRef(global))]) { Globals = [global] };
    Assert.That(Run(module).Module, Is.SameAs(module));
    Assert.That(ModuleReachability.Compute(module).LiveGlobals, Does.Contain(global));
  }

  [Test]
  public void RetainedClassDeclarationsProtectTheirStaticFields() {
    var global = new IrGlobal("C.value", IrType.I32, I(7));
    var type = new IrClassType("C");
    type.Define([]);
    type.DefineMembers([], [], [], [global]);
    var module = new IrModule("class", [F("main", true, I(0))]) { Globals = [global], Types = [type] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(Run(module).Module, Is.SameAs(module));
  }

  [Test]
  public void StaticArraysScalarsAndStringsAreRemovedWithTheirLogicalSizes() {
    var array = new IrGlobal("array", IrType.Array(IrType.U16), new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U16),
      [new IrConst(IrType.U16, 1), new IrConst(IrType.U16, 2), new IrConst(IrType.U16, 3)]));
    var module = new IrModule("sizes", [F("main", true, I(0))]) {
      Globals = [
      array, new IrGlobal("alias", array.Type, new IrGlobalRef(array)),
      new IrGlobal("zero", IrType.Array(IrType.I32), new IrNewArray(IrType.Array(IrType.I32), I(0))),
      new IrGlobal("fixed", IrType.Array(IrType.I32), new IrNewArray(IrType.Array(IrType.I32), I(3))),
      new IrGlobal("flag", IrType.Bool, new IrConst(IrType.Bool, 1)),
      new IrGlobal("float", IrType.F64, new IrConst(IrType.F64, 0)),
      new IrGlobal("text", IrType.String, new IrStrConst("unused"))
    ]
    };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Run(module);
    Assert.That(result.Module.Globals, Is.Empty);
    foreach (var (name, bytes) in new[] { ("array", "6"), ("alias", "unknown"), ("zero", "0"), ("fixed", "12"), ("flag", "1"), ("float", "8"), ("text", "unknown") })
      Assert.That(result.Notes, Does.Contain($"O0383 applied: global @{name} removed ({bytes} bytes): unreferenced [proof: structural]"));
  }

  [TestCase(-1)]
  [TestCase(2147483648L)]
  public void InvalidArrayLengthsCannotDisappear(long length) {
    var type = IrType.Array(IrType.I32);
    var module = new IrModule("length", [F("main", true, I(0))]) { Globals = [new IrGlobal("array", type, new IrNewArray(type, new IrConst(length < 0 ? IrType.I32 : IrType.U32, length)))] };
    Assert.That(Run(module).Module, Is.SameAs(module));
  }

  [Test]
  public void DynamicArrayLengthAndTrappingInitializerCallsAreConservativeRoots() {
    var type = IrType.Array(IrType.I32);
    var length = new IrGlobal("length", IrType.I32, I(3));
    var array = new IrGlobal("array", type, new IrNewArray(type, new IrGlobalRef(length)));
    var trap = new IrGlobal("trap", IrType.I32, new IrCall("divide", IrType.I32, []));
    var module = new IrModule("unknown", [F("main", true, I(0)), F("divide", false, new IrBinary(IrBinaryOp.Divide, I(1), I(0)))]) { Globals = [length, array, trap] };
    Assert.That(Run(module).Module, Is.SameAs(module));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(int.MaxValue)]
  public void FixedArrayLengthBoundariesAreStaticData(int length) {
    var type = IrType.Array(IrType.I32);
    var module = new IrModule("fixed", [F("main", true, I(0))]) { Globals = [new IrGlobal("array", type, new IrNewArray(type, I(length)))] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Run(module);
    Assert.That(result.Module.Globals, Is.Empty);
    Assert.That(result.Notes.Single(), Does.Contain($"({4L * length} bytes)"));
  }

  [Test]
  public void ADeadCodeAndDataCycleHasNoAccidentalInitializerRoot() {
    var signature = IrType.Function(IrType.I32);
    var global = new IrGlobal("cycle", IrType.I32, new IrCall("factory", IrType.I32, []));
    var factory = new IrFunction("factory", false, IrType.I32, [], [], new IrBlock([
      new IrExprStmt(new IrFunctionRef(signature, "handler")), new IrReturn(I(1))
    ]));
    var module = new IrModule("cycle", [F("main", true, I(7)), factory, F("handler", false, new IrGlobalRef(global))]) { Globals = [global] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Run(module).Module;
    Assert.That(result.Globals, Is.Empty);
    Assert.That(result.Functions.Select(f => f.Name), Is.EqualTo(new[] { "main" }));
    Assert.That(IrInterpreter.Instantiate(result).Call("main"), Is.EqualTo(IrInterpreter.Instantiate(module).Call("main")));
  }

  [Test]
  public void CheckedInitializerOverflowRemainsObservable() {
    var value = new IrBinary(IrBinaryOp.AddChecked, I(int.MaxValue), I(1));
    var module = new IrModule("overflow", [F("main", true, I(0))]) { Globals = [new IrGlobal("unused", IrType.I32, value)] };
    var result = Run(module).Module;
    Assert.That(result, Is.SameAs(module));
    foreach (var version in new[] { module, result })
      Assert.That(() => IrInterpreter.Instantiate(version), Throws.TypeOf<IrTrapException>());
  }

  [Test]
  public void AStepIsAGlobalReferenceAndKeepsItsStateAcrossCalls() {
    var global = new IrGlobal("counter", IrType.I32, I(0));
    var module = new IrModule("step", [F("main", true, new IrStep(new IrGlobalRef(global), true, false, false))]) { Globals = [global] };
    var instance = IrInterpreter.Instantiate(Run(module).Module);
    Assert.That(instance.Call("main"), Is.EqualTo(1));
    Assert.That(instance.Call("main"), Is.EqualTo(2));
  }

  [Test]
  public void StandalonePassIgnoresFunctionScopeAndValidatesArguments() {
    var module = Tables();
    var pass = new DeadGlobalElimination();
    var result = pass.Run(module, _ => false, (_, _) => { });
    Assert.That(result.Functions, Is.SameAs(module.Functions));
    Assert.That(result.Globals.Select(g => g.Name), Is.EqualTo(new[] { "limit", "table" }));
    Assert.Multiple(() => {
      Assert.That(() => pass.Run(null!, _ => true, (_, _) => { }), Throws.ArgumentNullException);
      Assert.That(() => pass.Run(module, null!, (_, _) => { }), Throws.ArgumentNullException);
      Assert.That(() => pass.Run(module, _ => true, null!), Throws.ArgumentNullException);
      Assert.That(() => pass.Run(module, _ => true, (_, _) => { }, null!), Throws.ArgumentNullException);
    });
  }

  [TestCase(true)]
  [TestCase(false)]
  public void JointDeletionWorksInEitherPassOrder(bool globalsFirst) {
    var pipeline = new IrPassPipeline(globalsFirst ? [new DeadGlobalElimination(), new DeadFunctionElimination()]
      : [new DeadFunctionElimination(), new DeadGlobalElimination()]);
    var result = pipeline.Run(Tables(), new OptimizerOptions { Verify = true });
    Assert.That(result.Module.Globals.Select(g => g.Name), Is.EqualTo(new[] { "limit" }));
    Assert.That(result.Module.Functions.Select(f => f.Name), Is.EqualTo(new[] { "main" }));
  }

  [Test]
  public void SourceSizesAreRecordedForEveryBackend() {
    var used = new IrGlobal("used", IrType.I32, I(320));
    var unused = new IrGlobal("unused", IrType.I32, I(7));
    var before = new IrModule("sizes", [F("main", true, new IrGlobalRef(used)), F("dead", false, new IrGlobalRef(unused))]) { Globals = [used, unused] };
    var after = Run(before).Module;
    foreach (var (name, emit) in new (string, Func<IrModule, string>)[] {
      ("C#", m => string.Concat(CSharpEmitter.Emit(m).Select(f => f.Content))),
      ("JavaScript", m => string.Concat(JavaScriptEmitter.Emit(m).Select(f => f.Content))),
      ("Python", m => string.Concat(PythonEmitter.Emit(m).Select(f => f.Content))),
      ("Rust", m => string.Concat(RustEmitter.Emit(m, RustProfile.Default).Select(f => f.Content)))
    }) {
      var first = System.Text.Encoding.UTF8.GetByteCount(emit(before));
      var last = System.Text.Encoding.UTF8.GetByteCount(emit(after));
      TestContext.Out.WriteLine($"{name}: {first} -> {last} source bytes; logical scalar data 8 -> 4 bytes");
      Assert.That(last, Is.LessThan(first));
    }
  }

  /// <summary>Seed 383: independent textual graph closure and eight exported executions per module.</summary>
  [Test, Category("Slow")]
  public void TwoThousandJointGraphsHaveTheExpectedSurvivorsAndOutcomes() {
    var random = new SeededRandom(383);
    for (var sample = 0; sample < 2000; ++sample) {
      var count = 3 + random.NextBelow(12);
      var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
      var exports = Enumerable.Range(0, count).Where(i => i == 0 || random.NextBelow(5) == 0).Select(i => "f" + i).ToHashSet();
      var hostRoots = new HashSet<string>();
      var globals = new List<IrGlobal>();
      var factories = new List<IrFunction>();
      var signature = IrType.Function(new IrFunctionType([IrType.I32], IrType.I32));
      for (var i = 0; i < count; ++i) {
        var name = "g" + i;
        var edges = graph[name] = [];
        IrExpr initializer = I(i + 1);
        var form = random.NextBelow(3);
        if (form == 0 && i > 0) {
          var target = random.NextBelow(i);
          initializer = new IrGlobalRef(globals[target]);
          edges.Add("g" + target);
        } else if (form == 1) {
          var target = "f" + random.NextBelow(count);
          var factory = "factory" + i;
          initializer = new IrCall(factory, IrType.I32, []);
          factories.Add(new(factory, false, IrType.I32, [], [], new IrBlock([
            new IrExprStmt(new IrFunctionRef(signature, target)), new IrReturn(I(i + 1))
          ])));
          edges.Add(factory);
          graph[factory] = [target];
        }
        globals.Add(new(name, IrType.I32, initializer));
        if (random.NextBelow(8) == 0)
          hostRoots.Add(name);
      }

      var state = new IrGlobal("state", IrType.I32, I(0));
      globals.Add(state);
      graph["state"] = [];
      var functions = new List<IrFunction>();
      for (var i = 0; i < count; ++i) {
        var name = "f" + i;
        var target = "f" + random.NextBelow(count);
        var data = globals[random.NextBelow(count)];
        var n = new IrLocal("n", IrType.I32);
        graph[name] = [target, data.Name, "state"];
        functions.Add(new(name, exports.Contains(name), IrType.I32, [n], [], new IrBlock([
          new IrGlobalAssign(state, new IrBinary(IrBinaryOp.Add,
            new IrBinary(IrBinaryOp.Multiply, new IrGlobalRef(state), I(31)), I(i + 1))),
          new IrIf(new IrBinary(IrBinaryOp.Greater, new IrLocalRef(n), I(0)), new IrBlock([
            new IrReturn(new IrBinary(IrBinaryOp.Add, new IrGlobalRef(data), new IrCall(target, IrType.I32,
              [new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(n), I(1))])))
          ]), null), new IrReturn(new IrGlobalRef(data))
        ])));
      }
      functions.Add(F("observe", true, new IrGlobalRef(state)));
      graph["observe"] = ["state"];
      var module = new IrModule("graph" + sample, [.. functions, .. factories]) { Globals = globals };
      Assert.That(IrVerifier.Verify(module), Is.Empty, "input " + sample);
      // Parse a separate edge-list representation; no production traversal or reachability helper.
      var text = string.Join('\n', graph.Select(pair => pair.Key + ":" + string.Join(',', pair.Value)));
      var parsed = text.Split('\n').ToDictionary(line => line.Split(':')[0], line => line.Split(':')[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
      foreach (var functionSweep in new[] { false, true }) {
        var roots = functionSweep ? exports.Append("observe").ToHashSet() : module.Functions.Select(f => f.Name).ToHashSet();
        var expected = new HashSet<string>(roots.Concat(hostRoots));
        var queue = new Queue<string>(expected);
        while (queue.TryDequeue(out var node))
          foreach (var target in parsed[node])
            if (expected.Add(target))
              queue.Enqueue(target);

        var result = Run(module, functions: functionSweep, globalRoots: hostRoots).Module;
        Assert.That(result.Functions.Select(f => f.Name), Is.EquivalentTo(module.Functions.Where(f => expected.Contains(f.Name)).Select(f => f.Name)), "functions " + sample);
        Assert.That(result.Globals.Select(g => g.Name), Is.EquivalentTo(globals.Where(g => expected.Contains(g.Name)).Select(g => g.Name)), "globals " + sample);
        var before = IrInterpreter.Instantiate(module);
        var after = IrInterpreter.Instantiate(result);
        foreach (var name in exports)
          for (var input = -1; input < 7; ++input) {
            Assert.That(after.Call(name, input), Is.EqualTo(before.Call(name, input)), $"sample {sample} {name}({input})");
            Assert.That(after.Call("observe"), Is.EqualTo(before.Call("observe")), $"effect trace {sample} {name}({input})");
          }
      }
    }
    TestContext.Out.WriteLine("Seed 383: 2000 joint graphs, both function-sweep modes, eight inputs per exported entry; zero survivor or execution mismatches.");
  }

}
