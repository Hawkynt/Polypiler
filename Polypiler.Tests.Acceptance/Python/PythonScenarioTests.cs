using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Python;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// The scenarios of the Python backend issues with the values they state, run under the pinned
/// CPython: the scalar forms of #447, the loops and switches of #444 in both switch styles, and the
/// published Adler-32 values of #445. The generated text the scenarios name is checked by the unit
/// tests of the backend; here the generated module runs.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PythonScenarioTests {

  /// <summary>The functions of the scenarios, in C#: each is the IR operation or loop a scenario names.</summary>
  private const string Source = """
    public static class Scenarios {
      public static int Add(int x, int y) => x + y;
      public static uint Mul(uint x, uint y) => x * y;
      public static int ArithmeticShift(int x, int y) => x >> y;
      public static int LogicalShift(int x, int y) => x >>> y;
      public static int Quotient(int x, int y) => x / y;
      public static int Remainder(int x, int y) => x % y;
      public static int FloorRemainder(int x, int y) => (x % y + y) % y;
      public static float AddSingle(float x, float y) => x + y;
      public static int Saturated(double x) => (int)x;
      public static double Divide(double x, double y) => x / y;
      public static int Average(int x, int y) => (x + y + 1) >> 1;
      public static int SumTo(int n) { int s = 0; for (int i = 1; i <= n; i++) { if (i % 2 == 0) s += i; else s += i; } return s; }
      public static int Up(int n) { int s = 0; for (int i = 0; i < n; i++) s = s * 10 + i; return s; }
      public static int UpTo(int n) { int s = 0; for (int i = 1; i <= n; i++) s = s * 10 + i; return s; }
      public static int Down(int n) { int s = 0; for (int i = n; i >= 1; i--) s = s * 10 + i; return s; }
      public static int Even(int n) { int s = 0; for (int i = 0; i <= 4; i += 2) s = s * 10 + i; return s + n * 0; }
      public static int PostTest(int n) { int c = 0; do { c++; } while (c < n); return c; }
      public static int Dense(int x) {
        switch (x) {
          case 0: return 10;
          case 1: case 2: return 20;
          case 3: case 4: case 5: case 6: case 7: case 8: case 9: return 30;
          default: return 40;
        }
      }
      public static int Merge(int x) { int r = 0; if (x > 0) r = 1; else for (int i = 0; i < x; i++) r = i; return r; }
      public static int PastTheEnd(int n, int m) { int c = 0; for (int i = n; i <= m; i++) { c++; if (c == 3) return i; } return c; }
      public static int BelowTheStart(int n, int m) { int c = 0; for (int i = n; i >= m; i--) { c++; if (c == 3) return i; } return c; }
    }
    """;

  private static readonly Dictionary<string, PythonTranslation> _translations = [];

  private static PythonTranslation Translated(string profile) {
    lock (_translations) {
      if (!_translations.TryGetValue(profile, out var translation))
        _translations[profile] = translation = PythonFixtures.Translate(Source, "Scenarios.cs", PythonProfile.Parse(profile));

      return translation;
    }
  }

  private static async Task<IReadOnlyList<string>> Run(string profile, params PrototypeCall[] calls) {
    var translated = Translated(profile);
    Assert.That(translated.Source, Is.Not.Null, string.Join("; ", translated.Problems));
    return await PythonDriver.OutcomesAsync(translated.Source!, [.. calls.Select(call => call with { Method = translated.Names![call.Method] })]);
  }

  /// <summary>#447, "Emitted expressions run and compute the IR result", and the pixel average of its test design.</summary>
  [Test, RequiresToolchain("python")]
  public async Task ScalarOperations_ComputeTheIrResult() {
    var outcomes = await Run("py3.14",
      new("Add", [2147483647, 1]), new("Mul", [4294967295u, 4294967295u]), new("ArithmeticShift", [-8, 33]), new("LogicalShift", [-8, 1]),
      new("Quotient", [-7, 2]), new("Remainder", [-7, 3]), new("FloorRemainder", [-7, 3]), new("AddSingle", [16777216f, 1f]),
      new("Saturated", [3000000000.0]), new("Average", [200, 101]));
    Assert.That(outcomes, Is.EqualTo(new[] { "-2147483648", "1", "-4", "2147483644", "-3", "-1", "2", "16777216", "2147483647", "151" }));
  }

  /// <summary>#447, "Floating-point division by zero follows IEEE 754 instead of raising".</summary>
  [Test, RequiresToolchain("python")]
  public async Task FloatDivisionByZero_FollowsIeee754() {
    var outcomes = await Run("py3.14", new("Divide", [1.0, 0.0]), new("Divide", [-1.0, 0.0]), new("Divide", [0.0, 0.0]), new("Quotient", [1, 0]), new("Quotient", [int.MinValue, -1]));
    Assert.That(outcomes, Is.EqualTo(new[] { "f:7ff0000000000000", "f:fff0000000000000", "NaN", "trap: divide by zero", "trap: integer overflow" }));
  }

  /// <summary>#444: the counted loops of "A counted loop becomes a range loop", the post-test loop, the zero-trip merge and the sum loop of the test design.</summary>
  [TestCase("py3.8"), TestCase("py3.14")]
  [RequiresToolchain("python")]
  public async Task Loops_KeepTheirTripCountsAndValues(string profile) {
    var outcomes = await Run(profile,
      new("Up", [4]), new("UpTo", [4]), new("Down", [4]), new("Even", [4]), new("PostTest", [0]), new("PostTest", [5]), new("Merge", [-1]), new("Merge", [3]), new("SumTo", [10]),
      new("Up", [0]), new("UpTo", [0]), new("Down", [0]));
    Assert.That(outcomes, Is.EqualTo(new[] { "123", "1234", "4321", "24", "1", "5", "0", "1", "55", "0", "0", "0" }));
    Assert.That(Translated(profile).Source, Does.Contain("for i in range(n):").And.Contain("while i <= n:").And.Contain("for i in range(n, 0, -1):").And.Contain("for i in range(0, 5, 2):"));
  }

  /// <summary>
  /// A counted loop whose inclusive bound is the end of the counter's type runs on in the IR, the
  /// counter wrapping past it, where a range would stop: the third pass sees the wrapped value.
  /// </summary>
  [Test, RequiresToolchain("python")]
  public async Task CountedLoop_ToTheEndOfTheType_WrapsAsTheIrDoes() {
    var outcomes = await Run("py3.14",
      new("PastTheEnd", [int.MaxValue - 1, int.MaxValue]), new("BelowTheStart", [int.MinValue + 1, int.MinValue]),
      new("PastTheEnd", [1, 2]), new("BelowTheStart", [2, 1]), new("PastTheEnd", [5, 4]));
    Assert.That(outcomes, Is.EqualTo(new[] { "-2147483648", "2147483647", "2", "2", "0" }));
    Assert.That(Translated("py3.14").Source, Does.Contain("while i <= m:").And.Contain("while i >= m:"));
  }

  /// <summary>#444, "A dense switch becomes a match statement" and "The same switch is emitted as a chain when match is not available".</summary>
  [TestCase("py3.14", "match x:"), TestCase("py3.9", "elif 3 <= x <= 9:")]
  [RequiresToolchain("python")]
  public async Task DenseSwitch_ChoosesTheArmOfEveryValue(string profile, string form) {
    var outcomes = await Run(profile, new("Dense", [0]), new("Dense", [2]), new("Dense", [9]), new("Dense", [10]), new("Dense", [-1]));
    Assert.That(outcomes, Is.EqualTo(new[] { "10", "20", "30", "40", "40" }));
    Assert.That(Translated(profile).Source, Does.Contain(form));
  }

  /// <summary>#445, "The emitted function matches the published Adler-32 values" (RFC 1950).</summary>
  [Test, RequiresToolchain("python")]
  public async Task Adler32_MatchesThePublishedValues() {
    var translated = PythonFixtures.Translate(PrototypeFixture.Imported("Adler32"));
    var outcomes = await PythonDriver.OutcomesAsync(translated.Source!, [
      new(translated.Names!["Compute"], [Array.Empty<byte>()]), new(translated.Names["Compute"], ["Wikipedia"u8.ToArray()]), new(translated.Names["Compute"], ["123456789"u8.ToArray()])
    ]);
    Assert.That(outcomes.Select(outcome => outcome.Split(" | ")[0]), Is.EqualTo(new[] { "1", "300286872", "152961502" }));
  }

}
