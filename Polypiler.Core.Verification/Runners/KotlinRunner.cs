using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>The diagnostics of the Kotlin runner, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class KotlinRunnerDiagnostics {

  /// <summary><c>PLY1691</c>: the Kotlin compiler rejected the input. Argument: its messages with file and line.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Rejected = new("PLY1691", DiagnosticSeverity.Error, ExitCode.TargetBuildFailure, "the Kotlin toolchain rejected the input: {0}", "kotlin.runner.compile");

  /// <summary><c>PLY1692</c>: the driver or the test run crashed. Arguments: the exit code, standard error.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Crashed = new("PLY1692", DiagnosticSeverity.Error, ExitCode.OracleFailure, "the Kotlin driver died with exit code {0}: {1}", "kotlin.runner.crash");

  /// <summary><c>PLY1694</c>: the detail of a call whose outcome differs. Arguments: the call, the expected and the actual outcome.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Mismatch = new("PLY1694", DiagnosticSeverity.Error, ExitCode.OracleFailure, "call {0}: expected {1}, actual {2}", "kotlin.runner.mismatch");

  /// <summary><c>PLY1695</c>: a protocol error: an image that does not fit the signature, a signature that contradicts the declaration, an export the unit does not define.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Protocol = new("PLY1695", DiagnosticSeverity.Error, ExitCode.OracleFailure, "{0}", "kotlin.runner.protocol");

}

/// <summary>How the Kotlin runner builds and runs a unit.</summary>
/// <param name="CompilerOptions">Further options of <c>kotlinc</c>, such as a profile's language version and JVM target.</param>
/// <param name="WarningsAsErrors">Whether a warning fails the build, <c>-Werror</c>.</param>
/// <param name="DriverLimits">The limits of one run of the driver.</param>
public sealed record KotlinBuildOptions(IReadOnlyList<string> CompilerOptions, bool WarningsAsErrors, RunLimits DriverLimits) {

  /// <summary>No further options, warnings as errors, the default limits.</summary>
  public static KotlinBuildOptions Default { get; } = new([], true, RunLimits.Default);

}

/// <summary>
/// The language runner of Kotlin (#426): builds a unit with the pinned <c>kotlinc</c> on the pinned
/// JDK, its generated driver (<see cref="KotlinDriverGenerator"/>) beside it, into one runnable jar,
/// and runs it on the JDK's <c>java</c>, every step a plan of the cross-language runner. The build is
/// <c>kotlinc &lt;files&gt; PolypilerDriver.kt -include-runtime -d driver.jar</c> (with <c>-Werror</c>
/// where the options ask), the call <c>java -cp driver.jar &lt;package&gt;.PolypilerDriverKt inputs.hex</c>.
/// The signature of a vector file must be the one the declaration gives (<see cref="KotlinSignatures"/>),
/// else <c>PLY1695</c>. It also runs a helper jar of a frontend (<see cref="IHelperRunner"/>).
/// </summary>
public sealed class KotlinRunner : LanguageRunnerBase, IHelperRunner {

  /// <summary>The key of the Kotlin compiler in <c>tooling/toolchains.lock</c>.</summary>
  public const string Compiler = "kotlinc";

  /// <summary>The key of the JDK in <c>tooling/toolchains.lock</c>, whose executable is <c>javac</c>.</summary>
  public const string Jdk = "jdk";

  /// <summary>The <c>java</c> launcher of the JDK, which the resolver of the cross-language runner finds beside <c>javac</c>.</summary>
  public const string Java = "java";

  /// <summary>The jar a build writes.</summary>
  public const string Jar = "driver.jar";

  private const string MainFile = "main.txt";

  private readonly ICrossLanguageRunner _runner;
  private readonly string _workspace;
  private readonly IReadOnlyDictionary<string, string> _environment;

  /// <summary>Creates a runner.</summary>
  /// <param name="runner">Runs the plans; its resolver resolves <see cref="Compiler"/>, <see cref="Jdk"/> and <see cref="Java"/>.</param>
  /// <param name="workspace">The folder each build's jar is kept in.</param>
  /// <param name="options">How units are built and run.</param>
  /// <param name="environment">Further variables of every step, such as <c>JAVA_HOME</c> of the pinned JDK for <c>kotlinc</c>.</param>
  public KotlinRunner(ICrossLanguageRunner runner, string workspace, KotlinBuildOptions? options = null, IReadOnlyDictionary<string, string>? environment = null) {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(workspace);
    this._runner = runner;
    this._workspace = workspace;
    this.Options = options ?? KotlinBuildOptions.Default;
    this._environment = environment ?? new Dictionary<string, string>(StringComparer.Ordinal);
  }

  /// <summary>How units are built and run.</summary>
  public KotlinBuildOptions Options { get; }

  /// <inheritdoc />
  public override string Language => "kotlin";

  /// <inheritdoc />
  public override IReadOnlyList<string> Toolchains => [Compiler, Jdk];

  /// <summary>
  /// The class of the JVM that holds the top-level functions of a Kotlin file, as the compiler names
  /// it: the file name with its first letter in upper case, every character that cannot be in an
  /// identifier replaced by <c>_</c>, and <c>Kt</c> appended; inside the file's package.
  /// </summary>
  /// <param name="file">The file name.</param>
  /// <param name="package">The file's package, or <see langword="null"/>.</param>
  /// <returns>The qualified class name.</returns>
  public static string FileClass(string file, string? package) {
    ArgumentNullException.ThrowIfNull(file);
    var name = new string([.. Path.GetFileNameWithoutExtension(file).Select(character => char.IsLetterOrDigit(character) ? character : '_')]);
    var simple = char.ToUpperInvariant(name[0]) + name[1..] + "Kt";
    return package is null ? simple : package + "." + simple;
  }

  /// <inheritdoc />
  public override CompileResult Compile(RunnerInput input) {
    ArgumentNullException.ThrowIfNull(input);
    var sources = input.Files.Where(file => file.Key.EndsWith(".kt", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal).ToList();
    var text = string.Join('\n', sources.Select(file => Encoding.UTF8.GetString(file.Value.Span)));
    var package = KotlinSignatures.PackageOf(text);
    string driver;
    try {
      var derived = KotlinSignatures.Derive(text, input.Export);
      if (!KotlinSignatures.Agree(input.Signature, derived))
        return Refused($"the signature {input.Signature} contradicts the declaration of '{input.Export}', which gives {derived}");

      driver = KotlinDriverGenerator.Generate(package, input.Export, input.Signature);
    } catch (ArgumentException exception) {
      return Refused(exception.Message);
    }

    var files = new Dictionary<string, ReadOnlyMemory<byte>>(input.Files, StringComparer.Ordinal) { [KotlinDriverGenerator.FileName] = Encoding.UTF8.GetBytes(driver) };
    var arguments = new List<string>([.. sources.Select(file => file.Key), KotlinDriverGenerator.FileName, "-include-runtime", .. this.Options.CompilerOptions]);
    if (this.Options.WarningsAsErrors)
      arguments.Add("-Werror");

    arguments.AddRange(["-d", Jar]);
    var build = new ProcessSpec(Compiler, [.. arguments]) { Environment = this._environment };
    var plan = new RunPlan(files, [build], new ProcessSpec(Java, "-version") { Environment = this._environment }, RunLimits.Default, input.Trust) { Collect = [Jar] };
    var result = this._runner.RunAsync(plan).GetAwaiter().GetResult();
    var directory = Path.Combine(this._workspace, Guid.NewGuid().ToString("N"));
    if (result.Outcome == RunOutcome.Completed) {
      Directory.CreateDirectory(directory);
      File.WriteAllBytes(Path.Combine(directory, Jar), result.Collected[Jar].ToArray());
      var main = sources.Count > 0 ? FileClass(sources[0].Key, package) : KotlinDriverGenerator.MainClass(package);
      File.WriteAllLines(Path.Combine(directory, MainFile), [KotlinDriverGenerator.MainClass(package), main]);
    }

    IReadOnlyList<Diagnostic> diagnostics = result.Outcome == RunOutcome.BuildFailed
      ? [.. result.Diagnostics, Diagnostic.Create(KotlinRunnerDiagnostics.Rejected, null, Messages(result))]
      : result.Diagnostics;
    return new(result.Outcome, directory, diagnostics, result);

    CompileResult Refused(string message) {
      var refusal = new RunResult(RunOutcome.BuildFailed, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, []);
      return new(RunOutcome.BuildFailed, "", [Diagnostic.Create(KotlinRunnerDiagnostics.Protocol, null, message)], refusal);
    }
  }

  /// <summary>The compiler's messages of a failed build: the lines of its output that name a file and a line.</summary>
  private static string Messages(RunResult result) {
    var cause = string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Cause).Where(cause => !string.IsNullOrEmpty(cause)));
    var lines = cause.Split('\n').Where(line => line.Contains(": error: ", StringComparison.Ordinal) || line.StartsWith("error: ", StringComparison.Ordinal)).ToList();
    return lines.Count > 0 ? string.Join("; ", lines) : cause.Trim();
  }

  /// <summary>Runs the <c>main</c> of the unit's first file with the arguments.</summary>
  /// <param name="compiled">The build.</param>
  /// <param name="arguments">The program's arguments.</param>
  /// <returns>What the program did.</returns>
  public override RunResult Execute(CompileResult compiled, IReadOnlyList<string> arguments) {
    ArgumentNullException.ThrowIfNull(compiled);
    ArgumentNullException.ThrowIfNull(arguments);
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [Jar] = File.ReadAllBytes(Path.Combine(compiled.Directory, Jar)) };
    var main = File.ReadAllLines(Path.Combine(compiled.Directory, MainFile))[1];
    var step = new ProcessSpec(Java, ["-cp", Jar, main, .. arguments]) { Environment = this._environment };
    return this._runner.RunAsync(new RunPlan(files, [], step, RunLimits.Default, TrustLevel.Trusted)).GetAwaiter().GetResult();
  }

  /// <inheritdoc />
  public override TestReport RunTests(string projectDirectory)
    => throw new NotSupportedException("running kotlin.test classes needs the JUnit Platform console launcher, which tooling/toolchains.lock does not pin yet");

  /// <inheritdoc />
  protected override RunResult RunDriver(string directory, string export, CallSignature signature, ReadOnlyMemory<byte> inputs, TrustLevel trust) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) {
      [Jar] = File.ReadAllBytes(Path.Combine(directory, Jar)),
      ["inputs.hex"] = inputs
    };
    var main = File.ReadAllLines(Path.Combine(directory, MainFile))[0];
    var result = this._runner.RunAsync(new RunPlan(files, [], new ProcessSpec(Java, "-cp", Jar, main, "inputs.hex") { Environment = this._environment }, this.Options.DriverLimits, trust)).GetAwaiter().GetResult();
    if (result.Outcome != RunOutcome.Completed || result.ExitCode == 0)
      return result;

    var stderr = Encoding.UTF8.GetString(result.Stderr.Span).Trim();
    var diagnostic = result.ExitCode == 2
      ? Diagnostic.Create(KotlinRunnerDiagnostics.Protocol, null, $"the driver could not read an input image of '{export}' under {signature}")
      : Diagnostic.Create(KotlinRunnerDiagnostics.Crashed, null, result.ExitCode.ToString(CultureInfo.InvariantCulture), stderr) with { Cause = stderr };
    return result with { Diagnostics = [.. result.Diagnostics, diagnostic] };
  }

  /// <inheritdoc />
  public RunResult RunHelper(string helperPath, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> stdin) {
    ArgumentNullException.ThrowIfNull(helperPath);
    ArgumentNullException.ThrowIfNull(arguments);
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [Path.GetFileName(helperPath)] = File.ReadAllBytes(helperPath) };
    var step = new ProcessSpec(Java, ["-jar", Path.GetFileName(helperPath), .. arguments], stdin) { Environment = this._environment };
    return this._runner.RunAsync(new RunPlan(files, [], step, RunLimits.Default, TrustLevel.Trusted)).GetAwaiter().GetResult();
  }

}
