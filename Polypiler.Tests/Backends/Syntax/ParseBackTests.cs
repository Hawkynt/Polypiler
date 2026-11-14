using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Backends.Syntax;

/// <summary>
/// Every source of the prototype fixtures, in every variant of both targets and with the target passes
/// on, is built into the target syntax tree and printed; Roslyn at the profile's language version and
/// Acornima at the profile's ECMAScript version read the text without a problem, the tree they read has
/// the structure of the tree that was printed (<see cref="SyntaxShape"/>), no helper is left that nothing
/// names, and translating again prints the same text.
/// </summary>
[TestFixture]
[Category("Slow")]
public sealed class ParseBackTests {

  private static IEnumerable<TestCaseData> Cases()
    => from source in GoldenOutputs.All
       from target in GoldenOutputs.Targets
       select new TestCaseData(source, target).SetName($"ParseBack({source}, {target})");

  [TestCaseSource(nameof(Cases))]
  public void EveryVariant_ParsesBackToTheTreeItWasPrintedFrom(string source, string target) {
    var problems = new List<string>();
    foreach (var variant in GoldenOutputs.Variants(target)) {
      if (TargetTrees.Translate(source, variant) is not { } translated)
        continue;

      if (TargetTrees.ParseBackProblem(translated) is { } problem)
        problems.Add($"{variant}: {problem}\n{translated.Text}");

      if (TargetTrees.UnreferencedHelpers(translated) is { Count: > 0 } unused)
        problems.Add($"{variant}: helpers nothing names: {string.Join(", ", unused)}");

      if (TargetTrees.Translate(source, variant)!.Text != translated.Text)
        problems.Add($"{variant}: a second translation prints differently");
    }

    Assert.That(problems.Take(3), Is.Empty);
  }

}
