using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Interop;

/// <summary>
/// Async functions (#1330) and native calls (#1332) together. A native call is synchronous: it runs to its end
/// where it stands, so an async method may make one between its awaits, in C# as P/Invoke and in JavaScript
/// through the binding. Its effects are outside the module, so a function that makes one is not isolated, and a
/// <c>Task.WhenAll</c> of it is refused as one whose order depends on timing.
/// </summary>
[TestFixture]
public sealed class AsyncForeignTests {

  /// <summary>An async class that calls <c>abs</c> of the C library of the running operating system before and after an await.</summary>
  private static string Source(string sum) => $$"""
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;

    public static class Native {

      [DllImport("{{InteropFixtures.Library}}", EntryPoint = "abs")]
      public static extern int Abs(int x);

      public static async Task<int> Half(int x) {
        await Task.CompletedTask;
        return Abs(x) / 2;
      }

      public static async Task<int> Sum(int a, int b) {
        {{sum}}
      }

    }
    """;

  private const string Sequential = "var half = await Half(a);\n    return half + Abs(b);";

  private static IrModule? _module;

  private static IrModule Module => _module ??= Imported(Source(Sequential));

  private static IrModule Imported(string source) {
    var result = CSharpFrontend.Import("Native.cs", source);
    Assert.That(result.Diagnostics, Is.Empty);
    return result.Module!;
  }

  [Test]
  public void Import_KeepsTheNativeCallInsideTheAsyncFunction() {
    var module = Module;
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var half = module.Functions.Single(function => function.Name == "Half");
    Assert.That(half.ReturnType, Is.EqualTo(IrType.Async(IrType.I32)));
    Assert.That(IrTree.Nodes(half.Body).OfType<IrForeignCall>().Select(call => call.Function), Is.EqualTo(new[] { "Abs" }));
    Assert.That(IrAsyncFunctions.Isolated(module), Does.Not.Contain("Half").And.Not.Contain("Sum"));
  }

  /// <summary>Every profile with async methods compiles the P/Invoke inside them and, run here, awaits what the C library computes; below net4.6 async is refused.</summary>
  [TestCase("net4.6")]
  [TestCase("net10.0")]
  [Category("Slow")]
  public void CSharp_AwaitsTheNativeCall(string name) {
    var profile = CSharpProfile.Parse(name);
    var text = LegalizedCSharp.Emit(Module, profile).Single().Content;
    Assert.That(InteropBuild.Complaints(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    Assert.That(text, Does.Contain("async").And.Contain("Abs(x)"), text);
    var translated = InteropBuild.Compile(text);
    Assert.That(AsyncFixtures.Awaited(InteropFixtures.Invoke(InteropFixtures.Method(translated, "Sum"), -8, -3)), Is.EqualTo("7"));
  }

  [Test]
  public void CSharp_BelowNet46_RefusesTheAsyncFunctionNotTheNativeCall() {
    var problems = LegalizedCSharp.Check(Module, CSharpProfile.Parse("net2.0")).Select(diagnostic => diagnostic.ToString()).ToList();
    Assert.That(problems, Is.Not.Empty.And.All.Contains("PLY1053"));
  }

  /// <summary>JavaScript calls the binding inside the async function: natively from ES2017, inside the generator <c>$async</c> drives at ES2015.</summary>
  [TestCase("es2015", "$async(")]
  [TestCase("es2017", "async function Half(x)")]
  [TestCase("es2022", "async function Half(x)")]
  public void JavaScript_CallsTheBindingInsideTheAsyncFunction(string level, string form) {
    var profile = JavaScriptProfile.Parse(level, "esm", null) with { Bindings = new Dictionary<string, string> { [InteropFixtures.Library] = "./libc-bindings.mjs" } };
    Assert.That(LegalizedJavaScript.Check(Module, profile), Is.Empty);
    var text = LegalizedJavaScript.Emit(Module, profile).Single().Content;
    Assert.That(text, Does.Contain(form).And.Contain(".abs(x)").And.Contain("Abs(b)").And.Contain("from \"./libc-bindings.mjs\""), text);
  }

  /// <summary>A Task.WhenAll of async methods that call native code: their native effects would interleave by timing.</summary>
  [Test]
  public void WhenAll_OfFunctionsThatCallNativeCode_IsRefused() {
    var result = CSharpFrontend.Import("Native.cs", Source("var halves = await Task.WhenAll(Half(a), Half(b));\n    return halves[0] + halves[1];"));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Has.Some.Contains("Task.WhenAll of 'Half', which writes more than its own locals, whose order depends on timing"));
  }

  /// <summary>A native call and the error a native call left are effects outside the module: neither function is isolated.</summary>
  [Test]
  public void ForeignCallAndLastError_AreNotIsolated() {
    var native = new IrFunction("N", false, IrType.I32, [], [], new IrBlock([new IrReturn(new IrForeignCall("abs", IrType.I32, [new IrConst(IrType.I32, -1L)]))]));
    var error = new IrFunction("E", false, IrType.I32, [], [], new IrBlock([new IrReturn(new IrLastError(true))]));
    var plain = new IrFunction("P", false, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1L))]));
    Assert.That(IrAsyncFunctions.Isolated(new IrModule("M", [native, error, plain])), Is.EquivalentTo(new[] { "P" }));
  }

}
