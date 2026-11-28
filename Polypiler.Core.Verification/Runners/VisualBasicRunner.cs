using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>The diagnostics of the classic Visual Basic runner, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class VisualBasicRunnerDiagnostics {

  /// <summary><c>PLY2691</c>: LibreOffice Basic gave no answer: a module it cannot compile, or an error no handler caught, stops it at a dialog. Argument: the outcome of the run.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Rejected = new("PLY2691", DiagnosticSeverity.Error, ExitCode.TargetBuildFailure, "LibreOffice Basic did not answer ({0}): a module it cannot compile or an uncaught run-time error stops it at a dialog", "visualbasic.runner.compile");

  /// <summary><c>PLY2692</c>: the office process ended without its answer. Arguments: the exit code, standard error.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Crashed = new("PLY2692", DiagnosticSeverity.Error, ExitCode.OracleFailure, "the LibreOffice Basic driver exited with code {0}: {1}", "visualbasic.runner.crash");

  /// <summary><c>PLY2695</c>: a protocol error: the driver answered another number of calls, or a line that is no answer.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Protocol = new("PLY2695", DiagnosticSeverity.Error, ExitCode.OracleFailure, "{0}", "visualbasic.runner.protocol");

}

/// <summary>One call of a driver: statements that prepare it, then the expression whose value is printed.</summary>
/// <param name="Setup">Statements written before the result, each a line of Visual Basic, such as <c>Dim a(0 To 1) As Byte</c>.</param>
/// <param name="Result">An expression whose value <c>CStr</c> prints.</param>
public sealed record VisualBasicCase(IReadOnlyList<string> Setup, string Result) {

  /// <summary>A call without preparation.</summary>
  /// <param name="result">The expression printed.</param>
  public VisualBasicCase(string result) : this([], result) { }

}

/// <summary>What one call printed: its value as <c>CStr</c> writes it, or the number of the run-time error it raised.</summary>
/// <param name="Value">The text of the value, or <see langword="null"/> where the call raised an error.</param>
/// <param name="Error">The <c>Err.Number</c> of the error, or <see langword="null"/>.</param>
public sealed record VisualBasicOutcome(string? Value, int? Error) {

  /// <summary>The trap class of the error, or <see langword="null"/> for a call that returned.</summary>
  public TrapClass? Trap => this.Error is { } number ? VisualBasicRunner.Classify(number) : null;

  /// <summary>The outcome as the driver printed it: the value, or <c>!</c> and the error number.</summary>
  public override string ToString() => this.Error is { } number ? "!" + number.ToString(CultureInfo.InvariantCulture) : this.Value!;

}

/// <summary>What the calls of a driver did: one outcome per call in order, or the diagnostics of why there is none.</summary>
/// <param name="Outcomes">The outcomes; <see langword="null"/> when the office gave no answer.</param>
/// <param name="Diagnostics">Why there are no outcomes.</param>
public sealed record VisualBasicExecution(IReadOnlyList<VisualBasicOutcome>? Outcomes, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Runs classic Visual Basic standard modules on the oracle of ADR 2600: LibreOffice Basic in its VBA
/// compatibility mode (<c>Option VBASupport 1</c>), started headless through the pinned toolchain
/// <c>libreoffice</c> as one plan of the cross-language runner. The working directory is the home of
/// the run: the modules and a generated driver are written into the office's user profile there as
/// the <c>Standard</c> library, the driver is started by its macro URL and writes one line per call
/// to <c>out.txt</c>, which the plan collects. Each call runs in a function of its own under
/// <c>On Error GoTo</c>, so a run-time error is an outcome, not a dialog. The module is loaded as the
/// Visual Basic editor imports a <c>.bas</c> file: its <c>Attribute</c> lines are dropped.
/// </summary>
public sealed class VisualBasicRunner {

  /// <summary>The key of the toolchain in <c>tooling/toolchains.lock</c>; its executable is <c>soffice</c>.</summary>
  public const string Toolchain = "libreoffice";

  /// <summary>The name of the driver's module in the library.</summary>
  public const string DriverModule = "PlyDriver";

  /// <summary>The file the driver writes its answers to, relative to the working directory.</summary>
  public const string AnswerFile = "out.txt";

  /// <summary>The folder of the office's user profile, relative to the working directory, which is the run's home.</summary>
  public const string ProfileFolder = ".config/libreoffice/4/user";

  /// <summary>The longest a run may take; a module the office cannot compile stops it at a dialog until then.</summary>
  public static TimeSpan WallTime { get; } = TimeSpan.FromSeconds(60);

  private readonly ICrossLanguageRunner _runner;

  /// <summary>Creates a runner.</summary>
  /// <param name="runner">Runs the plans.</param>
  public VisualBasicRunner(ICrossLanguageRunner runner) => this._runner = runner ?? throw new ArgumentNullException(nameof(runner));

  /// <summary>The trap class of a run-time error number: 6 overflow, 11 division by zero, 9 bounds, 91 null, 5 invalid argument; others by their number.</summary>
  /// <param name="number">The <c>Err.Number</c>.</param>
  /// <returns>The class.</returns>
  public static TrapClass Classify(int number) => number switch {
    6 => new(TrapKind.Overflow),
    11 => new(TrapKind.DivideByZero),
    9 => new(TrapKind.Bounds),
    91 => new(TrapKind.Null),
    5 => new(TrapKind.InvalidArgument),
    _ => new(TrapKind.Other, "VbError" + number.ToString(CultureInfo.InvariantCulture))
  };

  /// <summary>A module as LibreOffice Basic reads it: without its <c>Attribute</c> lines, after <c>Option VBASupport 1</c>.</summary>
  /// <param name="source">The text of a <c>.bas</c> file.</param>
  /// <returns>The text of the module.</returns>
  public static string Prepare(string source) {
    ArgumentNullException.ThrowIfNull(source);
    var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
      .Where(line => !line.TrimStart().StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase));
    return "Option VBASupport 1\n" + string.Join("\n", lines);
  }

  /// <summary>
  /// The driver module: <c>Main</c> opens the answer file and prints, per call, what its function
  /// returns - the value through <c>CStr</c>, or <c>!</c> and the error number.
  /// </summary>
  /// <param name="cases">The calls in order.</param>
  /// <returns>The text of the module.</returns>
  public static string Driver(IReadOnlyList<VisualBasicCase> cases) {
    ArgumentNullException.ThrowIfNull(cases);
    var text = new StringBuilder("Option VBASupport 1\nOption Explicit\n\nSub Main()\n    Dim f As Integer\n    f = FreeFile\n");
    text.Append("    Open Environ(\"HOME\") & \"/").Append(AnswerFile).Append("\" For Output As #f\n");
    for (var index = 0; index < cases.Count; ++index)
      text.Append("    Print #f, PlyCase").Append(index.ToString(CultureInfo.InvariantCulture)).Append("()\n");
    text.Append("    Close #f\nEnd Sub\n");
    for (var index = 0; index < cases.Count; ++index) {
      var name = "PlyCase" + index.ToString(CultureInfo.InvariantCulture);
      text.Append("\nFunction ").Append(name).Append("() As String\n    On Error GoTo Failed\n");
      foreach (var line in cases[index].Setup)
        text.Append("    ").Append(line).Append('\n');
      text.Append("    ").Append(name).Append(" = CStr(").Append(cases[index].Result).Append(")\n    Exit Function\nFailed:\n    ")
        .Append(name).Append(" = \"!\" & CStr(Err.Number)\nEnd Function\n");
    }

    return text.ToString();
  }

  /// <summary>The locale the office runs the modules under unless another is named: the separators of the invariant culture.</summary>
  public const string DefaultLocale = "en-US";

  /// <summary>
  /// The files of the run: the office's user profile with the modules and the driver as its
  /// <c>Standard</c> library, and the locale its settings name (<c>ooSetupSystemLocale</c>), which
  /// decides the separators <c>CStr</c> writes and <c>CDbl</c> reads - stated, never the host's.
  /// </summary>
  /// <param name="modules">The modules by name, as <c>.bas</c> text.</param>
  /// <param name="driver">The driver module's text.</param>
  /// <param name="locale">The locale, such as <c>en-US</c> or <c>de-DE</c>.</param>
  /// <returns>The files by name relative to the working directory.</returns>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files(IReadOnlyDictionary<string, string> modules, string driver, string locale = DefaultLocale) {
    ArgumentNullException.ThrowIfNull(modules);
    ArgumentNullException.ThrowIfNull(driver);
    const string doctype = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE {0} PUBLIC \"-//OpenOffice.org//DTD OfficeDocument 1.0//EN\" \"{1}\">\n";
    string Head(string root, string dtd) => string.Format(CultureInfo.InvariantCulture, doctype, root, dtd);
    var all = modules.Select(pair => (Name: pair.Key, Text: Prepare(pair.Value))).Append((Name: DriverModule, Text: driver)).ToList();
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    void Add(string path, string text) => files[ProfileFolder + "/" + path] = Encoding.UTF8.GetBytes(text);
    // The office takes a profile with this flag as one it set up itself and keeps the library in it.
    Add("registrymodifications.xcu", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<oor:items xmlns:oor=\"http://openoffice.org/2001/registry\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n"
      + "<item oor:path=\"/org.openoffice.Setup/Office\"><prop oor:name=\"ooSetupInstCompleted\" oor:op=\"fuse\"><value>true</value></prop></item>\n"
      + "<item oor:path=\"/org.openoffice.Setup/L10N\"><prop oor:name=\"ooSetupSystemLocale\" oor:op=\"fuse\"><value>" + SecurityElement.Escape(locale) + "</value></prop></item>\n</oor:items>\n");
    foreach (var kind in new[] { "script", "dialog" })
      Add($"basic/{kind}.xlc", Head("library:libraries", "libraries.dtd") + "<library:libraries xmlns:library=\"http://openoffice.org/2000/library\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">\n"
        + $" <library:library library:name=\"Standard\" xlink:href=\"$(USER)/basic/Standard/{kind}.xlb/\" xlink:type=\"simple\" library:link=\"false\"/>\n</library:libraries>\n");
    Add("basic/Standard/dialog.xlb", Head("library:library", "library.dtd") + "<library:library xmlns:library=\"http://openoffice.org/2000/library\" library:name=\"Standard\" library:readonly=\"false\" library:passwordprotected=\"false\"/>\n");
    Add("basic/Standard/script.xlb", Head("library:library", "library.dtd") + "<library:library xmlns:library=\"http://openoffice.org/2000/library\" library:name=\"Standard\" library:readonly=\"false\" library:passwordprotected=\"false\">\n"
      + string.Concat(all.Select(module => $" <library:element library:name=\"{module.Name}\"/>\n")) + "</library:library>\n");
    foreach (var (name, text) in all)
      Add($"basic/Standard/{name}.xba", Head("script:module", "module.dtd") + $"<script:module xmlns:script=\"http://openoffice.org/2000/script\" script:name=\"{name}\" script:language=\"StarBasic\">"
        + SecurityElement.Escape(text) + "</script:module>");
    return files;
  }

  /// <summary>The command that starts the driver: <c>soffice --headless --invisible --norestore --nolockcheck macro:///Standard.PlyDriver.Main</c>.</summary>
  public static ProcessSpec Command { get; } = new(Toolchain, ["--headless", "--invisible", "--norestore", "--nolockcheck", $"macro:///Standard.{DriverModule}.Main"]);

  /// <summary>
  /// Calls expressions on modules through a generated driver, in the order given: one outcome per
  /// call. An office that gives no answer - a module it cannot compile stops it at a dialog until the
  /// time limit - gives <c>PLY2691</c>; one that ends without the answer file <c>PLY2692</c>; an answer
  /// with another number of lines <c>PLY2695</c>.
  /// </summary>
  /// <param name="modules">The modules by name, as <c>.bas</c> text.</param>
  /// <param name="cases">The calls.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  /// <param name="locale">The locale the office runs under, <see cref="DefaultLocale"/> unless named.</param>
  /// <returns>The outcomes, or why there are none.</returns>
  public async Task<VisualBasicExecution> ExecuteAsync(IReadOnlyDictionary<string, string> modules, IReadOnlyList<VisualBasicCase> cases, CancellationToken cancellationToken = default, string locale = DefaultLocale) {
    ArgumentNullException.ThrowIfNull(cases);
    var files = Files(modules, Driver(cases), locale);
    var limits = new RunLimits(WallTime, RunLimits.Default.MemoryBytes, RunLimits.Default.OutputBytes, RunLimits.Default.Processes);
    var plan = new RunPlan(files, [], Command, limits, TrustLevel.Trusted) { Collect = [AnswerFile] };
    var result = await this._runner.RunAsync(plan, cancellationToken).ConfigureAwait(false);
    if (result.Outcome == RunOutcome.TimedOut)
      return new(null, [Diagnostic.Create(VisualBasicRunnerDiagnostics.Rejected, null, result.Outcome.ToString())]);
    if (result.Outcome != RunOutcome.Completed)
      return new(null, result.Diagnostics);

    if (!result.Collected.TryGetValue(AnswerFile, out var answer))
      return new(null, [Diagnostic.Create(VisualBasicRunnerDiagnostics.Crashed, null, result.ExitCode.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetString(result.Stderr.Span).Trim())]);

    return Read(Encoding.UTF8.GetString(answer.Span), cases.Count);
  }

  /// <summary>The outcomes of the answer file of a driver of a number of calls.</summary>
  /// <param name="text">The file's text.</param>
  /// <param name="calls">How many calls the driver made.</param>
  /// <returns>The outcomes, or <c>PLY2695</c>.</returns>
  public static VisualBasicExecution Read(string text, int calls) {
    ArgumentNullException.ThrowIfNull(text);
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
    if (lines.Count > 0 && lines[^1].Length == 0)
      lines.RemoveAt(lines.Count - 1);
    if (lines.Count != calls)
      return new(null, [Diagnostic.Create(VisualBasicRunnerDiagnostics.Protocol, null, $"the driver answered {lines.Count} of {calls} calls")]);

    var outcomes = new List<VisualBasicOutcome>();
    foreach (var line in lines) {
      if (line.StartsWith('!')) {
        if (!int.TryParse(line[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
          return new(null, [Diagnostic.Create(VisualBasicRunnerDiagnostics.Protocol, null, $"the driver printed '{line}', which is no error number")]);
        outcomes.Add(new(null, number));
      } else {
        outcomes.Add(new(line, null));
      }
    }

    return new(outcomes, []);
  }

}
