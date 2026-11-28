using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>The diagnostics of the Visual Basic .NET runner, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class VbNetRunnerDiagnostics {

  /// <summary><c>PLY2690</c>: the Visual Basic compiler of the .NET SDK rejected the project. Argument: what the build printed.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Rejected = new("PLY2690", DiagnosticSeverity.Error, ExitCode.TargetBuildFailure, "the Visual Basic compiler of dotnet-sdk rejected the project: {0}", "visualbasic.net.runner.compile");

  /// <summary><c>PLY2693</c>: the driver ended without printing an answer per call. Arguments: the exit code, standard error.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Crashed = new("PLY2693", DiagnosticSeverity.Error, ExitCode.OracleFailure, "the Visual Basic .NET driver exited with code {0} without its answer: {1}", "visualbasic.net.runner.crash");

  /// <summary><c>PLY2696</c>: the recorded dialect table differs from the committed one. Argument: the first entry that differs.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TableDiffers = new("PLY2696", DiagnosticSeverity.Error, ExitCode.OracleFailure, "the recorded dialect table differs from spec/languages/visualbasic/vbnet-errors.yaml at '{0}'", "visualbasic.net.runner.table");

}

/// <summary>What one call of a Visual Basic .NET driver printed: the value in the invariant culture, or the name of the exception it threw.</summary>
/// <param name="Value">The text of the value, or <see langword="null"/> where the call threw.</param>
/// <param name="Exception">The simple name of the exception's type, such as <c>OverflowException</c>, or <see langword="null"/>.</param>
public sealed record VbNetOutcome(string? Value, string? Exception) {

  /// <summary>The trap class of the exception, or <see langword="null"/> for a call that returned.</summary>
  public TrapClass? Trap => this.Exception is { } name ? VbNetRunner.Classify(name) : null;

  /// <summary>The outcome as the driver printed it: the value, or <c>!</c> and the exception's name.</summary>
  public override string ToString() => this.Exception is { } name ? "!" + name : this.Value!;

}

/// <summary>What the calls of a driver did, one outcome per call in order, or the diagnostics of why there is none.</summary>
/// <param name="Outcomes">The outcomes; <see langword="null"/> when the project did not build or the driver gave no answer.</param>
/// <param name="Diagnostics">Why there are no outcomes.</param>
public sealed record VbNetExecution(IReadOnlyList<VbNetOutcome>? Outcomes, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Compiles and runs Visual Basic .NET with the pinned <c>dotnet-sdk</c> as one plan of the
/// cross-language runner: the source files and a generated driver, <c>PlyDriver.vb</c>, in one
/// console project, <c>PlyDriver.vbproj</c>, for the language version given, <c>Option Strict On</c>,
/// warnings errors and integer checks as the code needs them; <c>dotnet build</c>, then
/// <c>dotnet out/PlyDriver.dll</c>, which prints one line per call: the value through
/// <c>Convert.ToString</c> in the invariant culture, or <c>!</c> and the name of the exception thrown.
/// </summary>
public sealed class VbNetRunner {

  /// <summary>The key of the toolchain in <c>tooling/toolchains.lock</c>.</summary>
  public const string Toolchain = "dotnet-sdk";

  /// <summary>The name of the driver's module and project.</summary>
  public const string DriverModule = "PlyDriver";

  /// <summary>The longest a build and run may take.</summary>
  public static TimeSpan WallTime { get; } = TimeSpan.FromSeconds(300);

  private readonly ICrossLanguageRunner _runner;

  /// <summary>Creates a runner.</summary>
  /// <param name="runner">Runs the plans.</param>
  public VbNetRunner(ICrossLanguageRunner runner) => this._runner = runner ?? throw new ArgumentNullException(nameof(runner));

  /// <summary>
  /// The trap class of an exception the driver names: <c>OverflowException</c> overflow,
  /// <c>DivideByZeroException</c> division by zero, <c>IndexOutOfRangeException</c> bounds,
  /// <c>NullReferenceException</c> null, the argument exceptions an invalid argument, any other by its name.
  /// </summary>
  /// <param name="exception">The simple name of the exception's type.</param>
  /// <returns>The class.</returns>
  public static TrapClass Classify(string exception) {
    ArgumentNullException.ThrowIfNull(exception);
    return exception switch {
      "OverflowException" => new(TrapKind.Overflow),
      "DivideByZeroException" => new(TrapKind.DivideByZero),
      "IndexOutOfRangeException" => new(TrapKind.Bounds),
      "NullReferenceException" => new(TrapKind.Null),
      "ArgumentException" or "ArgumentNullException" or "ArgumentOutOfRangeException" => new(TrapKind.InvalidArgument),
      _ => new(TrapKind.Other, exception)
    };
  }

  /// <summary>The driver: <c>Main</c> prints the outcome of every call, each run in a function of its own inside <c>Try</c>.</summary>
  /// <param name="cases">The calls in order, in the form of <see cref="VisualBasicCase"/>: statements, then the expression printed.</param>
  /// <returns>The text of <c>PlyDriver.vb</c>.</returns>
  public static string Driver(IReadOnlyList<VisualBasicCase> cases) {
    ArgumentNullException.ThrowIfNull(cases);
    var text = new StringBuilder("Option Strict On\nOption Explicit On\n\nPublic Module PlyDriver\n\n    Public Sub Main()\n");
    for (var index = 0; index < cases.Count; ++index)
      text.Append("        System.Console.WriteLine(PlyCase").Append(index.ToString(CultureInfo.InvariantCulture)).Append("())\n");
    text.Append("    End Sub\n\n    Private Function PlyShow(ByVal value As Object) As String\n")
      .Append("        Return System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)\n    End Function\n");
    for (var index = 0; index < cases.Count; ++index) {
      text.Append("\n    Private Function PlyCase").Append(index.ToString(CultureInfo.InvariantCulture)).Append("() As String\n        Try\n");
      foreach (var line in cases[index].Setup)
        text.Append("            ").Append(line).Append('\n');
      text.Append("            Return PlyShow(").Append(cases[index].Result).Append(")\n        Catch ex As System.Exception\n")
        .Append("            Return \"!\" & ex.GetType().Name\n        End Try\n    End Function\n");
    }

    return text.Append("\nEnd Module\n").ToString();
  }

  /// <summary>The project of the driver: a console program on .NET 10 of the language version, every option stated, warnings errors.</summary>
  /// <param name="languageVersion">The language version, such as <c>16.9</c>.</param>
  /// <param name="removeIntegerChecks">Whether integer arithmetic wraps, as <c>/removeintchecks</c> compiles it.</param>
  /// <returns>The text of <c>PlyDriver.vbproj</c>.</returns>
  public static string Project(string languageVersion, bool removeIntegerChecks) {
    ArgumentNullException.ThrowIfNull(languageVersion);
    return "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n"
      + $"    <LangVersion>{languageVersion}</LangVersion>\n    <RootNamespace></RootNamespace>\n    <StartupObject>PlyDriver</StartupObject>\n"
      + "    <OptionExplicit>On</OptionExplicit>\n    <OptionStrict>On</OptionStrict>\n    <OptionInfer>On</OptionInfer>\n    <OptionCompare>Binary</OptionCompare>\n"
      + $"    <RemoveIntegerChecks>{(removeIntegerChecks ? "true" : "false")}</RemoveIntegerChecks>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n"
      + "    <Deterministic>true</Deterministic>\n  </PropertyGroup>\n</Project>\n";
  }

  /// <summary>The build that makes the driver, then the command that runs it.</summary>
  public static (ProcessSpec Build, ProcessSpec Run) Commands { get; } = (
    new ProcessSpec(Toolchain, "build", DriverModule + ".vbproj", "-c", "Release", "-o", "out", "--nologo", "--disable-build-servers", "-v", "q"),
    new ProcessSpec(Toolchain, "out/" + DriverModule + ".dll"));

  /// <summary>
  /// Compiles the source files with a driver of the calls and runs it: one outcome per call. A project
  /// that does not build is <c>PLY2690</c>, a driver that answers another number of lines <c>PLY2693</c>.
  /// </summary>
  /// <param name="sources">The <c>.vb</c> files by name.</param>
  /// <param name="cases">The calls.</param>
  /// <param name="languageVersion">The language version.</param>
  /// <param name="removeIntegerChecks">Whether integer arithmetic wraps.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  /// <returns>The outcomes, or why there are none.</returns>
  public async Task<VbNetExecution> ExecuteAsync(IReadOnlyDictionary<string, string> sources, IReadOnlyList<VisualBasicCase> cases, string languageVersion = "16.9",
    bool removeIntegerChecks = true, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(cases);
    var files = sources.ToDictionary(pair => pair.Key, pair => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(pair.Value), StringComparer.Ordinal);
    files[DriverModule + ".vb"] = Encoding.UTF8.GetBytes(Driver(cases));
    files[DriverModule + ".vbproj"] = Encoding.UTF8.GetBytes(Project(languageVersion, removeIntegerChecks));
    var limits = new RunLimits(WallTime, RunLimits.Default.MemoryBytes, RunLimits.Default.OutputBytes, RunLimits.Default.Processes);
    var result = await this._runner.RunAsync(new RunPlan(files, [Commands.Build], Commands.Run, limits, TrustLevel.Trusted), cancellationToken).ConfigureAwait(false);
    if (result.Outcome == RunOutcome.BuildFailed)
      return new(null, [Diagnostic.Create(VbNetRunnerDiagnostics.Rejected, null, (Encoding.UTF8.GetString(result.Stdout.Span) + Encoding.UTF8.GetString(result.Stderr.Span)).Trim())]);
    if (result.Outcome != RunOutcome.Completed)
      return new(null, result.Diagnostics);

    return Read(Encoding.UTF8.GetString(result.Stdout.Span), cases.Count, result.ExitCode, Encoding.UTF8.GetString(result.Stderr.Span));
  }

  /// <summary>The outcomes a driver of a number of calls printed.</summary>
  /// <param name="text">What it printed.</param>
  /// <param name="calls">How many calls it made.</param>
  /// <param name="exitCode">Its exit code.</param>
  /// <param name="error">What it wrote to standard error.</param>
  /// <returns>The outcomes, or <c>PLY2693</c>.</returns>
  public static VbNetExecution Read(string text, int calls, int exitCode = 0, string error = "") {
    ArgumentNullException.ThrowIfNull(text);
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
    if (lines.Count > 0 && lines[^1].Length == 0)
      lines.RemoveAt(lines.Count - 1);
    if (lines.Count != calls)
      return new(null, [Diagnostic.Create(VbNetRunnerDiagnostics.Crashed, null, exitCode.ToString(CultureInfo.InvariantCulture), $"{lines.Count} of {calls} answers; {error}".Trim())]);

    return new([.. lines.Select(line => line.StartsWith('!') ? new VbNetOutcome(null, line[1..]) : new VbNetOutcome(line, null))], []);
  }

}
