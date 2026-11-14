using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/jit-shift-xor.feature</c>: the generated C# is compiled in
/// Release and run on this runtime, in its first tier and fully optimized, and compared with the
/// interpreter (#1310).
/// </summary>
[Binding, Scope(Feature = "Generated C# is immune to the JIT's shift-xor rotate defect")]
public sealed class ShiftXorRotationSteps(PrototypeScenario scenario) {

  private string _text = "";

  /// <summary>Translates the method to C# with the options of the step, which name the target.</summary>
  [When("^it is translated with \"--to csharp (.*)\" and the output is compiled in Release and executed on .NET 10$")]
  public void TranslatedAndCompiled(string options) => this._text = scenario.Translate(scenario.Source, "csharp", "csharp", [.. CommandLine.Split(options)]).Text;

  /// <summary>
  /// At count 0 the result is what C# computes, and on every count it is the interpreter's: in the
  /// first tier and fully optimized, with overflow checking off and on.
  /// </summary>
  [Then(@"^F\(0x12345678, 0\) returns 0 and the result equals the interpreter for every n in (\d+)\.\.(\d+)$")]
  public void ComputesCSharpsResult(int first, int last) {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(scenario.Text));
    foreach (var fullyOptimized in new[] { false, true })
      foreach (var checking in new[] { false, true }) {
        var build = GeneratedCSharp.Compile(this._text, checking, fullyOptimized);
        Assert.That(build.Outcome(new PrototypeCall("F", [0x12345678u, 0])), Is.EqualTo("0"));
        var mismatches = Enumerable.Range(first, last - first + 1).Select(n => new PrototypeCall("F", [0x12345678u, n]))
          .Where(call => build.Outcome(call) != PrototypeFixture.InterpretedOutcome(module, call)).Select(call => call.ToString());
        Assert.That(mismatches, Is.Empty, $"fully optimized {fullyOptimized}, CheckOverflow {checking}");
      }
  }

  /// <summary>
  /// The expression is written as the source wrote it, and the table without the row of #1310 writes
  /// the module the same way.
  /// </summary>
  [Then("^the generated expression is unchanged by this issue$")]
  public void ExpressionIsUnchanged() {
    var text = scenario.Translate(scenario.Source, "csharp", "csharp", "--opt", "none").Text;
    Assert.That(text, Does.Contain("(x << 8) | (x >> 24)"));
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(scenario.Text));
    var unguarded = new CSharpCapabilities(CSharpCapabilities.Default.Rows.Where(row => row.Defect is null));
    Assert.That(CSharpEmitter.Emit(module, CSharpProfile.Default, null, unguarded).Single().Content, Is.EqualTo(CSharpEmitter.Emit(module).Single().Content));
  }

}
