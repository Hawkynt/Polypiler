using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The fixtures of the integer operations: SipHash-2-4, xxHash32 and CRC-16/ARC with their published
/// vectors, and one file per family. Each is imported; the interpreter computes what the original
/// computes - the C# compiler's build of a C# fixture, the published values of the JavaScript one,
/// which node confirms in the acceptance tests - and every profile of both backends writes it.
/// </summary>
[TestFixture]
public sealed class BitsFixtureTests {

  private static readonly bool[] _overflowChecking = [false, true];

  private static readonly JavaScriptProfile[] _levels = [
    JavaScriptProfile.Parse("es5"), JavaScriptProfile.Parse("es5", typedArrays: true), JavaScriptProfile.Parse("es2015"),
    JavaScriptProfile.Parse("es2017"), JavaScriptProfile.Parse("es2020"), JavaScriptProfile.Default
  ];

  private static IEnumerable<TestCaseData> CSharp() => BitsFixtures.CSharp.Select(fixture => new TestCaseData(fixture));

  private static IEnumerable<TestCaseData> Scripts() => BitsFixtures.JavaScript.Keys.Select(fixture => new TestCaseData(fixture));

  private static string Code(string text) => text[text.IndexOf("\n\n", StringComparison.Ordinal)..];

  /// <summary>The published vectors, through the interpreter: those of the paper, the specification and the CRC catalogue.</summary>
  [TestCase("SipHash24", 16, 15, "11613035633349379557")]
  [TestCase("SipHash24", 16, 0, "8246050544436514353")]
  public void SipHash_HasThePublishedValues(string fixture, int key, int message, string expected)
    => Assert.That(PrototypeFixture.InterpretedOutcome(BitsFixtures.Imported(fixture), new PrototypeCall("Hash", [BitsFixtures.Counting(key), BitsFixtures.Counting(message)])),
      Does.StartWith(expected + " | "));

  [TestCase("", "46947589")]
  [TestCase("a", "1426945110")]
  [TestCase("abc", "852579327")]
  [TestCase("Nobody inspects the spammish repetition", "3794352943")]
  public void XxHash32_HasThePublishedValues(string text, string expected)
    => Assert.That(PrototypeFixture.InterpretedOutcome(BitsFixtures.Imported("XxHash32"), new PrototypeCall("Hash", [Encoding.ASCII.GetBytes(text), 0u])),
      Does.StartWith(expected + " | "));

  [Test]
  public void Crc16_HasThePublishedCheckValue() {
    var module = JavaScriptFixture.Import("crc16.js", File.ReadAllText(BitsFixtures.ScriptOf("crc16")));
    Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("compute", [Encoding.ASCII.GetBytes("123456789")])), Does.StartWith("47933 | "));
  }

  /// <summary>Test design 1 and 2 for the interpreter: every call of every C# fixture has the outcome of the fixture as the C# compiler built it.</summary>
  [TestCaseSource(nameof(CSharp))]
  public void CSharpFixture_IsImported_AndComputesWhatItsBuildDoes(string fixture) {
    var module = BitsFixtures.Imported(fixture);
    var calls = BitsFixtures.Calls(fixture);
    var mismatches = calls.Where(call => PrototypeFixture.InterpretedOutcome(module, call) != BitsFixtures.CompiledOutcome(fixture, call)).Select(call => call.ToString()).ToList();
    Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>Every C# fixture in every JavaScript level: no PLY1152, and nothing the level lacks.</summary>
  [TestCaseSource(nameof(CSharp))]
  public void CSharpFixture_IsWrittenAtEveryJavaScriptLevel(string fixture) {
    var module = BitsFixtures.Imported(fixture);
    foreach (var profile in _levels) {
      Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty);
      Assert.That(GeneratedJavaScript.Problems(JavaScriptEmitter.Emit(module, profile).Single().Content, profile), Is.Empty, profile.ToString());
    }
  }

  /// <summary>
  /// Test design 1 for C#: every C# fixture in every C# profile compiles for its framework and has,
  /// without and with overflow checking, the outcomes of the original build; every JavaScript fixture
  /// those of the interpreter.
  /// </summary>
  [TestCaseSource(nameof(CSharp)), Category("Slow")]
  public void CSharpFixture_ComputesTheSameInEveryCSharpProfile(string fixture) {
    var module = BitsFixtures.Imported(fixture);
    var calls = BitsFixtures.Calls(fixture);
    this.EveryProfile(module, calls, calls.Select(call => BitsFixtures.CompiledOutcome(fixture, call)).ToList());
  }

  [TestCaseSource(nameof(Scripts)), Category("Slow")]
  public void JavaScriptFixture_ComputesTheSameInEveryCSharpProfile(string fixture) {
    var module = JavaScriptFixture.Import(char.ToUpperInvariant(fixture[0]) + fixture[1..] + ".js", File.ReadAllText(BitsFixtures.ScriptOf(fixture)));
    var calls = BitsFixtures.ScriptCalls(fixture);
    this.EveryProfile(module, calls, calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList());
  }

  private void EveryProfile(Core.IR.IrModule module, IReadOnlyList<PrototypeCall> calls, List<string> expected) {
    var executed = new HashSet<string>();
    var mismatches = new List<string>();
    foreach (var profile in CSharpProfile.All) {
      var text = CSharpEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      if (!executed.Add(Code(text)))
        continue;

      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        for (var index = 0; index < calls.Count; ++index)
          if (build.Outcome(calls[index]) is var outcome && outcome != expected[index])
            mismatches.Add($"{profile.Name}, CheckOverflow {checking}: {calls[index]} is {outcome}, not {expected[index]}");
      }
    }

    Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

}
