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
/// names, and translating again prints the same text. The full matrix is the category <c>Sweep</c>,
/// which CI runs once, in the job <c>target-sweep</c>; <see cref="ParseBackSliceTests"/> is its slice in
/// every OS's test job.
/// </summary>
[TestFixture]
[Category("Slow")]
[Category("Sweep")]
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

/// <summary>
/// The representative slice of <see cref="ParseBackTests"/> (<see cref="GoldenOutputs.Slice"/>): every
/// source in one variant of each target, every variant at least once, checked the same way. It is in
/// the fast tier and runs on every OS.
/// </summary>
[TestFixture]
public sealed class ParseBackSliceTests {

  private static IEnumerable<TestCaseData> Cases()
    => GoldenOutputs.Slice.Select(item => new TestCaseData(item.Source, item.Variant.Target, item.Variant.Name).SetName($"ParseBackSlice({item.Source}, {item.Variant.Target}, {item.Variant})"));

  [TestCaseSource(nameof(Cases))]
  public void TheVariant_ParsesBackToTheTreeItWasPrintedFrom(string source, string target, string name) {
    var variant = GoldenOutputs.Variants(target).Single(variant => variant.Name == name);
    if (TargetTrees.Translate(source, variant) is not { } translated) {
      Assert.Pass("the translation refuses the source in this variant");
      return;
    }

    Assert.That(TargetTrees.ParseBackProblem(translated), Is.Null, translated.Text);
    Assert.That(TargetTrees.UnreferencedHelpers(translated), Is.Empty, "helpers nothing names");
    Assert.That(TargetTrees.Translate(source, variant)!.Text, Is.EqualTo(translated.Text), "a second translation prints differently");
  }

}
