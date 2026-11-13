using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// Compares the interpreter running the imported fixtures with what <c>node</c> did with their
/// original source. Node's outcomes are recorded beside the fixtures; the acceptance tests run node
/// again, hold the recordings to it and run the same calls through the JavaScript emitted from the IR.
/// </summary>
[TestFixture]
public sealed class JavaScriptDifferentialTests {

  private static readonly Dictionary<string, IrModule> _modules = JavaScriptFixture.Names.ToDictionary(name => name, JavaScriptFixture.Import);

  private static readonly string _opCodes = File.ReadAllText(JavaScriptFixture.OpCodesPath);

  /// <summary>
  /// Test design 1 to 7: the published vectors, each operator on each type at its boundaries - shift
  /// counts 0, 1, 31, 32, 33 and -1 among them -, each statement and each array access inside its
  /// array have the outcome in the interpreter that node recorded for the original.
  /// </summary>
  [TestCaseSource(typeof(JavaScriptFixture), nameof(JavaScriptFixture.Names))]
  public void Interpreter_AgreesWithNodeOnTheOriginal(string fixture) {
    var calls = JavaScriptCases.For(fixture);
    var recorded = JavaScriptFixture.RecordedLines(fixture);
    var interpreted = calls.Select(call => JavaScriptFixture.Line(call, PrototypeFixture.InterpretedOutcome(_modules[fixture], call))).ToArray();
    var mismatches = interpreted.Zip(recorded).Where(pair => pair.First != pair.Second).Select(pair => $"interpreter {pair.First}, node {pair.Second}").ToList();

    Assert.That(calls, Is.Not.Empty);
    Assert.That(recorded, Has.Length.EqualTo(calls.Count), "the recording does not belong to these calls");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>Every function a fixture exports is called at least once, so none is compared on nothing.</summary>
  [TestCaseSource(typeof(JavaScriptFixture), nameof(JavaScriptFixture.Names))]
  public void EveryPublicFunction_IsCalled(string fixture) {
    var called = JavaScriptCases.For(fixture).Select(call => call.Method).ToHashSet();
    Assert.That(_modules[fixture].Functions.Where(function => function.IsPublic).Select(function => function.Name), Is.SubsetOf(called));
  }

  /// <summary>Test design 1: the published check values.</summary>
  [TestCase("adler32", "compute", "Wikipedia", 0x11E60398u)]
  [TestCase("crc32", "compute", "123456789", 0xCBF43926u)]
  [TestCase("fnv1a", "hash", "a", 0xE40C292Cu)]
  public void Checksum_MatchesItsPublishedValue(string fixture, string function, string text, uint published) {
    var call = new PrototypeCall(function, [PrototypeCases.Text(text)]);
    Assert.That(PrototypeFixture.InterpretedOutcome(_modules[fixture], call), Is.EqualTo(PrototypeFixture.Outcome(published, call.Arguments)));
    Assert.That(JavaScriptFixture.RecordedLines(fixture), Does.Contain(JavaScriptFixture.Line(call, PrototypeFixture.Outcome(published, call.Arguments))));
  }

  /// <summary>Scenario outline: Cipher's rotates import and behave like the original.</summary>
  [TestCase("RotL32", 0x80000001u, 1, 3u)]
  [TestCase("RotL32", 0x12345678u, 8, 878082066u)]
  [TestCase("RotL32", 0x12345678u, 32, 305419896u)]
  [TestCase("RotR32", 0x00000003u, 1, 2147483649u)]
  [TestCase("RotR32", 0x12345678u, 0, 305419896u)]
  public void CiphersRotates_ImportAndBehaveLikeTheOriginal(string function, uint value, int positions, uint expected) {
    var result = JavaScriptFrontend.Import("OpCodes.js", _opCodes, [function]);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(result.Module!.Functions.Select(imported => (imported.Name, imported.IsPublic)), Is.EqualTo(new[] { (function, true) }));
    Assert.That(IrInterpreter.Call(result.Module, function, [value, positions]), Is.EqualTo(expected));
  }

  /// <summary>Scenario: An unknown export name is an error.</summary>
  [Test]
  public void UnknownExportName_IsAnError() {
    var result = JavaScriptFrontend.Import("OpCodes.js", _opCodes, ["NoSuchFunction", "RotL32", "AlsoMissing"]);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.EqualTo(new[] {
      "OpCodes.js(1,1): error PLY1104: the file has no function 'NoSuchFunction' to export",
      "OpCodes.js(1,1): error PLY1104: the file has no function 'AlsoMissing' to export"
    }));
  }

  /// <summary>
  /// Test design 8 and the caveats: where JavaScript and the imported code differ on purpose, the
  /// interpreter does what the test says, node did what it recorded, and the import announces it.
  /// </summary>
  [TestCaseSource(typeof(JavaScriptCases), nameof(JavaScriptCases.Differences))]
  public void Difference_IsAnnouncedByACaveat(object boxed) {
    var difference = (JavaScriptDifference)boxed;
    var result = JavaScriptFrontend.Import(difference.Fixture + ".mjs", JavaScriptFixture.SourceOf(difference.Fixture));
    Assert.That(PrototypeFixture.InterpretedOutcome(result.Module!, difference.Call), Is.EqualTo(difference.Imported));
    Assert.That(difference.Node, Is.Not.EqualTo(difference.Imported));
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Does.Contain(difference.Caveat));
  }

  /// <summary>Test design 8: the differences cover reads and writes of an empty and a one-element array at -1 and at the length.</summary>
  [Test]
  public void Differences_CoverTheEdgesOfAnArray() {
    var edges = JavaScriptCases.Differences().Where(difference => difference.Imported == PrototypeFixture.Trap(IrTrap.IndexOutOfRange)).ToList();
    Assert.That(edges.Select(difference => difference.Call.Method).Distinct(),
      Is.EquivalentTo(new[] { "readByte", "readInt", "readUint", "readCoerced", "writeByte", "writeInt", "writeUint" }));
    Assert.That(edges.Select(difference => (((Array)difference.Call.Arguments[0]).Length, (int)difference.Call.Arguments[1])).Distinct(),
      Is.EquivalentTo(new[] { (0, -1), (0, 0), (1, -1), (1, 1) }));
  }

  /// <summary>
  /// Test design 14: which functions of the pinned <c>OpCodes.js</c> import and which are refused
  /// why, function by function, equals the committed list. Run with <c>POLYPILER_RECORD=1</c> to
  /// write the list after the importer changed.
  /// </summary>
  [Test, Category("Slow")]
  public void OpCodes_ImportsFunctionByFunctionAsExpected() {
    var names = Regex.Matches(_opCodes, @"(?m)^    (\w+): function ?\(").Select(match => match.Groups[1].Value).ToList();
    var lines = new List<string>();
    foreach (var name in names) {
      var result = JavaScriptFrontend.Import("OpCodes.js", _opCodes, [name]);
      lines.AddRange(result.Module is null
        ? result.Diagnostics.Select(diagnostic => $"{name}\t{diagnostic}")
        : [$"{name}\t{JavaScriptFixture.Imported}"]);
    }

    if (Environment.GetEnvironmentVariable("POLYPILER_RECORD") == "1")
      File.WriteAllText(JavaScriptFixture.OpCodesExpectationPath, string.Join('\n', lines) + "\n");

    var expected = JavaScriptFixture.OpCodesExpectation();
    Assert.That(lines, Is.EqualTo(expected.Select(line => $"{line.Function}\t{line.Status}")));
    Assert.That(expected.Where(line => line.Status == JavaScriptFixture.Imported).Select(line => line.Function), Does.Contain("RotL32").And.Contain("RotR32"));
  }

  /// <summary>
  /// Test design 14: the whole file without an export list fails, with one diagnostic for every
  /// function it refuses - the same ones the function-by-function list names.
  /// </summary>
  [Test]
  public void OpCodes_AsAWholeIsRefusedFunctionByFunction() {
    var result = JavaScriptFrontend.Import("OpCodes.js", _opCodes);
    var expected = JavaScriptFixture.OpCodesExpectation();
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Caveats, Is.Empty);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()),
      Is.EquivalentTo(expected.Select(line => line.Status).Where(status => status != JavaScriptFixture.Imported).Distinct()));
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Line, diagnostic.Column)), Is.Ordered.And.Unique);

    // The functions that import one by one also import together, and are all there is beside the refused ones.
    var imported = expected.Where(line => line.Status == JavaScriptFixture.Imported).Select(line => line.Function).ToList();
    var together = JavaScriptFrontend.Import("OpCodes.js", _opCodes, imported);
    Assert.That(together.Module!.Functions.Select(function => function.Name), Is.EquivalentTo(imported));
    Assert.That(IrVerifier.Verify(together.Module), Is.Empty);
  }

}
