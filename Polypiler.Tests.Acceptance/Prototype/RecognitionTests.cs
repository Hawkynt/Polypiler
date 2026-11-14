using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/recognition.feature</c> with the real <c>polypiler</c>
/// executable, every C# output built for its framework and for this runtime, every JavaScript output
/// parsed at its level and run by node; and the spelled-out SipHash and xxHash in every profile of
/// both backends with optimization on and off (test design 3), translated in process.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class RecognitionTests {

  private static readonly bool[] _overflowChecking = [false, true];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a file of the working directory with the real executable: the file written, its text, everything the command printed and how long it took.</summary>
  private (string Path, string Text, string Printed, TimeSpan Took) Translate(string source, string from, string to, params string[] further)
    => this.Translate(false, source, from, to, further);

  /// <summary>Translates a file of the working directory in process or with the real executable: the file written, its text, everything the command printed and how long it took.</summary>
  private (string Path, string Text, string Printed, TimeSpan Took) Translate(bool inProcess, string source, string from, string to, params string[] further) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    var watch = Stopwatch.StartNew();
    var (exitCode, printed) = Toolchain.Polypiler(inProcess, this._directory, ["translate", source, "--from", from, "--to", to, "--output", output, .. further]);
    Assert.That(exitCode, Is.Zero, printed);
    var file = Directory.GetFiles(output).Single(path => Path.GetFileName(path) != "polypiler.manifest.json");
    return (file, File.ReadAllText(file), printed, watch.Elapsed);
  }

  private string Copy(string fixture) {
    File.Copy(Path.Combine(RecognitionFixtures.Directory, fixture), Path.Combine(this._directory, fixture));
    return fixture;
  }

  /// <summary>What one C# or JavaScript profile makes of a source for the calls, by build.</summary>
  private Dictionary<string, IReadOnlyList<string>> OneProfile(bool inProcess, string source, TargetCase target, IReadOnlyList<PrototypeCall> calls, params string[] further) {
    var outcomes = new Dictionary<string, IReadOnlyList<string>>();
    TimeSpan took;
    if (target.CSharp is { } profile) {
      var (_, text, _, time) = this.Translate(inProcess, source, "csharp", "csharp", ["--profile", profile.Name, .. further]);
      took = time;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      foreach (var checking in _overflowChecking)
        outcomes[$"{profile.Name}, CheckOverflow {checking}"] = [.. calls.Select(GeneratedCSharp.Compile(text, checking).Outcome)];
    } else {
      var script = target.JavaScript!;
      var (file, text, _, time) = this.Translate(inProcess, source, "csharp", "javascript", [.. script.Arguments, .. further]);
      took = time;
      Assert.That(GeneratedJavaScript.Problems(text, script.Parsed), Is.Empty, script.ToString());
      outcomes[script.ToString()] = Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), calls, false, script.Mode, script.Global);
    }

    TestContext.Out.WriteLine($"{source} {target} {string.Join(" ", further)}: {outcomes.Count} runs, translated in {took.TotalMilliseconds:0} ms");
    return outcomes;
  }

  /// <summary>
  /// Test design 3: the spelled-out SipHash-2-4 and xxHash32 in every profile of both backends, with
  /// optimization on and off, give what the library-call versions give as the C# compiler built
  /// them - the published vectors among the calls - and the rules apply at least as often as the
  /// library-call versions call the library.
  /// </summary>
  [TestCaseSource(nameof(SpelledAlgorithms))]
  public void SpelledAlgorithm_ComputesItsVectorsInEveryProfile(string fixture, string library, int rotates, int loads, string level, TargetCase target) {
    var source = this.Copy(fixture);
    var calls = library == "SipHash24"
      ? new PrototypeCall[] {
        new("Hash", [BitsFixtures.Counting(16), BitsFixtures.Counting(15)]) { Returns = typeof(ulong) },
        new("Hash", [BitsFixtures.Counting(16), Array.Empty<byte>()]) { Returns = typeof(ulong) },
        new("Hash", [BitsFixtures.Counting(16), BitsFixtures.Counting(63)]) { Returns = typeof(ulong) },
        new("Hash", [BitsFixtures.Counting(15), BitsFixtures.Counting(4)]) { Returns = typeof(ulong) }
      }
      : [
        new("Hash", [Array.Empty<byte>(), 0u]),
        new("Hash", [Encoding.ASCII.GetBytes("abc"), 0u]),
        new("Hash", [Encoding.ASCII.GetBytes("Nobody inspects the spammish repetition"), 0u]),
        new("Hash", [BitsFixtures.Counting(37), 2654435761u])
      ];
    var expected = calls.Select(call => BitsFixtures.CompiledOutcome(library, call)).ToList();
    var printed = this.Translate(true, source, "csharp", "javascript", "--explain-opt").Printed;
    Assert.That(Regex.Matches(printed, " O0100 ").Count, Is.GreaterThanOrEqualTo(rotates));
    Assert.That(Regex.Matches(printed, " O0140 ").Count, Is.GreaterThanOrEqualTo(loads));
    var outcomes = this.OneProfile(true, source, target, calls, "--opt", level);
    Assert.That(outcomes.Where(run => !run.Value.SequenceEqual(expected)).Select(run => $"{level}, {run.Key}: {string.Join("; ", run.Value)}"), Is.Empty);
  }

  /// <summary>Both spelled-out algorithms, with optimization on and off, in every profile of both backends.</summary>
  private static IEnumerable<TestCaseData> SpelledAlgorithms()
    => from algorithm in new[] { ("SipHash24Spelled.cs", "SipHash24", 18, 3), ("XxHash32Spelled.cs", "XxHash32", 10, 5) }
       from level in new[] { "default", "none" }
       from target in TargetCase.All
       select new TestCaseData(algorithm.Item1, algorithm.Item2, algorithm.Item3, algorithm.Item4, level, target);

}
