using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Async;

/// <summary>
/// The C# backend writes async functions (#1330): native <c>async</c>/<c>await</c> from net4.6 on, a
/// <c>ValueTask</c> where the profile has it and a <c>Task</c> below with a line in the header, and a
/// refusal with PLY1053 at net2.0 and net3.5. The generated class computes what .NET computes of the fixture.
/// </summary>
[TestFixture]
internal sealed class CSharpAsyncEmissionTests {

  private static IEnumerable<TestCaseData> Admitted()
    => CSharpProfile.All.Skip(2).Select(profile => new TestCaseData(profile).SetName($"Pipeline.{profile.Name}"));

  private static string Emitted(IrModule module, CSharpProfile profile) {
    Assert.That(LegalizedCSharp.Check(module, profile), Is.Empty, profile.Name);
    return LegalizedCSharp.Emit(module, profile)[0].Content;
  }

  /// <summary>Test design 1 and 8: every admitted profile writes the fixture, it compiles at the profile, and it computes what .NET computes.</summary>
  [TestCaseSource(nameof(Admitted)), Category("Slow")]
  public void AdmittedProfile_WritesTheFixtureThatComputesWhatDotNetComputes(CSharpProfile profile) {
    var text = Emitted(AsyncFixtures.Imported(), profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    var compiled = GeneratedCSharp.Compile(text, false);
    foreach (var call in AsyncFixtures.Calls())
      Assert.That(AsyncFixtures.Awaited(compiled.Method(call.Method).Invoke(null, call.FreshArguments())), Is.EqualTo(AsyncFixtures.DotNetOutcome(call)), $"{profile.Name} {call}\n{text}");
  }

  /// <summary>Test design 8 and 14: the profiles before net4.6 refuse an async function with PLY1053, and nothing else.</summary>
  [TestCase("net2.0"), TestCase("net3.5")]
  public void ProfileWithoutAsync_RefusesTheFixture(string name) {
    var problems = LegalizedCSharp.Check(AsyncFixtures.Imported(), CSharpProfile.Parse(name));
    Assert.That(problems.Select(problem => problem.ToString()), Is.EqualTo(new[] { $"error PLY1053: 'async function' cannot be expressed in C# profile '{name}'" }));
  }

  /// <summary>A module without an async function asks nothing of the profile.</summary>
  [Test]
  public void ModuleWithoutAsync_IsNotRefused()
    => Assert.That(LegalizedCSharp.Check(new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]))]), CSharpProfile.Net20), Is.Empty);

  [Test]
  public void Net10_WritesTheNativeForms() {
    var text = Emitted(AsyncFixtures.Imported(), CSharpProfile.Net100);
    Assert.That(text, Does.Contain("public static async global::System.Threading.Tasks.Task<int> Run(int n)"));
    Assert.That(text, Does.Contain("await Step(i)"));
    Assert.That(text, Does.Contain("await global::System.Threading.Tasks.Task.CompletedTask;"));
    Assert.That(text, Does.Contain("await global::System.Threading.Tasks.Task.FromResult<int>(5)"));
    Assert.That(text, Does.Contain("await global::System.Threading.Tasks.Task.WhenAll(Sq(2), Sq(3))"));
    Assert.That(text, Does.Contain("await global::System.Threading.Tasks.Task.WhenAll<int>()"));
    Assert.That(text, Does.Contain("private static async global::System.Threading.Tasks.Task Touch()"));
    Assert.That(text, Does.Contain("public static async global::System.Threading.Tasks.ValueTask<int> Valued(int x)"));
    Assert.That(text, Does.Not.Contain("ValueTask is written as Task"));
  }

  /// <summary>Test design 9: a ValueTask at netstandard2.0 is a Task, which the header lists; at netstandard2.1 it stays.</summary>
  [TestCase("netstandard2.0", false), TestCase("netstandard2.1", true), TestCase("net4.8", false), TestCase("netcoreapp3.1", true)]
  public void ValueTask_IsWrittenWhereTheProfileHasIt(string name, bool kept) {
    var text = Emitted(AsyncFixtures.Imported(), CSharpProfile.Parse(name));
    Assert.That(text.Contains("ValueTask<int> Valued(int x)"), Is.EqualTo(kept), text);
    Assert.That(text.Contains("ValueTask is written as Task, which this profile has instead: Valued."), Is.EqualTo(!kept), text);
  }

  /// <summary>An async function that awaits nothing awaits the completed task, so that C# does not warn of it (CS1998).</summary>
  [Test]
  public void AsyncFunctionWithoutAwait_AwaitsTheCompletedTask() {
    var function = new IrFunction("F", true, IrType.Async(IrType.I32), [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]));
    var text = Emitted(new IrModule("M", [function]), CSharpProfile.Net100);
    Assert.That(text, Does.Contain("await global::System.Threading.Tasks.Task.CompletedTask;"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net10.0", "14.0"), Is.Empty, text);
    Assert.That(AsyncFixtures.Awaited(GeneratedCSharp.Compile(text, false).Method("F").Invoke(null, [])), Is.EqualTo("1"));
  }

  /// <summary>A table without Task.CompletedTask writes the completed task as Task.FromResult(0), which .NET Framework 4.5 has.</summary>
  [Test]
  public void TableWithoutCompletedTask_WritesFromResult() {
    var table = new CSharpCapabilities(CSharpCapabilities.Default.Rows.Where(row => row.Name != "Task.CompletedTask"));
    var completed = new IrAwait(new IrAsync(IrAsyncOp.Completed, IrType.Async(IrType.Void), []));
    var function = new IrFunction("F", true, IrType.Async(IrType.Void), [], [], new IrBlock([new IrExprStmt(completed)]));
    var module = new IrModule("M", [function]);
    Assert.That(LegalizedCSharp.Check(module, CSharpProfile.Net46, table), Is.Empty);
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Net46, capabilities: table)[0].Content;
    Assert.That(text, Does.Contain("await global::System.Threading.Tasks.Task.FromResult(0);"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net4.6", "6"), Is.Empty, text);
  }

  /// <summary>Every row of the async functions is in the default table.</summary>
  [Test]
  public void Table_HasTheRowsOfAsync() {
    var table = CSharpCapabilities.Default;
    Assert.That(table.Has(CSharpFeature.AsyncAwait, CSharpProfile.Net46) && !table.Has(CSharpFeature.AsyncAwait, CSharpProfile.Net35), Is.True);
    Assert.That(table.HasApi("Task.CompletedTask", CSharpProfile.Net46) && !table.HasApi("Task.CompletedTask", CSharpProfile.Net35), Is.True);
    Assert.That(table.HasApi("ValueTask<T>", CSharpProfile.NetStandard21) && !table.HasApi("ValueTask<T>", CSharpProfile.NetStandard20), Is.True);
  }

}
