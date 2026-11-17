using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Optimize.Interprocedural;
using Polypiler.Core.Verification.Generators;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Optimize;

/// <summary>O0381 removes unreachable functions, including cycles, after the other passes.</summary>
[TestFixture]
public sealed class DeadFunctionEliminationTests {

  private static IrFunction Function(string name, bool exported, params string[] calls)
    => new(name, exported, IrType.I32, [], [], new IrBlock([
      .. calls.Select(call => (IrStmt)new IrExprStmt(new IrCall(call, IrType.I32, []))),
      new IrReturn(new IrConst(IrType.I32, 7))
    ]));

  private static OptimizationResult Run(IrModule module, bool disabled = false, OptimizationLevel level = OptimizationLevel.Default)
    => IrPassPipeline.Default.Run(module, new OptimizerOptions {
      Verify = true, Level = level,
      Disabled = IrPassPipeline.Default.Ids.Where(id => id != "O0381" || disabled).ToHashSet()
    });

  [Test]
  public void DeadCycleAndChainDisappearButReachableBodiesAreUnchanged() {
    var module = new IrModule("M", [Function("main", true, "Used"), Function("Used", false),
      Function("Unused", false, "AlsoUnused"), Function("AlsoUnused", false, "Unused"),
      Function("Self", false, "Self"), Function("a", false, "b"), Function("b", false, "c"), Function("c", false)]);
    var result = Run(module);
    Assert.That(result.Module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "main", "Used" }));
    Assert.That(result.Module.Functions[0], Is.SameAs(module.Functions[0]));
    Assert.That(result.Applications.Select(application => application.Function), Is.EqualTo(new[] { "Unused", "AlsoUnused", "Self", "a", "b", "c" }));
    Assert.That(IrInterpreter.Call(result.Module, "main", []), Is.EqualTo(IrInterpreter.Call(module, "main", [])));
  }

  [TestCase(false, OptimizationLevel.None)]
  [TestCase(true, OptimizationLevel.Default)]
  public void DisabledOrNoneKeepsEveryFunction(bool disabled, OptimizationLevel level) {
    var module = new IrModule("M", [Function("main", true), Function("unused", false)]);
    var result = Run(module, disabled, level);
    Assert.That(result.Module, Is.SameAs(module));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(3)]
  public void NoRootsRemovesAllFunctions(int count) {
    var module = new IrModule("M", Enumerable.Range(0, count).Select(index => Function("f" + index, false)).ToArray());
    var result = Run(module);
    Assert.That(result.Module.Functions, Is.Empty);
    Assert.That(result.Applications.Count, Is.EqualTo(count));
  }

  [Test]
  public void AllExportsStayWithoutAReport() {
    var module = new IrModule("M", [Function("api", true), Function("roundtrip", true, "helper"), Function("helper", false)]);
    var result = Run(module);
    Assert.That(result.Module, Is.SameAs(module));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
  }

  [Test]
  public void ForeignDeclarationsRemainWhenEveryInternalFunctionIsRemoved() {
    var module = new IrModule("M", [Function("orphan", false)]) {
      ForeignFunctions = [new IrForeignFunction("print", false, "native", IrType.I32, [])]
    };
    var result = Run(module);
    Assert.That(result.Module.Functions, Is.Empty);
    Assert.That(result.Module.ForeignFunctions, Is.SameAs(module.ForeignFunctions));
    Assert.That(result.Applications.Select(application => application.Function), Is.EqualTo(new[] { "orphan" }));
  }

  [Test]
  public void ModuleAndGlobalInitializersAreRoots() {
    var start = new IrFunction("start", false, IrType.Void, [], [], new IrBlock([new IrExprStmt(new IrCall("setup", IrType.I32, [])), new IrReturn(null)]));
    var global = new IrGlobal("state", IrType.I32, new IrCall("initialValue", IrType.I32, []));
    var module = new IrModule("M", [start, Function("setup", false), Function("initialValue", false, "dependency"), Function("dependency", false), Function("orphan", false)]) {
      Initializer = "start", Globals = [global]
    };
    var result = Run(module);
    Assert.That(result.Module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "start", "setup", "initialValue", "dependency" }));
    Assert.That(result.Module.Globals, Is.SameAs(module.Globals));
  }

  [TestCase(true)]
  [TestCase(false)]
  public void FunctionReferencesAreFollowedOnlyFromLiveBodies(bool live) {
    var signature = IrType.Function(new IrFunctionType([], IrType.I32));
    var holder = new IrFunction("holder", live, signature, [], [], new IrBlock([
      new IrReturn(new IrFunctionRef(signature, "target"))
    ]));
    var module = new IrModule("M", [holder, Function("target", false)]);
    var result = Run(module);
    Assert.That(result.Module.Functions.Count, Is.EqualTo(live ? 2 : 0));
  }

  [Test]
  public void AReferenceInsideALambdaOrAnUntakenBranchKeepsItsTarget() {
    var lambda = IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(new IrCall("target", IrType.I32, []))]));
    var main = new IrFunction("main", true, IrType.I32, [], [], new IrBlock([
      new IrIf(new IrConst(IrType.Bool, 0), new IrBlock([new IrExprStmt(lambda)]), null),
      new IrReturn(new IrConst(IrType.I32, 7))
    ]));
    var module = new IrModule("M", [main, Function("target", false), Function("orphan", false)]);
    var result = Run(module);
    Assert.That(result.Module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "main", "target" }));
  }

  [Test]
  public void InlinedHelpersDisappearAndTheirNoticesRemain() {
    var module = new IrModule("M", [Function("main", true, "helper", "helper"), Function("helper", false)]) {
      Implementations = [("helper", "Library.Helper")]
    };
    var result = IrPassPipeline.Default.Run(module, new OptimizerOptions {
      Verify = true, Disabled = IrPassPipeline.Default.Ids.Where(id => id is not ("O0006" or "O0381")).ToHashSet()
    });
    Assert.That(result.Module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "main" }));
    Assert.That(result.Module.Implementations, Is.Empty);
    Assert.That(result.Module.Inlined, Does.Contain("Library.Helper"));
    Assert.That(result.Notes.Single(), Is.EqualTo("O0381 applied: @helper removed (2 instructions): unreachable from exports, tests and taken addresses [proof: structural]"));
    Assert.That(IrInterpreter.Call(result.Module, "main", []), Is.EqualTo(IrInterpreter.Call(module, "main", [])));
  }

  [Test]
  public void IndirectCallThroughAGlobalKeepsTheFactoryAndTheHandler() {
    var signature = IrType.Function(IrType.I32);
    var global = new IrGlobal("dispatch", signature, new IrCall("factory", signature, []));
    var factory = new IrFunction("factory", false, signature, [], [], new IrBlock([new IrReturn(new IrFunctionRef(signature, "handler"))]));
    var main = new IrFunction("main", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrInvoke(IrType.I32, new IrGlobalRef(global), []))]));
    var module = new IrModule("M", [main, factory, Function("handler", false), Function("orphan", false)]) { Globals = [global] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Run(module);
    Assert.That(result.Module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "main", "factory", "handler" }));
    Assert.That(IrInterpreter.Instantiate(result.Module).Call("main"), Is.EqualTo(IrInterpreter.Instantiate(module).Call("main")));
    var reachable = ModuleReachability.Compute(result.Module);
    Assert.That(reachable.Roots["handler"], Is.EqualTo("global dispatch"));
    Assert.That(reachable.LiveGlobals, Does.Contain(global));
  }

  [Test]
  public void RetainedClassesAndInterfacesKeepTheirBodiesAndSignatures() {
    var module = PrototypeFixture.Import("Snippet.cs", "public static class Snippet { public interface I { int F(); } public class C : I { public int F() => Helper(); private static int Helper() => 7; } private static int Orphan() => 8; }");
    var result = Run(module);
    var members = module.Types.SelectMany(type => type switch {
      IrClassType c => c.Functions,
      IrInterfaceType i => i.Functions,
      _ => []
    }).ToHashSet();
    Assert.That(members, Is.Not.Empty);
    Assert.That(members.IsSubsetOf(result.Module.Functions.Select(function => function.Name)), Is.True);
    Assert.That(result.Module.Functions.Any(function => function.Name.Contains("Orphan", StringComparison.Ordinal)), Is.False);
    Assert.That(result.Module.Functions.Any(function => function.Name.Contains("Helper", StringComparison.Ordinal)), Is.True);
  }

  [Test]
  public void NullArgumentsAreRejected() {
    var module = new IrModule("M", []);
    var pass = new DeadFunctionElimination();
    Assert.Multiple(() => {
      Assert.That(() => ModuleReachability.Compute(null!), Throws.ArgumentNullException);
      Assert.That(() => pass.Run(null!, _ => true, (_, _) => { }), Throws.ArgumentNullException);
      Assert.That(() => pass.Run(module, null!, (_, _) => { }), Throws.ArgumentNullException);
      Assert.That(() => pass.Run(module, _ => true, null!), Throws.ArgumentNullException);
      Assert.That(() => pass.Run(module, _ => true, (_, _) => { }, null!), Throws.ArgumentNullException);
    });
  }

  [Test]
  public void AdditionalTestRootsKeepTheirCalleesAndSweepIgnoresTheFunctionScope() {
    var type = new IrStructType("Record");
    type.Define([]);
    var module = new IrModule("M", [Function("roundtrip", false, "helper"), Function("helper", false), Function("orphan", false)]) {
      Globals = [new IrGlobal("uninitialized", IrType.I32, null)], Types = [type]
    };
    var roots = new[] { "roundtrip", "roundtrip", "external" };
    var live = ModuleReachability.Compute(module, roots);
    Assert.That(live.Roots["helper"], Is.EqualTo("entry roundtrip"));
    Assert.That(live.LiveGlobals.Count, Is.EqualTo(1));
    var result = new DeadFunctionElimination().Run(module, _ => false, (_, _) => { }, _ => { }, roots);
    Assert.That(result.Functions.Select(function => function.Name), Is.EqualTo(new[] { "roundtrip", "helper" }));
    var empty = new DeadFunctionElimination().Run(module, _ => false, (_, _) => { });
    Assert.That(empty.Functions, Is.Empty);
  }

  [Test]
  public void SourceSizesAreRecordedForEveryBackend() {
    var before = new IrModule("M", [Function("main", true, "Used"), Function("Used", false),
      Function("Unused", false, "AlsoUnused"), Function("AlsoUnused", false, "Unused")]);
    var after = Run(before).Module;
    foreach (var (name, emit) in new (string, Func<IrModule, string>)[] {
      ("C#", module => string.Concat(CSharpEmitter.Emit(module).Select(file => file.Content))),
      ("JavaScript", module => string.Concat(JavaScriptEmitter.Emit(module).Select(file => file.Content))),
      ("Python", module => string.Concat(PythonEmitter.Emit(module).Select(file => file.Content))),
      ("Rust", module => string.Concat(RustEmitter.Emit(module, RustProfile.Default).Select(file => file.Content)))
    }) {
      var original = emit(before);
      var optimized = emit(after);
      TestContext.Out.WriteLine($"{name}: {System.Text.Encoding.UTF8.GetByteCount(original)} -> {System.Text.Encoding.UTF8.GetByteCount(optimized)} source bytes");
      Assert.That(optimized, Is.Not.Empty);
    }
  }

  [Test]
  public void RustCratePreparationKeepsTheTestFunctionsGivenToItsEmitter() {
    var module = new IrModule("M", [Function("api", true), Function("helper", false),
      new IrFunction("roundtrip", false, IrType.Void, [], [], new IrBlock([
        new IrExprStmt(new IrCall("helper", IrType.I32, [])), new IrReturn(null)
      ]))]);
    var files = RustCrates.Emit(module, options: new RustEmitOptions { TestFunctions = ["roundtrip"] },
      disabled: IrPassPipeline.Default.Ids.Where(id => id != "O0381").ToHashSet());
    var source = string.Concat(files.Select(file => file.Content));
    Assert.That(source, Does.Contain("#[test]").And.Contain("fn roundtrip()").And.Contain("fn helper()"));
  }

  /// <summary>Seed 381: an independent BFS over a textual edge list, then eight executions per root.</summary>
  [Test, Category("Slow")]
  public void TwoThousandGraphsHaveTheExpectedSurvivorsAndOutcomes() {
    var random = new SeededRandom(381);
    for (var sample = 0; sample < 2000; ++sample) {
      var count = 3 + random.NextBelow(18);
      var roots = Enumerable.Range(0, count).Where(_ => random.NextBelow(4) == 0).ToHashSet();
      var edges = Enumerable.Range(0, count).Select(_ => Enumerable.Range(0, random.NextBelow(4)).Select(_ => random.NextBelow(count)).ToArray()).ToArray();
      var references = Enumerable.Range(0, count).Select(_ => random.NextBoolean() ? random.NextBelow(count) : -1).ToArray();
      var text = string.Join('\n', Enumerable.Range(0, count).Select(index => $"{index}:{string.Join(',', edges[index].Concat(references[index] < 0 ? [] : new[] { references[index] }))}"));
      var graph = text.Split('\n').ToDictionary(line => int.Parse(line.Split(':')[0]), line => line.Split(':')[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray());
      var expected = new HashSet<int>(roots);
      var queue = new Queue<int>(roots);
      while (queue.TryDequeue(out var from))
        foreach (var to in graph[from])
          if (expected.Add(to))
            queue.Enqueue(to);

      var parameter = new IrLocal("n", IrType.I32);
      var state = new IrGlobal("state", IrType.I32, new IrConst(IrType.I32, 0));
      var functions = Enumerable.Range(0, count).Select(index => {
        var statements = new List<IrStmt> {
          new IrGlobalAssign(state, new IrBinary(IrBinaryOp.Add, new IrGlobalRef(state), new IrConst(IrType.I32, index + 1)))
        };
        if (references[index] >= 0)
          statements.Add(new IrExprStmt(new IrFunctionRef(IrType.Function(IrType.I32, IrType.I32), "f" + references[index])));
        statements.Add(new IrIf(new IrBinary(IrBinaryOp.Greater, new IrLocalRef(parameter), new IrConst(IrType.I32, 0)),
          new IrBlock([.. edges[index].Select(target => (IrStmt)new IrExprStmt(new IrCall("f" + target, IrType.I32, [new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(parameter), new IrConst(IrType.I32, 1))])))]), null));
        // A possible trap after the calls also checks that preceding global writes are preserved.
        statements.Add(new IrReturn(new IrBinary(IrBinaryOp.Divide, new IrConst(IrType.I32, index + 1), new IrLocalRef(parameter))));
        return new IrFunction("f" + index, roots.Contains(index), IrType.I32, [parameter], [], new IrBlock(statements));
      }).ToArray();
      var read = new IrFunction("ReadState", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrGlobalRef(state))]));
      var module = new IrModule("M", [.. functions, read]) { Globals = [state] };
      var result = Run(module);
      Assert.That(result.Module.Functions.Select(function => function.Name), Is.EquivalentTo(expected.Select(index => "f" + index).Append("ReadState")), $"sample {sample}\n{text}");
      foreach (var root in roots)
        foreach (var input in new[] { -1, 0, 1, 2, 3, 4, 5, 6 }) {
          (string, object?) Outcome(IrModule candidate) {
            var instance = IrInterpreter.Instantiate(candidate);
            string value;
            try { value = Convert.ToString(instance.Call("f" + root, input), System.Globalization.CultureInfo.InvariantCulture)!; } catch (IrException exception) { value = "trap " + exception.Class.Name; }
            return (value, instance.Call("ReadState"));
          }
          Assert.That(Outcome(result.Module), Is.EqualTo(Outcome(module)), $"sample {sample}, root {root}, input {input}");
        }
    }
  }

}
