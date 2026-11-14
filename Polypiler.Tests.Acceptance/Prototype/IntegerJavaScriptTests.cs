using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The integer and bit operations in generated JavaScript, run by <c>node</c>: the module of every
/// operation and width, in every language level - with and without typed arrays at ECMAScript 5 and
/// as a script - has for every call the outcome of the bit-by-bit reference of the tests. Below
/// ECMAScript 2020 the 64-bit operations run on pairs of halves, from it on on <c>BigInt</c>s, so the
/// two forms of every 64-bit helper are compared on the same calls.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class IntegerJavaScriptTests {

  /// <summary>The calls and what the reference makes of them, computed once for every profile.</summary>
  private static readonly Lazy<(IReadOnlyList<PrototypeCall> Calls, string[] Expected)> _expected = new(() => {
    var random = new Random(IntegerCases.Seed);
    var cases = IntegerCases.All.SelectMany(@case => IntegerCases.Calls(@case, random).Select(call => (Case: @case, Call: call))).ToList();
    return (cases.Select(entry => entry.Call).ToList(), cases.Select(entry => IntegerCases.Expected(entry.Case, entry.Call)).ToArray());
  });

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>One profile per language level, ECMAScript 5 with and without typed arrays, and a script.</summary>
  private static IEnumerable<ProfileCase> Profiles() => [
    new("es5", "cjs", false, null), new("es5", "cjs", true, null), new("es5", "iife", false, "Generated"),
    new("es2015", "esm", false, null), new("es2017", "cjs", false, null), new("es2020", "esm", false, null), ProfileCase.Default
  ];

  /// <summary>
  /// Test design 2, 5, 6, 7, 8, 9 and 12 for JavaScript: every operation on every width, on every
  /// combination of the boundaries of its arguments and on 1000 random tuples (seed 2026), has in
  /// <c>node</c> the outcome of the bit-by-bit reference, the traps included; and the file is at the
  /// level of its profile.
  /// </summary>
  [TestCaseSource(nameof(Profiles))]
  public void EveryOperation_ComputesTheReference(ProfileCase profile) {
    var (calls, expected) = _expected.Value;
    var module = IntegerCases.Module(IntegerCases.All);
    var file = JavaScriptEmitter.Emit(module, profile.Parsed).Single();
    Assert.That(GeneratedJavaScript.Problems(file.Content, profile.Parsed), Is.Empty);
    File.WriteAllText(Path.Combine(this._directory, file.Path), file.Content);

    var node = Toolchain.NodeOutcomes(this._directory, file.Path, calls, false, profile.Mode, profile.Global);
    var mismatches = Enumerable.Range(0, calls.Count)
      .Where(index => node[index] != expected[index])
      .Select(index => $"{calls[index]}: node {node[index]}, the reference {expected[index]}")
      .ToList();
    TestContext.Out.WriteLine($"{IntegerCases.All.Count} operations and widths as {profile}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

}
