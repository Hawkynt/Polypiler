using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Backends.VisualBasic;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.VisualBasic;

/// <summary>
/// The classic Visual Basic frontend and backend against their oracle, LibreOffice Basic in VBA mode
/// (ADR 2600): the probe of spike #1097 prints what its evidence file records; the fixtures of
/// <c>tests/fixtures/visualbasic/classic</c> print what VB6 / VBA documents, as written and after the
/// round trip through the IR; and the prototype fixtures, translated from C#, print what the IR
/// interpreter computes for the same calls - run-time error numbers for its traps.
/// </summary>
[TestFixture, Layer("visualbasic")]
public sealed class VisualBasicOracleTests {

  private static readonly string _classic = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "classic");

  private static async Task<IReadOnlyList<string>> RunAsync(IReadOnlyDictionary<string, string> modules, IReadOnlyList<VisualBasicCase> cases) {
    var execution = await new VisualBasicRunner(new CrossLanguageRunner(Host, Toolchains)).ExecuteAsync(modules, cases);
    Assert.That(execution.Diagnostics, Is.Empty, string.Join("\n", execution.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));
    return [.. execution.Outcomes!.Select(outcome => outcome.ToString())];
  }

  private static IReadOnlyList<VisualBasicCase> Cases(string file)
    => [.. File.ReadAllLines(Path.Combine(_classic, file)).Where(line => line.Length > 0).Select(line => new VisualBasicCase(line))];

  private static string RoundTrip(string source, string file) {
    var imported = VisualBasicFrontend.Import(file, source);
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var translation = VisualBasicFixtures.Translate(imported.Module!);
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    return translation.Source!;
  }

  [Test, RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task ClassicProbe_PrintsWhatTheEvidenceRecords() {
    var probe = File.ReadAllText(Path.Combine(_classic, "ClassicProbe.bas"));
    var output = await RunAsync(new Dictionary<string, string> { ["ClassicProbe"] = probe }, [new("Replace(Probes(), Chr(10), \"|\")")]);
    var recorded = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "adr", "evidence", "2600", "ClassicProbe.libreoffice-26.8.3.1.txt"));
    Assert.That(output.Single().Split('|').Where(line => line.Length > 0), Is.EqualTo(recorded.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0)));
  }

  [TestCase("Numeric", true)]
  [TestCase("Records", false)]
  [RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task ClassicFixture_PrintsTheDocumentedValues_AsWrittenAndAfterTheRoundTrip(string fixture, bool asWritten) {
    var source = File.ReadAllText(Path.Combine(_classic, fixture + ".bas"));
    var cases = Cases(fixture + ".cases.txt");
    var expected = File.ReadAllLines(Path.Combine(_classic, fixture + ".expected.txt")).Where(line => line.Length > 0).ToList();
    // Records.bas assigns a record parameter, which LibreOffice aliases where VBA copies (ADR 2600): only the round trip is compared.
    if (asWritten)
      Assert.That(await RunAsync(new Dictionary<string, string> { [fixture] = source }, cases), Is.EqualTo(expected), "as written");
    foreach (var profile in new[] { VisualBasicProfile.Vb6, VisualBasicProfile.Vba7 }) {
      var imported = VisualBasicFrontend.Import(fixture + ".bas", source).Module!;
      var text = VisualBasicFixtures.Translate(imported, profile).Source!;
      Assert.That(await RunAsync(new Dictionary<string, string> { [fixture] = text }, cases), Is.EqualTo(expected), $"round trip, {profile}\n{text}");
    }
  }

  [Test, RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task RoundTrip_IsAlsoWhatTheIrInterpreterComputes() {
    var source = File.ReadAllText(Path.Combine(_classic, "Numeric.bas"));
    var module = VisualBasicFrontend.Import("Numeric.bas", source).Module!;
    var calls = new (string Function, object[] Arguments, string Vba)[] {
      ("IncInteger", [(short)32767], "IncInteger(32767)"), ("MulLong", [65536, 65536], "MulLong(65536, 65536)"), ("IntDiv", [-7, 2], "IntDiv(-7, 2)"),
      ("IntDiv", [int.MinValue, -1], "IntDiv(&H80000000, -1)"), ("Collatz", [97], "Collatz(97)"), ("SafeDivide", [1, 0], "SafeDivide(1, 0)"),
      ("ErrorOfOverflow", [], "ErrorOfOverflow()"), ("ArraySum", [10], "ArraySum(10)"), ("Bits", [-1, 12345], "Bits(-1, 12345)")
    };
    var expected = calls.Select(call => Interpreted(module, call.Function, call.Arguments)).ToList();
    var output = await RunAsync(new Dictionary<string, string> { ["Numeric"] = RoundTrip(source, "Numeric.bas") }, [.. calls.Select(call => new VisualBasicCase(call.Vba))]);
    Assert.That(output, Is.EqualTo(expected));
  }

  /// <summary>What the IR interpreter computes, as the driver prints it: the value, or <c>!</c> and the error number of the trap.</summary>
  private static string Interpreted(IrModule module, string function, object[] arguments) {
    try {
      return IrInterpreter.Call(module, function, arguments) switch {
        bool flag => flag ? "True" : "False",
        uint bits => unchecked((int)bits).ToString(CultureInfo.InvariantCulture),
        var value => Convert.ToString(value, CultureInfo.InvariantCulture)!
      };
    } catch (IrException exception) {
      return "!" + ErrorNumber(exception.Class).ToString(CultureInfo.InvariantCulture);
    }
  }

  /// <summary>The run-time error Visual Basic raises for an exception class of the IR.</summary>
  private static int ErrorNumber(IrClassType type) => type == IrExceptionClass.DivideByZeroException ? 11
    : type == IrExceptionClass.OverflowException ? 6
    : type == IrExceptionClass.IndexOutOfRangeException ? 9
    : type == IrExceptionClass.InvalidCastException ? 13
    : type == IrExceptionClass.NullReferenceException ? 91
    : 5;

  private static IEnumerable<TestCaseData> Prototypes() {
    yield return new TestCaseData("Statements", new[] { ("Sign", new object[] { -5 }), ("Clamp", [15, 0, 10]), ("SumTo", [100]), ("Halvings", [1000]), ("IntegerRoot", [1000]), ("Triangle", [5]), ("Factorial", [13]), ("IsEven", [10]) });
    yield return new TestCaseData("Operators", new[] {
      ("AddInt", new object[] { int.MaxValue, 1 }), ("MultiplyInt", [65537, 65537]), ("DivideInt", [int.MinValue, -1]), ("DivideInt", [1, 0]), ("RemainderInt", [-7, 2]),
      ("ShiftLeftInt", [1, 33]), ("ShiftRightInt", [-16, 2]), ("ShiftRightLogicalInt", [-16, 28]), ("NegateInt", [int.MinValue]), ("DivideUInt", [0xFFFFFFFFu, 7u]),
      ("RemainderUInt", [0xFFFFFFFFu, 10u]), ("LessUInt", [0x80000000u, 1u]), ("MultiplyUInt", [0xFFFFFFFFu, 0xFFFFFFFFu]), ("Folded", [5]), ("Mask", [0xFFFFFFFFu])
    });
  }

  [TestCaseSource(nameof(Prototypes))]
  [RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task PrototypeFixture_PrintsWhatTheIrInterpreterComputes(string fixture, (string Function, object[] Arguments)[] calls) {
    var module = PrototypeFixture.Imported(fixture);
    var translation = VisualBasicFixtures.Translate(module);
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    var expected = calls.Select(call => Interpreted(module, call.Function, call.Arguments)).ToList();
    string Literal(object value) => value switch {
      uint bits => "&H" + bits.ToString("X8", CultureInfo.InvariantCulture),
      int.MinValue => "&H80000000",
      _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
    };
    var cases = calls.Select(call => new VisualBasicCase($"{call.Function}({string.Join(", ", call.Arguments.Select(Literal))})")).ToList();
    Assert.That(await RunAsync(new Dictionary<string, string> { [fixture] = translation.Source! }, cases), Is.EqualTo(expected), translation.Source);
  }

  [TestCase("Adler32", "Wikipedia", "11E60398")]
  [TestCase("Crc32", "123456789", "CBF43926")]
  [RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task Checksum_OfAByteArray_IsItsPublishedValue(string fixture, string text, string expected) {
    var source = VisualBasicFixtures.Translate(PrototypeFixture.Imported(fixture)).Source!;
    var bytes = string.Join(", ", text.Select(c => ((int)c).ToString(CultureInfo.InvariantCulture)));
    var setup = new List<string> { "Dim d() As Byte", $"ReDim d(0 To {text.Length - 1})" };
    setup.AddRange(text.Select((c, index) => $"d({index}) = {(int)c}"));
    var output = await RunAsync(new Dictionary<string, string> { [fixture] = source }, [new(setup, "Hex(Compute(d))")]);
    Assert.That(output, Is.EqualTo(new[] { expected }), bytes);
  }

  [Test, RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task ModuleThatDoesNotCompile_IsPly2691() {
    var execution = await new VisualBasicRunner(new CrossLanguageRunner(Host, Toolchains)).ExecuteAsync(new Dictionary<string, string> { ["Broken"] = "Function F() As Long\n    F = = 1\nEnd Function\n" }, [new("F()")]);
    Assert.That(execution.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("PLY2691"));
  }

}
