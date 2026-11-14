using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// Test design 3 for JavaScript: every use of a member of the integer table, run by node as it is
/// written, and imported and run by the interpreter, has one outcome on every combination of the
/// edges of its parameters. node reports the range a <c>DataView</c> does not hold in its own words;
/// any <c>RangeError</c> of a <c>DataView</c> is the IR's index out of range.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class IntegerJavaScriptImportTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static IEnumerable<TestCaseData> Uses() => JavaScriptIntegerUses.All.Select(use => new TestCaseData(use.Member, use.Source).SetArgDisplayNames(use.Source.Split('\n')[^3].Trim() + " " + string.Join(",", use.Parameters.Select(type => type.ToString()))));

  [TestCaseSource(nameof(Uses))]
  public void Import_ComputesWhatNodeComputes(string member, string source) {
    var use = JavaScriptIntegerUses.All.First(candidate => candidate.Source == source);
    File.WriteAllText(Path.Combine(this._directory, "snippet.mjs"), source);
    var calls = JavaScriptIntegerUses.Calls(use);
    var node = Toolchain.NodeOutcomes(this._directory, "snippet.mjs", calls);
    var module = JavaScriptFixture.Import("snippet.mjs", source);
    var mismatches = Enumerable.Range(0, calls.Count)
      .Select(index => (Call: calls[index], Node: Normalized(member, node[index]), Ir: PrototypeFixture.InterpretedOutcome(module, calls[index])))
      .Where(run => run.Node != run.Ir)
      .Select(run => $"{run.Call}: node {run.Node}, IR {run.Ir}")
      .ToList();
    Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ:\n{source}");
  }

  private static string Normalized(string member, string outcome)
    => member.StartsWith("DataView.", StringComparison.Ordinal) && outcome.StartsWith("trap: ", StringComparison.Ordinal) ? "trap: index out of range" : outcome;

}
