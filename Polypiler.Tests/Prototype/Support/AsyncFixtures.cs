using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of async functions (#1330) under <c>tests/fixtures/prototype/async</c>: the C# fixture
/// <c>Pipeline.cs</c>, compiled into this assembly as the oracle, and the JavaScript fixture
/// <c>pipeline.mjs</c>, whose oracle is node.
/// </summary>
internal static class AsyncFixtures {

  /// <summary>The namespace of the C# fixture.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Async";

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "async", file);

  /// <summary>The text of a fixture file of the folder, its extension included.</summary>
  public static string SourceOf(string file) => File.ReadAllText(PathOf(file));

  /// <summary>The C# fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported() {
    var result = CSharpFrontend.Import("Pipeline.cs", SourceOf("Pipeline.cs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>The JavaScript fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule ImportedScript() {
    var result = JavaScriptFrontend.Import("pipeline.mjs", SourceOf("pipeline.mjs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A static method of the fixture class as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string method)
    => typeof(AsyncFixtures).Assembly.GetType($"{Namespace}.Pipeline", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException("Pipeline", method);

  /// <summary>The public calls of the C# fixture every implementation is compared on.</summary>
  public static IEnumerable<PrototypeCall> Calls() {
    foreach (var n in new[] { 0, 1, 4 })
      yield return new PrototypeCall("Run", [n]);

    yield return new PrototypeCall("Step", [7]);
    foreach (var method in new[] { "Completed", "Squares", "NoSquares", "OneSquare", "Faults", "UsesValued", "Both" })
      yield return new PrototypeCall(method, []);

    yield return new PrototypeCall("Observed", [true]);
    yield return new PrototypeCall("Observed", [false]);
    yield return new PrototypeCall("Valued", [1]);
  }

  /// <summary>The public calls of the JavaScript fixture, which take numbers.</summary>
  public static IEnumerable<PrototypeCall> ScriptCalls() {
    foreach (var n in new[] { 0, 1, 4 })
      yield return new PrototypeCall("run", [(double)n]);

    foreach (var function in new[] { "completed", "squares", "observed" })
      yield return new PrototypeCall(function, []);
  }

  /// <summary>
  /// What awaiting a task gives, written as an outcome: the result, or the message of the exception it
  /// faulted with. A task that is no task - a value returned as it is - is that value.
  /// </summary>
  public static string Awaited(object? result) {
    if (result is ValueTask<int> valued)
      result = valued.AsTask();

    if (result is not Task task)
      return PrototypeFixture.Show(result);

    try {
      task.GetAwaiter().GetResult();
    } catch (Exception exception) {
      return "fault: " + exception.Message;
    }

    var property = task.GetType().GetProperty("Result");
    return PrototypeFixture.Show(property is null || property.PropertyType.Name == "VoidTaskResult" ? null : property.GetValue(task));
  }

  /// <summary>What .NET computes of a call of the compiled fixture, awaited.</summary>
  public static string DotNetOutcome(PrototypeCall call) => Awaited(Compiled(call.Method).Invoke(null, call.FreshArguments()));

  /// <summary>What the interpreter computes of a call, awaited at the boundary: the result, or the message of the fault.</summary>
  public static string InterpretedOutcome(IrModule module, PrototypeCall call) {
    try {
      return PrototypeFixture.Show(IrInterpreter.Call(module, call.Method, call.FreshArguments()));
    } catch (IrException exception) {
      return "fault: " + exception.Message;
    }
  }

}
