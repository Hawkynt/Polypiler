using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>
/// The spelled-out SipHash-2-4 and xxHash32 of <c>tests/fixtures/prototype/recognize</c>: the rules
/// apply at least as often as the library-call versions in <c>bits/</c> call the library, per
/// operation, and the optimized and the unoptimized IR compute exactly what the C# compiler's build of
/// the library-call version computes - the published vectors among the calls - in the interpreter and
/// in every C# profile.
/// </summary>
[TestFixture]
public sealed class SpelledAlgorithmTests {

  private static IEnumerable<TestCaseData> Algorithms() => [
    new TestCaseData("SipHash24Spelled", "SipHash24", 18, 3).SetArgDisplayNames("SipHash24Spelled"),
    new TestCaseData("XxHash32Spelled", "XxHash32", 10, 5).SetArgDisplayNames("XxHash32Spelled")
  ];

  private static IrModule Imported(string fixture) => PrototypeFixture.Import(fixture + ".cs", RecognitionFixtures.SourceOf(fixture + ".cs"));

  [TestCaseSource(nameof(Algorithms))]
  public void Rules_ApplyAsOftenAsTheLibraryVersionCallsTheLibrary(string fixture, string library, int rotates, int loads) {
    var applications = Optimizer.Run(Imported(fixture), RecognitionFixtures.Options).Applications;
    TestContext.Out.WriteLine($"{fixture}: {string.Join(", ", applications.GroupBy(application => application.Id).Select(group => $"{group.Key} x{group.Count()}"))}");
    Assert.That(applications.Count(application => application.Id == "O0100"), Is.GreaterThanOrEqualTo(rotates));
    Assert.That(applications.Count(application => application.Id == "O0140"), Is.GreaterThanOrEqualTo(loads));
    Assert.That(applications.Select(application => application.Id).Distinct(), Is.SubsetOf(new[] { "O0100", "O0140" }));
  }

  [TestCaseSource(nameof(Algorithms))]
  public void OptimizedAndUnoptimized_ComputeWhatTheLibraryVersionComputes(string fixture, string library, int rotates, int loads) {
    var original = Imported(fixture);
    var optimized = Optimizer.Run(original, RecognitionFixtures.Options).Module;
    var calls = BitsFixtures.Calls(library);
    var mismatches = calls.Select(call => (Call: call, Expected: BitsFixtures.CompiledOutcome(library, call)))
      .Where(entry => PrototypeFixture.InterpretedOutcome(original, entry.Call) != entry.Expected || PrototypeFixture.InterpretedOutcome(optimized, entry.Call) != entry.Expected)
      .Select(entry => entry.Call.ToString()).ToList();
    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(10), Is.Empty);
  }

  [TestCaseSource(nameof(Algorithms)), Category("Slow")]
  public void Optimized_ComputesTheSameInEveryCSharpProfile(string fixture, string library, int rotates, int loads) {
    var optimized = Optimizer.Run(Imported(fixture), RecognitionFixtures.Options).Module;
    var calls = BitsFixtures.Calls(library).Take(40).ToList();
    var expected = calls.Select(call => BitsFixtures.CompiledOutcome(library, call)).ToList();
    var mismatches = new List<string>();
    var built = new HashSet<string>();
    foreach (var profile in CSharpProfile.All) {
      var text = CSharpEmitter.Emit(optimized, profile).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      if (!built.Add(text[text.IndexOf("\n\n", System.StringComparison.Ordinal)..]))
        continue;

      var build = GeneratedCSharp.Compile(text, false);
      mismatches.AddRange(calls.Select((call, index) => (call, index)).Where(entry => build.Outcome(entry.call) != expected[entry.index])
        .Select(entry => $"{profile.Name}: {entry.call}"));
    }

    Assert.That(mismatches.Take(10), Is.Empty);
  }

  [TestCaseSource(nameof(Algorithms))]
  public void Optimized_IsWrittenAtEveryJavaScriptLevel(string fixture, string library, int rotates, int loads) {
    var optimized = Optimizer.Run(Imported(fixture), RecognitionFixtures.Options).Module;
    foreach (var level in new[] { "es5", "es2015", "es2017", "es2020", "es2022" }) {
      var profile = JavaScriptProfile.Parse(level);
      Assert.That(JavaScriptEmitter.Check(optimized, profile), Is.Empty, level);
      Assert.That(GeneratedJavaScript.Problems(JavaScriptEmitter.Emit(optimized, profile).Single().Content, profile), Is.Empty, level);
    }
  }

}
