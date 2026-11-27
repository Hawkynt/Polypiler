using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Go;
using Polypiler.Core.Frontends.Go;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using System.Text.Json;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Frontends.Go.Support;

namespace Polypiler.Tests.Acceptance.Go;

/// <summary>
/// The Go layer (tooling/ci/layers/go.yaml): every prototype fixture the Go backend writes is a module
/// that <c>gofmt -l</c> lists no file of and <c>go vet</c> is silent on, and that has, call for call
/// under the pinned <c>go</c>, the outcome the C# compiler's build of it and the IR interpreter have;
/// every integer operation on every width agrees with the bit-by-bit reference; a Go source imported by
/// the Go frontend and written back by the Go backend computes what the interpreter computes; a fixture
/// outside the subset is refused with a code of the Go backend.
/// </summary>
[TestFixture, Layer("go")]
[Category("EndToEnd")]
public sealed class GoDifferentialTests {

  [TestCaseSource(typeof(PrototypeFixture), nameof(PrototypeFixture.Names))]
  [RequiresToolchain("go")]
  public void Go_Interpreter_AndCompiledFixture_Agree(string fixture) {
    var module = PrototypeFixture.Imported(fixture);
    var calls = PrototypeCases.For(fixture);
    Compare(fixture, GoFixtures.Translate(module), calls, call => PrototypeFixture.CompiledOutcome(fixture, call), call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  /// <summary>Every integer operation on every width on the boundaries of its arguments and on random tuples (seed 2150), against the bit-by-bit reference, in the oldest and the newest profile.</summary>
  [TestCase("go1.18"), TestCase("go1.27")]
  [RequiresToolchain("go")]
  public void IntegerOperations_AgreeWithTheReference(string profile) {
    var module = IntegerCases.Module(IntegerCases.All);
    var generator = new Random(2150);
    var cases = new Dictionary<PrototypeCall, IntegerCase>(ReferenceEqualityComparer.Instance);
    foreach (var @case in IntegerCases.All)
      foreach (var call in IntegerCases.Calls(@case, generator, 40))
        cases[call] = @case;

    var translated = GoFixtures.Translate(module, GoProfile.Parse(profile));
    Assert.That(translated.Problems, Is.Empty, "every integer operation has a Go form");
    Compare("integer operations", translated, [.. cases.Keys], call => IntegerCases.Expected(cases[call], call), call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  /// <summary>The checksums of the Go frontend's fixture, imported by the frontend and written back by the backend, with the published vectors.</summary>
  [Test]
  [RequiresToolchain("go")]
  public void GoSource_RoundTripsThroughTheFrontendAndTheBackend() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "frontend", "checksums.go");
    var imported = GoFrontend.Import("checksums.go", File.ReadAllText(path));
    Assert.That(imported.Diagnostics, Is.Empty);
    var module = imported.Module!;
    List<PrototypeCall> calls = [
      new("Adler32", [PrototypeCases.Text("Wikipedia")]), new("Fnv1a32", [PrototypeCases.Text("a")]), new("Fnv1a32", [PrototypeCases.Text("foobar")]),
      new("AddWrap", [int.MaxValue, 1]), new("ByteSum", [(byte)200, (byte)100]), new("Promote", []),
      new("ShiftLeft", [1u, 32UL]), new("ShiftLeft", [1u, 31UL]), new("ShiftRight", [-8, 40UL]), new("ShiftSigned", [1u, 3L]),
      new("Quarter", []), new("ExactSum", []), new("VariableSum", [0.1, 0.2]),
      new("Divide", [int.MinValue, -1]), new("Remainder", [int.MinValue, -1]), new("Divide", [1, 0]), new("Divide", [-7, 2]),
      new("Complement", [(byte)0x0F]), new("ClearBits", [0xFFu, 0x0Fu]), new("Rotate", [0x80000001u, 1L]), new("Ones", [ulong.MaxValue]),
      new("Truncate", [-3.99]), new("Larger", [3L, 9L]), new("Classify", [7]), new("Classify", [3]), new("Classify", [9]), new("Sign", [-4]),
      new("SumTo", [100]), new("CountDown", [1000L]), new("Squares", [5L]), new("Fib", [10L]), new("FirstAbove", [new[] { 1, 5, 9 }, 4])
    ];
    var expected = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    Assert.That(expected[0], Does.StartWith(PrototypeFixture.Show(0x11E60398u) + " |"));
    Assert.That(expected[2], Does.StartWith(PrototypeFixture.Show(0xBF9CF968u) + " |"));
    Compare("checksums.go", GoFixtures.Translate(module), calls, call => expected[calls.IndexOf(call)], call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  /// <summary>
  /// The constructs of the Go frontend, run by go itself as the oracle: the interpreter of the imported
  /// module and the Go the backend writes from it have Go's outcome for every call, and the outcomes the
  /// unit tests compare with are Go's (<c>constructs.outcomes.json</c>; <c>POLYPILER_RECORD=1</c> writes them).
  /// </summary>
  [Test]
  [RequiresToolchain("go")]
  public void GoConstructs_HaveGosOwnOutcomes() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "frontend", "constructs.go");
    var source = File.ReadAllText(path);
    var imported = GoFrontend.Import("constructs.go", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    var calls = GoConstructCalls.All;
    var names = calls.Select(call => call.Method).Distinct().ToDictionary(name => name, name => name);
    var original = GoDriver.Outcomes([new("go.mod", "module constructs\n\ngo 1.27\n"), new("constructs.go", source)], names, calls, vet: false);
    if (Environment.GetEnvironmentVariable("POLYPILER_RECORD") == "1")
      File.WriteAllText(GoConstructCalls.Recorded, JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true }) + "\n");

    Assert.That(JsonSerializer.Deserialize<string[]>(File.ReadAllText(GoConstructCalls.Recorded)), Is.EqualTo(original), "the recorded outcomes are go's");
    var indexes = new Dictionary<PrototypeCall, int>(ReferenceEqualityComparer.Instance);
    for (var index = 0; index < calls.Count; ++index)
      indexes[calls[index]] = index;

    Compare("constructs.go", GoFixtures.Translate(imported.Module!), calls, call => original[indexes[call]], call => Interpreted(imported.Module!, call));
  }

  /// <summary>What the interpreter does for a call, a thrown exception that is no trap - a panic of the source - as the driver reports one.</summary>
  internal static string Interpreted(IrModule module, PrototypeCall call) {
    try {
      return PrototypeFixture.InterpretedOutcome(module, call);
    } catch (IrException exception) when (exception is not IrTrapException) {
      return "exception: " + exception.Message;
    }
  }

  private static void Compare(string fixture, GoTranslation translated, IReadOnlyList<PrototypeCall> calls, Func<PrototypeCall, string> expected, Func<PrototypeCall, string> interpreted) {
    if (translated.Files is null) {
      Assert.That(translated.Problems, Is.Not.Empty.And.All.Match(@"^error PLY21[5-7][0-9]: "), "a refusal names its construct with a code of the Go backend");
      TestContext.Out.WriteLine($"{fixture} is refused: {string.Join("; ", translated.Problems)}");
      return;
    }

    var crossing = calls.Where(call => GoDriver.Crosses(translated.Module!, call, translated.Names!)).ToList();
    var outcomes = crossing.Count == 0 ? [] : GoDriver.Outcomes(translated.Files, translated.Names!, crossing);
    var mismatches = new List<string>();
    for (var index = 0; index < crossing.Count; ++index) {
      var (oracle, reference) = (expected(crossing[index]), interpreted(crossing[index]));
      if (outcomes[index] != oracle || reference != oracle)
        mismatches.Add($"{crossing[index]}: oracle {oracle}, go {outcomes[index]}, interpreter {reference}");
    }

    TestContext.Out.WriteLine($"{fixture}: {crossing.Count} of {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {crossing.Count} calls differ\n{string.Join("\n", translated.Files.Select(file => file.Content))}");
  }

}
