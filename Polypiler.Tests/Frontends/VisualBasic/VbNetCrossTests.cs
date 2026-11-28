using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.VisualBasic.Net;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Backends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The Visual Basic .NET fixtures through the IR into other languages, compiled in memory: written
/// again as Visual Basic .NET by the backend, and as C# by the C# backend, each call does what the IR
/// interpreter does with the imported module.
/// </summary>
[TestFixture]
public sealed class VbNetCrossTests {

  private static IEnumerable<string> Fixtures() => VbNetCases.Names;

  private static IrModule Imported(string name) {
    var imported = VbNetFrontend.Import(name + ".vb", VbNetCases.Source(name));
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    return imported.Module!;
  }

  [TestCaseSource(nameof(Fixtures))]
  public void RoundTrip_ThroughTheBackend_ComputesWhatTheInterpreterComputes(string name) {
    var module = Imported(name);
    var translation = VbNetFixtures.Translate(module);
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    var assembly = VbNetFixtures.Load(translation.Source!);
    var mismatches = VbNetCases.For(name).Where(call => call.Method != "PastTheEnd")
      .Select(call => (call, Expected: PrototypeFixture.InterpretedOutcome(module, call), Actual: PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(assembly, call.Method), call)))
      .Where(result => result.Expected != result.Actual).Select(result => $"{result.call}: expected {result.Expected}, actual {result.Actual}").ToList();
    Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches) + "\n" + translation.Source);
  }

  [TestCaseSource(nameof(Fixtures))]
  public void ToCSharp_ComputesWhatTheInterpreterComputes(string name) {
    var module = Imported(name);
    var optimized = Optimizer.Run(module, new OptimizerOptions { Target = RuleTarget.Neutral }).Module;
    var legalized = CSharpEmitter.Legalize(optimized, () => PrecomputedImplementations.Module, CSharpProfile.Default, FloatMode.Accurate);
    var source = CSharpEmitter.Emit(IrPassPipeline.Default.Run(legalized, new OptimizerOptions()).Module).Single().Content;
    var build = GeneratedCSharp.Compile(source, false);
    var mismatches = VbNetCases.For(name).Where(call => call.Method != "PastTheEnd")
      .Select(call => (call, Expected: PrototypeFixture.InterpretedOutcome(module, call), Actual: build.Outcome(call)))
      .Where(result => result.Expected != result.Actual).Select(result => $"{result.call}: expected {result.Expected}, actual {result.Actual}").ToList();
    Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches) + "\n" + source);
  }

}
