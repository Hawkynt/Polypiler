using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Backends.VisualBasic.Net;
using Polypiler.Core.Frontends.VisualBasic.Net;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Backends.VisualBasic;
using Polypiler.Tests.Frontends.VisualBasic;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.VisualBasic;

/// <summary>
/// Visual Basic .NET against its oracle, the Visual Basic compiler and runtime of the pinned
/// <c>dotnet-sdk</c>, through <see cref="VbNetRunner"/>: the fixtures of <c>tests/fixtures/visualbasic/vbnet</c>
/// as written print what the IR interpreter computes from their import, and again after the round
/// trip through the backend; the prototype fixtures translated from C# print what the interpreter
/// computes, in every language version; the project the backend writes builds for the frameworks;
/// the error numbers and clearing rules of the dialect <c>vbnet</c> are what <c>ErrProbe.expected.txt</c> records.
/// </summary>
[TestFixture, Layer("visualbasic"), Category("EndToEnd")]
public sealed class VbNetOracleTests {

  private static async Task<IReadOnlyList<string>> RunAsync(IReadOnlyDictionary<string, string> sources, IReadOnlyList<VisualBasicCase> cases, string version = "16.9", bool removeIntegerChecks = true) {
    var execution = await new VbNetRunner(new CrossLanguageRunner(Host, Toolchains)).ExecuteAsync(sources, cases, version, removeIntegerChecks);
    Assert.That(execution.Diagnostics, Is.Empty, string.Join("\n", execution.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));
    return [.. execution.Outcomes!.Select(outcome => Bounds(outcome.ToString()))];
  }

  /// <summary>
  /// An outcome with the bounds trap named as the IR names it: <c>Substring</c> and <c>Array.Copy</c>
  /// report a range outside the string or array as an <c>ArgumentException</c>, which the IR's bounds
  /// trap stands for, as the prototype harness reads it.
  /// </summary>
  private static string Bounds(string outcome) => outcome is "!ArgumentOutOfRangeException" or "!ArgumentException" ? "!IndexOutOfRangeException" : outcome;

  /// <summary>A value as a Visual Basic .NET literal of its type.</summary>
  internal static string Literal(object value) => value switch {
    bool flag => flag ? "True" : "False",
    int.MinValue => "&H80000000",
    int number => number.ToString(CultureInfo.InvariantCulture),
    uint number => number.ToString(CultureInfo.InvariantCulture) + "UI",
    long.MinValue => "&H8000000000000000L",
    long number => number.ToString(CultureInfo.InvariantCulture) + "L",
    ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
    short.MinValue => "&H8000S",
    short number => number.ToString(CultureInfo.InvariantCulture) + "S",
    ushort number => number.ToString(CultureInfo.InvariantCulture) + "US",
    byte number => $"CByte({number.ToString(CultureInfo.InvariantCulture)})",
    sbyte number => $"CSByte({number.ToString(CultureInfo.InvariantCulture)})",
    char unit => $"ChrW({((int)unit).ToString(CultureInfo.InvariantCulture)})",
    double number when double.IsNaN(number) => "Double.NaN",
    double number when double.IsInfinity(number) => number > 0 ? "Double.PositiveInfinity" : "Double.NegativeInfinity",
    double number => Floating(number.ToString("R", CultureInfo.InvariantCulture)),
    float number when float.IsNaN(number) => "Single.NaN",
    float number when float.IsInfinity(number) => number > 0 ? "Single.PositiveInfinity" : "Single.NegativeInfinity",
    float number => Floating(number.ToString("R", CultureInfo.InvariantCulture)) + "F",
    string text => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
    Array array => $"New {ElementName(array.GetType().GetElementType()!)}() {{{string.Join(", ", array.Cast<object>().Select(Literal))}}}",
    _ => throw new ArgumentException($"No literal for {value.GetType()}.", nameof(value))
  };

  private static string Floating(string text) => text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";

  private static string ElementName(Type type) => Type.GetTypeCode(type) switch {
    TypeCode.Byte => "Byte",
    TypeCode.SByte => "SByte",
    TypeCode.Int16 => "Short",
    TypeCode.UInt16 => "UShort",
    TypeCode.Int32 => "Integer",
    TypeCode.UInt32 => "UInteger",
    TypeCode.Int64 => "Long",
    TypeCode.UInt64 => "ULong",
    TypeCode.Single => "Single",
    TypeCode.Double => "Double",
    TypeCode.Char => "Char",
    TypeCode.Boolean => "Boolean",
    _ => "String"
  };

  /// <summary>What the IR interpreter computes for a call, as the driver prints it: the value in the invariant culture, or <c>!</c> and the exception's name.</summary>
  internal static string Interpreted(IrModule module, PrototypeCall call) {
    try {
      return System.Convert.ToString(IrInterpreter.Call(module, call.Method, call.FreshArguments()), CultureInfo.InvariantCulture) ?? "";
    } catch (IrTrapException trap) {
      return "!" + IrExceptionClass.Of(trap.Trap).Name;
    } catch (IrException exception) {
      return "!" + exception.Class.Name;
    }
  }

  /// <summary>The case of a call: its value printed, or a procedure without one called first and the empty text printed.</summary>
  private static VisualBasicCase Case(PrototypeCall call) {
    var text = $"{VbNetNaming.Valid(call.Method, "Function")}({string.Join(", ", call.Arguments.Select(Literal))})";
    return call.Returns == typeof(void) ? new([text], "\"\"") : new(text);
  }

  private static IEnumerable<TestCaseData> Fixtures() => from name in VbNetCases.Names select new TestCaseData(name);

  [TestCaseSource(nameof(Fixtures))]
  [RequiresToolchain(VbNetRunner.Toolchain)]
  public async Task Fixture_AsWrittenAndAfterTheRoundTrip_PrintsWhatTheInterpreterComputes(string name) {
    var source = VbNetCases.Source(name);
    var module = VbNetFrontend.Import(name + ".vb", source).Module!;
    var calls = VbNetCases.For(name);
    var expected = calls.Select(call => Interpreted(module, call)).ToList();
    var cases = calls.Select(Case).ToList();
    Assert.That(await RunAsync(new Dictionary<string, string> { [name + ".vb"] = source }, cases, removeIntegerChecks: false), Is.EqualTo(expected), "as written");
    var translated = VbNetFixtures.Translate(module).Source!;
    var roundTrip = calls.Where(call => call.Method != "PastTheEnd").ToList();
    Assert.That(await RunAsync(new Dictionary<string, string> { [name + ".vb"] = translated }, [.. roundTrip.Select(Case)]),
      Is.EqualTo(roundTrip.Select(call => Interpreted(module, call))), translated);
  }

  private static IEnumerable<TestCaseData> Prototypes()
    => from fixture in PrototypeFixture.First from version in VisualBasicProfile.NetLanguageVersions select new TestCaseData(fixture, version);

  [TestCaseSource(nameof(Prototypes))]
  [RequiresToolchain(VbNetRunner.Toolchain)]
  public async Task PrototypeFixture_InEveryLanguageVersion_PrintsWhatTheInterpreterComputes(string fixture, string version) {
    var module = PrototypeFixture.Imported(fixture);
    var translation = VbNetFixtures.Translate(module, VisualBasicProfile.Parse(VisualBasicProfile.NetPrefix + version));
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    var calls = PrototypeFixture.Typed(fixture, PrototypeCases.For(fixture)).Where(call => call.Arguments.All(argument => argument is not char[])).Take(40).ToList();
    var output = await RunAsync(new Dictionary<string, string> { [fixture + ".vb"] = translation.Source! }, [.. calls.Select(Case)], version);
    Assert.That(output, Is.EqualTo(calls.Select(call => Interpreted(module, call))), translation.Source);
  }

  /// <summary>
  /// The project file the backend writes builds as it is, offline: for the framework of the SDK, whose
  /// reference assemblies come with it. The other frameworks, whose reference assemblies are packages,
  /// are compiled in memory against them by the unit tests.
  /// </summary>
  [TestCase("net10.0")]
  [RequiresToolchain(VbNetRunner.Toolchain)]
  public async Task GeneratedProject_BuildsForItsFramework(string framework) {
    var profile = VisualBasicProfile.Parse($"vbnet/vb15.5/{framework}");
    var module = PrototypeFixture.Imported("wide/Tables");
    var translation = VbNetFixtures.Translate(module, profile);
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) {
      ["Tables.vb"] = System.Text.Encoding.UTF8.GetBytes(translation.Source!),
      ["Tables.vbproj"] = System.Text.Encoding.UTF8.GetBytes(translation.Project!),
      ["NuGet.config"] = System.Text.Encoding.UTF8.GetBytes(VbNetRunner.OfflineFeed)
    };
    var build = new ProcessSpec(VbNetRunner.Toolchain, "build", "Tables.vbproj", "-c", "Release", "--nologo", "--disable-build-servers", "-v", "q", "-p:NuGetAudit=false");
    var limits = new RunLimits(VbNetRunner.WallTime, RunLimits.Default.MemoryBytes, RunLimits.Default.OutputBytes, RunLimits.Default.Processes);
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(new RunPlan(files, [build], new(VbNetRunner.Toolchain, "--version"), limits, TrustLevel.Trusted));
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), System.Text.Encoding.UTF8.GetString(result.Stdout.Span) + translation.Source);
  }

  [Test, RequiresToolchain(VbNetRunner.Toolchain)]
  public async Task ErrProbe_RecordsTheTableOfTheDialect() {
    var probe = File.ReadAllText(Path.Combine(VbNetCases.Folder, "ErrProbe.vb"));
    var table = VbNetRunner.Table(File.ReadAllText(Path.Combine(VbNetCases.Folder, "ErrProbe.expected.txt")));
    var cases = table.Select(entry => new VisualBasicCase(entry.Key.StartsWith("number:", StringComparison.Ordinal) ? $"NumberOf(\"{entry.Key["number:".Length..]}\")" : entry.Key + "()")).ToList();
    var output = await RunAsync(new Dictionary<string, string> { ["ErrProbe.vb"] = probe }, cases, removeIntegerChecks: false);
    Assert.That(VbNetRunner.Compare(table, output), Is.Empty, string.Join("\n", output));
  }

  [Test, RequiresToolchain(VbNetRunner.Toolchain)]
  public async Task ProjectThatDoesNotBuild_IsPly2690() {
    var execution = await new VbNetRunner(new CrossLanguageRunner(Host, Toolchains)).ExecuteAsync(
      new Dictionary<string, string> { ["Broken.vb"] = "Public Module Broken\n    Public Function F() As Integer\n        Return = 1\n    End Function\nEnd Module\n" }, [new("F()")]);
    Assert.That(execution.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY2690" }));
  }

}
