using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>The diagnostics of the C# runner, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class CSharpRunnerDiagnostics {

  /// <summary><c>PLY1091</c>: the C# compiler rejected the input. Argument: every <c>CS####</c> error with its text and location.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Rejected = new("PLY1091", DiagnosticSeverity.Error, ExitCode.TargetBuildFailure, "the C# compiler rejected the input: {0}", "csharp.runner.compile");

  /// <summary><c>PLY1092</c>: the driver process or the test host crashed. Arguments: the exit code, standard error.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Crashed = new("PLY1092", DiagnosticSeverity.Error, ExitCode.OracleFailure, "the C# driver crashed with exit code {0}: {1}", "csharp.runner.crash");

  /// <summary><c>PLY1094</c>: the detail of a mismatching call. Arguments: the call's index, the expected and the actual outcome.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Mismatch = new("PLY1094", DiagnosticSeverity.Error, ExitCode.SemanticMismatch, "call {0}: expected {1}, actual {2}", "csharp.runner.mismatch");

  /// <summary><c>PLY1095</c>: a protocol error: an image that does not fit, a signature incompatible with the declaration, an export that does not exist.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Protocol = new("PLY1095", DiagnosticSeverity.Error, ExitCode.OracleFailure, "{0}", "csharp.runner.protocol");

}

/// <summary>
/// The language runner of C#, <c>csharp</c> on the toolchain <c>dotnet-sdk</c>: the real C# compiler
/// and runtime build and run every program, each step a plan of the cross-language runner. A build is
/// <c>dotnet build -c Release</c> of the sources with a generated driver of the export
/// (<see cref="CSharpDriverGenerator"/>), restored without any package source; the driver runs as
/// <c>dotnet out/Driver.dll</c>. The sources are read with Roslyn only to find the export's
/// declaration and bind the signature to it.
/// </summary>
public sealed partial class CSharpRunner : LanguageRunnerBase {

  /// <summary>The key of the toolchain in <c>tooling/toolchains.lock</c>.</summary>
  public const string Toolchain = "dotnet-sdk";

  /// <summary>The NuGet configuration of every run: no package source, so a restore never reaches the network.</summary>
  public const string OfflineFeed = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n  </packageSources>\n</configuration>\n";

  private const string OutputFolder = "out";
  private const string ResultsFolder = "results";
  private const string FeedFolder = "feed";

  /// <summary>The NuGet configuration of a test run: the run's own feed as the only source, the packages extracted inside the run.</summary>
  private const string LocalFeed = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n"
    + "  <packageSources>\n    <clear />\n    <add key=\"run\" value=\"" + FeedFolder + "\" />\n  </packageSources>\n</configuration>\n";

  private readonly ICrossLanguageRunner _runner;
  private readonly string _workspace;
  private readonly RunLimits _buildLimits;
  private readonly RunLimits _driverLimits;
  private readonly string _packages;

  /// <summary>Creates a runner.</summary>
  /// <param name="runner">Runs the plans.</param>
  /// <param name="workspace">The directory the built programs are kept in, one directory per build.</param>
  /// <param name="buildLimits">The limits of a build and of a test run; the runner's defaults when <see langword="null"/>.</param>
  /// <param name="driverLimits">The limits of a driver run; the runner's defaults when <see langword="null"/>.</param>
  /// <param name="packages">
  /// The packages folder a test project's packages are taken from, laid out as NuGet's global packages
  /// folder; <c>NUGET_PACKAGES</c>, else <c>~/.nuget/packages</c>, when <see langword="null"/>.
  /// </param>
  public CSharpRunner(ICrossLanguageRunner runner, string workspace, RunLimits? buildLimits = null, RunLimits? driverLimits = null, string? packages = null) {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
    this._runner = runner;
    this._workspace = workspace;
    this._buildLimits = buildLimits ?? RunLimits.Default;
    this._driverLimits = driverLimits ?? RunLimits.Default;
    this._packages = packages ?? (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
      ? configured
      : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
  }

  /// <inheritdoc />
  public override string Language => "csharp";

  /// <inheritdoc />
  public override IReadOnlyList<string> Toolchains => [Toolchain];

  /// <summary>The build step of a driver project.</summary>
  public static ProcessSpec BuildStep { get; } = new(Toolchain, "build", CSharpDriverGenerator.ProjectFile, "-c", "Release", "-o", OutputFolder,
    "--nologo", "--disable-build-servers", "-v", "q", "-p:GenerateFullPaths=false");

  /// <summary>
  /// Builds the sources with the driver of the input's export: <c>PLY0030</c> with <c>PLY1091</c> when
  /// the compiler rejects the sources, <c>PLY1095</c> when the export does not exist, the profile is
  /// unknown or the signature does not fit the declaration.
  /// </summary>
  /// <param name="input">The C# sources, the export and its signature.</param>
  public override CompileResult Compile(RunnerInput input) {
    ArgumentNullException.ThrowIfNull(input);
    var directory = Path.Combine(this._workspace, Guid.NewGuid().ToString("N"));
    if (!CSharpProfile.Known.TryGetValue(input.Profile, out var profile))
      return Refused(directory, $"the profile '{input.Profile}' is not one of {string.Join(", ", CSharpProfile.Known.Keys)}");

    CSharpExportBinding binding;
    try {
      binding = CSharpSignatures.Bind(CSharpSignatures.Find(input.Files, input.Export, input.Signature.Parameters.Count), input.Signature, input.Export);
    } catch (ArgumentException exception) {
      return Refused(directory, exception.Message.Replace($" (Parameter '{exception.ParamName}')", "", StringComparison.Ordinal));
    }

    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    foreach (var (name, bytes) in input.Files)
      files[name] = bytes;
    files[CSharpDriverGenerator.ProjectFile] = Encoding.UTF8.GetBytes(CSharpDriverGenerator.Project(profile));
    files[CSharpDriverGenerator.SourceFile] = Encoding.UTF8.GetBytes(CSharpDriverGenerator.Generate(binding));
    files["NuGet.config"] = Encoding.UTF8.GetBytes(OfflineFeed);
    var plan = new RunPlan(files, [BuildStep], new(Toolchain, "--version"), this._buildLimits, input.Trust) { Collect = [OutputFolder] };
    var result = this.Run(plan);
    if (result.Outcome == RunOutcome.Completed) {
      foreach (var (name, bytes) in result.Collected) {
        var destination = Path.Combine(directory, name);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes.Span);
      }

      return new(result.Outcome, directory, [], result);
    }

    if (result.Outcome != RunOutcome.BuildFailed)
      return new(result.Outcome, directory, result.Diagnostics, result);

    var errors = CompilerErrors(BuildOutput(result));
    if (errors.Count > 0 && errors.All(error => error.File == CSharpDriverGenerator.SourceFile))
      return new(result.Outcome, directory, [.. result.Diagnostics,
        Diagnostic.Create(CSharpRunnerDiagnostics.Protocol, null, "the signature does not fit the declaration: " + string.Join("; ", errors))], result);

    var detail = errors.Count > 0 ? string.Join("; ", errors) : BuildOutput(result).Trim();
    return new(result.Outcome, directory, [.. result.Diagnostics, Diagnostic.Create(CSharpRunnerDiagnostics.Rejected, null, detail)], result);
  }

  /// <summary>Runs a built driver with the given arguments: <c>dotnet out/Driver.dll</c>.</summary>
  /// <param name="compiled">The build.</param>
  /// <param name="arguments">The arguments.</param>
  public override RunResult Execute(CompileResult compiled, IReadOnlyList<string> arguments) {
    ArgumentNullException.ThrowIfNull(compiled);
    ArgumentNullException.ThrowIfNull(arguments);
    return this.Run(new(Built(compiled.Directory), [], new(Toolchain, [CSharpDriverGenerator.Assembly, .. arguments]), this._driverLimits, TrustLevel.Trusted));
  }

  /// <summary>
  /// Runs the tests of an NUnit project with <c>dotnet test</c>, its results read from the TRX file.
  /// The project's packages are restored without the network: the packages its <c>PackageReference</c>s
  /// need, their dependencies included, are copied from the runner's packages folder into a feed of
  /// the run (<see cref="OfflinePackages"/>), the only source of the restore. A test host that crashes
  /// before it writes the results is <c>PLY1092</c>, a project that does not build <c>PLY1091</c>.
  /// </summary>
  /// <param name="projectDirectory">The project; its <c>bin</c> and <c>obj</c> are not copied.</param>
  /// <exception cref="LanguageRunnerException">The tests did not run.</exception>
  public override TestReport RunTests(string projectDirectory) {
    ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)) {
      var name = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');
      if (!name.StartsWith("bin/", StringComparison.Ordinal) && !name.StartsWith("obj/", StringComparison.Ordinal))
        files[name] = File.ReadAllBytes(path);
    }

    var references = files.Where(file => file.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
      .SelectMany(file => PackageReferences(Text(file.Value)));
    foreach (var (name, bytes) in OfflinePackages(this._packages, references))
      files[FeedFolder + "/" + name] = bytes;
    files["NuGet.config"] = Encoding.UTF8.GetBytes(LocalFeed);
    var step = new ProcessSpec(Toolchain, "test", "-c", "Release", "--nologo", "--disable-build-servers",
      "--logger", "trx;LogFileName=results.trx", "--results-directory", ResultsFolder, "-p:GenerateFullPaths=false");
    var result = this.Run(new(files, [], step, this._buildLimits, TrustLevel.Trusted) { Collect = [ResultsFolder] });
    if (result.Outcome != RunOutcome.Completed)
      throw new LanguageRunnerException($"the C# test run ended with {result.Outcome}", result.Diagnostics);

    var trx = result.Collected.Where(file => file.Key.EndsWith(".trx", StringComparison.Ordinal)).Select(file => Text(file.Value)).FirstOrDefault();
    if (trx is null) {
      var errors = CompilerErrors(BuildOutput(result));
      throw new LanguageRunnerException("the C# test run wrote no results", [errors.Count > 0
        ? Diagnostic.Create(CSharpRunnerDiagnostics.Rejected, null, string.Join("; ", errors))
        : Diagnostic.Create(CSharpRunnerDiagnostics.Crashed, null, result.ExitCode.ToString(CultureInfo.InvariantCulture), (Text(result.Stderr) + "\n" + Text(result.Stdout)).Trim())]);
    }

    return ReadTrx(trx);
  }

  /// <summary>The packages a project file references: the <c>Include</c> and <c>Version</c> of every <c>PackageReference</c>.</summary>
  /// <param name="project">The text of the project file.</param>
  /// <returns>The packages, with the versions as written.</returns>
  /// <exception cref="FormatException">The text is no XML.</exception>
  public static IReadOnlyList<(string Id, string Version)> PackageReferences(string project) {
    ArgumentNullException.ThrowIfNull(project);
    XDocument document;
    try {
      document = XDocument.Parse(project);
    } catch (System.Xml.XmlException exception) {
      throw new FormatException("the project file is no XML: " + exception.Message, exception);
    }

    return [.. document.Descendants().Where(element => element.Name.LocalName == "PackageReference")
      .Select(element => ((string?)element.Attribute("Include") ?? "", (string?)element.Attribute("Version") ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value ?? ""))
      .Where(reference => reference.Item1.Length > 0 && reference.Item2.Length > 0)];
  }

  /// <summary>
  /// The <c>.nupkg</c> files of packages and of everything they depend on, by file name, from a folder
  /// laid out as NuGet's global packages folder (<c>&lt;id&gt;/&lt;version&gt;/&lt;id&gt;.&lt;version&gt;.nupkg</c>
  /// with its <c>.nuspec</c> beside it). A dependency is taken at the lower bound of its range, the
  /// version NuGet's restore picks, from every dependency group but those of the .NET Framework; one
  /// the folder lacks is left out, for the restore to report.
  /// </summary>
  /// <param name="packages">The packages folder.</param>
  /// <param name="roots">The packages to start from.</param>
  /// <returns>The files.</returns>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> OfflinePackages(string packages, IEnumerable<(string Id, string Version)> roots) {
    ArgumentNullException.ThrowIfNull(packages);
    ArgumentNullException.ThrowIfNull(roots);
    var files = new SortedDictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pending = new Queue<(string Id, string Version)>(roots);
    while (pending.TryDequeue(out var package)) {
      var id = package.Id.ToLowerInvariant();
      var version = LowerBound(package.Version).ToLowerInvariant();
      if (!seen.Add(id + "/" + version))
        continue;

      var folder = Path.Combine(packages, id, version);
      var nupkg = Path.Combine(folder, $"{id}.{version}.nupkg");
      if (!File.Exists(nupkg))
        continue;

      files[$"{id}.{version}.nupkg"] = File.ReadAllBytes(nupkg);
      var nuspec = Path.Combine(folder, id + ".nuspec");
      if (!File.Exists(nuspec))
        continue;

      foreach (var dependency in XDocument.Load(nuspec).Descendants().Where(element => element.Name.LocalName == "dependency"))
        if (dependency.Parent is not { Name.LocalName: "group" } group || !IsFramework((string?)group.Attribute("targetFramework")))
          pending.Enqueue(((string?)dependency.Attribute("id") ?? "", (string?)dependency.Attribute("version") ?? ""));
    }

    return files;
  }

  /// <summary>The lower bound of a NuGet version range: <c>1.5.3</c> of <c>[1.5.3]</c>, <c>[1.5.3, )</c> or <c>1.5.3</c>.</summary>
  private static string LowerBound(string range) => range.Trim().TrimStart('[', '(').Split(',')[0].TrimEnd(']', ')').Trim();

  /// <summary>Whether a dependency group's target framework is one of the .NET Framework.</summary>
  private static bool IsFramework(string? framework)
    => framework is not null && (framework.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(framework, @"^net\d{2,3}$", RegexOptions.CultureInvariant));

  /// <summary>The counts and failures of a TRX file, the results <c>dotnet test --logger trx</c> writes.</summary>
  /// <param name="trx">The text of the file.</param>
  /// <returns>The report.</returns>
  /// <exception cref="FormatException">The text is no TRX file.</exception>
  public static TestReport ReadTrx(string trx) {
    ArgumentNullException.ThrowIfNull(trx);
    XDocument document;
    try {
      document = XDocument.Parse(trx.TrimStart('\uFEFF'));
    } catch (System.Xml.XmlException exception) {
      throw new FormatException("the test results are no XML: " + exception.Message, exception);
    }

    var results = document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").ToList();
    if (document.Root!.Name.LocalName != "TestRun")
      throw new FormatException("the test results are no TRX file: the root is not TestRun");

    string Outcome(XElement result) => (string?)result.Attribute("outcome") ?? "";
    var failures = results.Where(result => Outcome(result) == "Failed")
      .Select(result => new TestFailure((string?)result.Attribute("testName") ?? "",
        result.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value.Trim() ?? ""))
      .ToList();
    var passed = results.Count(result => Outcome(result) == "Passed");
    return new(results.Count, passed, failures.Count, results.Count - passed - failures.Count, failures);
  }

  /// <summary>
  /// The diagnostics of the vectors of a report that failed: <c>PLY0051</c> or <c>PLY0052</c> of the
  /// comparer, each followed by <c>PLY1094</c> with the expected and actual values of the call.
  /// </summary>
  /// <param name="report">The report of <see cref="LanguageRunnerBase.RunVectors"/>.</param>
  /// <param name="vectors">The vectors it ran.</param>
  /// <returns>The diagnostics, empty when every vector passed.</returns>
  public static IReadOnlyList<Diagnostic> Explain(ComparisonReport report, VectorFile vectors) {
    ArgumentNullException.ThrowIfNull(report);
    ArgumentNullException.ThrowIfNull(vectors);
    var signature = vectors.Signature ?? throw new ImageException("a vector file used by a language runner requires a call signature");
    var diagnostics = new List<Diagnostic>();
    var compared = report.Diagnostics(vectors.Fixture);
    var failed = 0;
    for (var index = 0; index < report.Outcomes.Count; ++index) {
      var outcome = report.Outcomes[index];
      if (outcome.Passed)
        continue;

      var vector = vectors.Vectors[index];
      diagnostics.Add(compared[failed++]);
      diagnostics.Add(Diagnostic.Create(CSharpRunnerDiagnostics.Mismatch, null, index.ToString(CultureInfo.InvariantCulture),
        Describe(signature, vector.Expected, vector.ExpectedTrap), Describe(signature, Image(outcome.ActualHex), Trap(outcome.ActualHex))));
    }

    return diagnostics;
  }

  /// <inheritdoc />
  protected override RunResult RunDriver(string directory, string export, CallSignature signature, ReadOnlyMemory<byte> inputs, TrustLevel trust) {
    var step = new ProcessSpec(Toolchain, [CSharpDriverGenerator.Assembly, export], inputs);
    var result = this.Run(new(Built(directory), [], step, this._driverLimits, trust));
    if (result.Outcome != RunOutcome.Completed || result.ExitCode == 0)
      return result;

    var stderr = Text(result.Stderr).Trim();
    var diagnostic = result.ExitCode == CSharpDriverGenerator.ProtocolExitCode
      ? Diagnostic.Create(CSharpRunnerDiagnostics.Protocol, null, stderr)
      : Diagnostic.Create(CSharpRunnerDiagnostics.Crashed, null, result.ExitCode.ToString(CultureInfo.InvariantCulture), stderr) with { Cause = stderr };
    return result with { Diagnostics = result.Diagnostics.Append(diagnostic).ToList() };
  }

  /// <summary>A compiler error of the build output: <c>file(line,column): error CS0029: text</c>.</summary>
  /// <param name="File">The file as the compiler names it.</param>
  /// <param name="Line">The line.</param>
  /// <param name="Column">The column.</param>
  /// <param name="Id">The error's id, such as <c>CS0029</c>.</param>
  /// <param name="Text">The message.</param>
  public sealed record CompilerError(string File, int Line, int Column, string Id, string Text) {

    /// <inheritdoc />
    public override string ToString() => $"{this.File}({this.Line},{this.Column}): {this.Id}: {this.Text}";

  }

  /// <summary>The distinct compiler errors of a build's output, in the order printed.</summary>
  /// <param name="output">What the build printed.</param>
  /// <returns>The errors.</returns>
  public static IReadOnlyList<CompilerError> CompilerErrors(string output) {
    ArgumentNullException.ThrowIfNull(output);
    return [.. ErrorLine().Matches(output)
      .Select(match => new CompilerError(FileName(match.Groups["file"].Value.Trim()), int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
        int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture), match.Groups["id"].Value, match.Groups["text"].Value.Trim()))
      .Distinct()];
  }

  /// <summary>The file name of a path as the compiler prints it, with <c>/</c> or <c>\</c> between folders whatever the host.</summary>
  private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

  private static byte[]? Image(string text) => text.StartsWith('!') ? null : Convert.FromHexString(text);

  private static TrapClass? Trap(string text) => text.StartsWith('!') ? TrapClass.Parse(text[1..]) : null;

  private static string Describe(CallSignature signature, byte[]? image, TrapClass? trap) {
    if (trap is not null)
      return "!" + trap.ToString();
    if (image is null)
      return "nothing";

    try {
      var values = CallImages.Decode(signature, CallImage.Output, image);
      return values.Count switch {
        0 => "nothing",
        1 when values[0].Name == CallImages.ResultName => values[0].Value.ToString()!,
        _ => string.Join(", ", values)
      };
    } catch (ImageException) {
      return Convert.ToHexStringLower(image);
    }
  }

  private CompileResult Refused(string directory, string reason) {
    var diagnostic = Diagnostic.Create(CSharpRunnerDiagnostics.Protocol, null, reason);
    var build = new RunResult(RunOutcome.BuildFailed, -1, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, [diagnostic]);
    return new(RunOutcome.BuildFailed, directory, [diagnostic], build);
  }

  private static Dictionary<string, ReadOnlyMemory<byte>> Built(string directory) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    var output = Path.Combine(directory, OutputFolder);
    if (Directory.Exists(output))
      foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        files[OutputFolder + "/" + Path.GetRelativePath(output, file).Replace('\\', '/')] = File.ReadAllBytes(file);

    return files;
  }

  /// <summary>What a build printed: the failed step's output, which its <c>PLY0030</c> carries, then the run's own.</summary>
  private static string BuildOutput(RunResult result)
    => string.Join("\n", result.Diagnostics.Select(diagnostic => $"{diagnostic.Message}\n{diagnostic.Cause}").Append(Text(result.Stdout)).Append(Text(result.Stderr)));

  private RunResult Run(RunPlan plan) => this._runner.RunAsync(plan).GetAwaiter().GetResult();

  private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

  [GeneratedRegex(@"^\s*(?<file>[^\r\n(]+)\((?<line>\d+),(?<column>\d+)\): error (?<id>CS\d{4}): (?<text>[^\r\n]*?)(?: \[[^\r\n]*\])?\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex ErrorLine();

}
