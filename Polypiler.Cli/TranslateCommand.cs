using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.Frontends.Rust.Cargo;
using Polypiler.Core.IR;
using Polypiler.Core.Backends.CSharp.Passes;
using Polypiler.Core.Backends.JavaScript.Passes;
using Polypiler.Core.Backends.Python.Passes;
using Polypiler.Core.Backends.Rust.Passes;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Optimize.Specialize;
using Polypiler.Core.Optimize.Target;
using Polypiler.Core.Recognize;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Cli;

/// <summary>
/// The <c>translate</c> command of the prototype:
/// <c>polypiler translate &lt;file&gt; --from &lt;csharp|javascript|python|rust&gt; --to &lt;javascript|csharp|python|rust&gt; --output &lt;directory&gt;</c>.
/// A target language may take a profile. <c>--profile</c> is one option that every target reads its
/// own way: for C# it names the target framework, and <c>--langversion</c> raises the version of the
/// language above the one the profile brings; for JavaScript it names the language level, and
/// <c>--module</c>, <c>--global</c> and <c>--typed-arrays</c> say how the file is packaged and what
/// an ECMAScript 5 engine has; for Rust it names the edition, <c>--rust-version</c> raises the
/// minimum supported Rust version above the one the edition brings, and <c>--trap-policy</c> chooses
/// whether a trap panics or is an error value (ADR 0008). The pipeline options <c>--opt</c>, <c>--disable-opt</c> and
/// <c>--explain-opt</c> (ADR 0004) apply to every pair: the optimizer runs between the frontend and
/// the backend, and the target passes between the backend's syntax tree and its printer (#1336),
/// unless <c>--opt none</c> says otherwise; the rules of the one and the passes of the other share
/// one namespace of optimization numbers. The foundation checks the command line
/// against the grammar; the command refuses, as <c>PLY0003</c>, a language it does not know and an
/// option that does not apply to the chosen languages or has a value they do not take. Beside the
/// files it writes <c>polypiler.manifest.json</c> (PRD §10.2), in the state <c>generated</c>: the
/// input with its hash, the profiles, the exported functions, the intrinsics, the floating-point
/// record the header of each file carries, the helpers the backend emitted with their origin and
/// licence, the licence of the output (<see cref="OutputLicence"/>: the input's
/// <c>SPDX-License-Identifier</c> header with the helpers' licences, <c>NOASSERTION</c> without a
/// header), the optimizations applied, the caveats and warnings, and the command line that writes the
/// output again, with its paths relative to the output directory.
/// </summary>
public sealed partial class TranslateCommand : ICliCommand {

  /// <summary>Exit code when the source, or the IR for the chosen target, uses a construct that is not supported.</summary>
  public const int UnsupportedConstruct = (int)ExitCode.UnsupportedConstruct;

  /// <summary>Exit code when the source is not valid in its own language.</summary>
  public const int InvalidSource = (int)ExitCode.InvalidSource;

  /// <summary>Exit code for an error inside Polypiler itself.</summary>
  public const int InternalError = (int)ExitCode.InternalError;

  private const string From = "--from";
  private const string To = "--to";
  private const string Output = "--output";
  private const string Namespace = "--namespace";
  private const string Export = "--export";
  private const string ExplainTypes = "--explain-types";
  private const string Profile = "--profile";
  private const string LangVersion = "--langversion";
  private const string Module = "--module";
  private const string Global = "--global";
  private const string TypedArrays = "--typed-arrays";
  private const string Float = "--float";
  private const string Opt = "--opt";
  private const string DisableOpt = "--disable-opt";
  private const string ExplainOpt = "--explain-opt";
  private const string Instantiate = "--instantiate";
  private const string Bind = "--bind";
  private const string RustVersion = "--rust-version";
  private const string OverflowChecksOption = "--overflow-checks";
  private const string CargoProfileOption = "--cargo-profile";
  private const string DebugAssertionsOption = "--debug-assertions";
  private const string TrapPolicyOption = "--trap-policy";
  private const string AllowUnsafe = "--allow-unsafe";
  private const string PerlContractOption = "--perl-contract";
  private const string ByteStorageOption = "--byte-storage";
  private const string ObjectModelOption = "--object-model";

  /// <summary>The caveat that records the overflow mode of a Rust import and where it came from.</summary>
  public const string RustOverflowCaveat = "rs-overflow-checks";

  private static readonly string[] _required = [From, To, Output];

  /// <summary>The name of the operand.</summary>
  private const string Operand = "file";

  /// <summary>What every option says in the help, by its name.</summary>
  private static readonly Dictionary<string, string> _descriptions = new(StringComparer.Ordinal) {
    [From] = "the source language",
    [To] = "the target language",
    [Output] = "the directory to write into; it is created when it does not exist",
    [Opt] = "the optimization level",
    [DisableOpt] = "optimizations not to apply",
    [ExplainOpt] = "write every optimization applied to standard error",
    [Export] = "JavaScript: the functions to translate",
    [ExplainTypes] = "JavaScript: write why every value has its type to standard output",
    [Profile] = "the target profile: a target framework of C#, a language level of JavaScript, a version of Python, an edition of Rust",
    [Module] = "JavaScript: how the file is packaged",
    [Global] = "JavaScript: the global object a script declares",
    [TypedArrays] = "JavaScript: an ECMAScript 5 engine with typed arrays",
    [Float] = "the floating-point mode",
    [Namespace] = "C#: the namespace of the generated code",
    [Instantiate] = "C#: an instantiation of a public generic method that the class itself does not use",
    [OverflowChecksOption] = "Rust: what an overflowing operator does, as rustc -C overflow-checks: panic (debug) or wrap (release); by default what the Cargo profile gives",
    [CargoProfileOption] = "Rust: the Cargo profile whose overflow-checks apply when --overflow-checks is not given (default: dev)",
    [DebugAssertionsOption] = "Rust: whether debug_assert! asserts, as rustc -C debug-assertions: on (debug) or off (release); a source with one needs it",
    [LangVersion] = "C#: the language version, above the one of the profile",
    [RustVersion] = "Rust: the minimum supported Rust version, above the one of the edition",
    [TrapPolicyOption] = "Rust: what a trap is: a panic (the default) or an error value, every function a trap can leave returning a Result (ADR 0008)",
    [AllowUnsafe] = "Rust: allow the one unsafe lowering, a mutable overlay view, which the crate then has to pass Miri with; refused with PLY1567 otherwise",
    [Bind] = "JavaScript: the binding module or global object of a native library, repeatable",
    [PerlContractOption] = "Perl: file holding source contract lines for exported subroutines",
    [ByteStorageOption] = "Perl: how u8[] byte buffers are stored (array or string)",
    [ObjectModelOption] = "Perl: object representation for classes and interfaces (blessed, moose or inside-out)"
  };

  private readonly Backend[] _backendsOfThis;

  /// <summary>Creates the command with the standard backends.</summary>
  public TranslateCommand() : this(_backends) {
  }

  private TranslateCommand(Backend[] backends) => this._backendsOfThis = backends;

  /// <inheritdoc/>
  public string Name => "translate";

  /// <inheritdoc/>
  public string Description => "translate a source file into another language; a prototype for small subsets of its languages, see the README for the planned interface";

  /// <summary>The options of the pass pipeline, which every pair of languages takes.</summary>
  private static readonly OptionSpec[] _pipeline = [new(Opt, "<none|default>"), new(DisableOpt, "<O####>[,<O####>...]"), new(ExplainOpt, null)];

  /// <summary>The source languages; another one is another entry.</summary>
  private static readonly Frontend[] _frontends = [
    new("csharp", [new(Instantiate, "<method[type,...]>")], InstantiateMisuse, ImportCSharp),
    new("javascript", [new(Export, "<name>[,<name>...]"), new(ExplainTypes, null)], ExportMisuse, ImportJavaScript),
    new("python", [], _ => null, ImportPython),
    new("rust", [new(OverflowChecksOption, "<panic|wrap>"), new(CargoProfileOption, "<name>"), new(DebugAssertionsOption, "<on|off>")], OverflowChecksMisuse, ImportRust),
    new("perl", [new(PerlContractOption, "<file-or-line>")], _ => null, ImportPerl),
    new("batch", [], _ => null, ImportBatch)
  ];

  /// <summary>The target languages; another one is another entry.</summary>
  private static readonly Backend[] _backends = [
    JavaScript(JavaScriptCapabilities.Standard, Implementations),
    CSharp(CSharpCapabilities.Default, Implementations),
    Python(PythonCapabilities.Standard, Implementations),
    Rust(RustCapabilities.Default, Implementations),
    Perl(Implementations)
  ];

  /// <summary>
  /// Declares the operand <c>&lt;file&gt;</c> and the options of the pipeline and of every language,
  /// each once; <c>--from</c>, <c>--to</c> and <c>--output</c> are required. Which of them apply
  /// depends on the languages chosen, which <see cref="Execute"/> checks.
  /// </summary>
  /// <param name="command">The command to add the symbols to.</param>
  public void Configure(Command command) {
    ArgumentNullException.ThrowIfNull(command);
    command.Arguments.Add(new Argument<string>(Operand) { Description = "the source file" });
    OptionSpec[] required = [
      new(From, Choice(_frontends.Select(frontend => frontend.Name))), new(To, Choice(this._backendsOfThis.Select(backend => backend.Name))), new(Output, "<directory>")
    ];
    var options = required
      .Concat(_pipeline)
      .Concat(_frontends.SelectMany(frontend => frontend.Options))
      .Concat(this._backendsOfThis.SelectMany(backend => backend.Options))
      .DistinctBy(option => option.Name);
    foreach (var option in options)
      command.Options.Add(Declared(option));
  }

  /// <summary>The option of the command line for an option of the command: a flag, a list or a single value.</summary>
  private static Option Declared(OptionSpec option) {
    var description = _descriptions[option.Name];
    if (option.Value is null)
      return new Option<bool>(option.Name) { Description = description };

    var helpName = option.Value[1..option.Value.IndexOf('>')];
    return option.Name is DisableOpt or Export or Instantiate or Bind or PerlContractOption
      ? new Option<string[]>(option.Name) { Description = description, HelpName = helpName }
      : new Option<string>(option.Name) { Description = description, HelpName = helpName, Required = _required.Contains(option.Name) };
  }

  /// <summary>Runs the command with the standard languages.</summary>
  /// <param name="arguments">The arguments after the word <c>translate</c>.</param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics and the caveats of an import.</param>
  /// <returns>The process exit code.</returns>
  public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    => Run(arguments, output, error, _backends);

  /// <summary>Runs the command with a table of its own for what each JavaScript profile has.</summary>
  /// <param name="arguments">The arguments after the word <c>translate</c>.</param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics and the caveats of an import.</param>
  /// <param name="capabilities">The table the JavaScript backend chooses its constructs from.</param>
  /// <returns>The process exit code.</returns>
  public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error, JavaScriptCapabilities capabilities)
    => Run(arguments, output, error, [JavaScript(capabilities, Implementations), CSharp(CSharpCapabilities.Default, Implementations), Python(PythonCapabilities.Standard, Implementations), Rust(RustCapabilities.Default, Implementations), Perl(Implementations)]);

  /// <summary>Runs the command with portable floating-point implementations of the caller's in place of the precomputed ones.</summary>
  /// <param name="arguments">The arguments after the word <c>translate</c>.</param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics and the caveats of an import.</param>
  /// <param name="implementations">
  /// Gives the implementations when a translation needs one; an <see cref="InvalidDataException"/> it
  /// throws is reported as damaged precomputed data, an internal error.
  /// </param>
  /// <returns>The process exit code.</returns>
  public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error, Func<IrModule> implementations)
    => Run(arguments, output, error, [JavaScript(JavaScriptCapabilities.Standard, implementations), CSharp(CSharpCapabilities.Default, implementations), Python(PythonCapabilities.Standard, implementations), Rust(RustCapabilities.Default, implementations), Perl(implementations)]);

  /// <summary>Runs <c>polypiler translate</c> with the arguments, the command made with the given backends.</summary>
  private static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error, Backend[] backends) {
    var registry = new CommandRegistry();
    registry.Add(new TranslateCommand(backends));
    return Program.Run(["translate", .. arguments], output, error, registry, Directory.GetCurrentDirectory());
  }

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    ArgumentNullException.ThrowIfNull(parse);
    ArgumentNullException.ThrowIfNull(context);
    var symbol = parse.CommandResult.Command;
    var file = parse.GetValue((Argument<string>)symbol.Arguments[0])!;
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    // The options contributed to the command belong to their areas, which read them; the command reads its own.
    foreach (var option in symbol.Options.Where(option => _descriptions.ContainsKey(option.Name)))
      if (parse.GetResult(option) is { Implicit: false })
        options[option.Name] = option switch {
          Option<bool> => "",
          Option<string[]> list => string.Join(option.Name == PerlContractOption ? '\n' : ',', parse.GetValue(list)!),
          _ => parse.GetValue((Option<string>)option)!
        };

    var frontend = _frontends.FirstOrDefault(candidate => candidate.Name == options[From]);
    if (frontend is null)
      return Refuse(context, $"unknown source language '{options[From]}'; the prototype reads {Listed(_frontends.Select(candidate => candidate.Name))}");

    var backends = this._backendsOfThis;
    var backend = backends.FirstOrDefault(candidate => candidate.Name == options[To]);
    if (backend is null)
      return Refuse(context, $"unknown target language '{options[To]}'; the prototype writes {Listed(backends.Select(candidate => candidate.Name))}");

    // An option that is not required belongs to a source or to a target language, and only to the chosen one.
    var foreign = options.Keys.FirstOrDefault(option => !_required.Contains(option) && _pipeline.All(pipeline => pipeline.Name != option) && !frontend.Has(option) && !backend.Has(option));
    var misuse = foreign is null ? PipelineMisuse(options) ?? frontend.Misuse(options) ?? backend.Misuse(options)
      : _frontends.Any(candidate => candidate.Has(foreign)) ? $"the option '{foreign}' does not apply to the source language '{frontend.Name}'"
      : $"the option '{foreign}' does not apply to the target language '{backend.Name}'";
    if (misuse is not null)
      return Refuse(context, misuse);

    if (!CliPaths.RequireInput(file, context))
      return (int)ExitCode.EnvironmentError;

    var bytes = File.ReadAllBytes(context.Resolve(file));
    using var text = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
    var (module, exitCode, caveats) = frontend.Import(file, text.ReadToEnd(), options, context);
    if (module is null)
      return exitCode;

    if (UnusedBinding(module, options) is { } unused)
      return Refuse(context, unused);

    var (optimized, applications) = Optimized(file, module, options, context, backend);
    return Emit(optimized, options, context, backend, new Record(file, bytes, frontend.Name, caveats, applications));
  }

  /// <summary>The level of <c>--opt</c>, or <see langword="null"/> for a value that names none.</summary>
  private static OptimizationLevel? LevelOf(IReadOnlyDictionary<string, string> options) => options.GetValueOrDefault(Opt, "default") switch {
    "none" => OptimizationLevel.None,
    "default" => OptimizationLevel.Default,
    _ => null
  };

  /// <summary>
  /// The optimization numbers of the pipeline: the rules of the IR optimizer, the passes over the
  /// legalized module and the target passes of every backend, which share one namespace, in the order
  /// of their numbers.
  /// </summary>
  private static readonly string[] _optimizations = [
    .. RuleTable.Default.Ids.Concat(IrPassPipeline.Default.Ids).Append(PyIntSpecialization.Id).Concat(CSharpTargetPasses.Pipeline.Ids).Concat(JavaScriptTargetPasses.Pipeline.Ids).Concat(PythonTargetPasses.Pipeline.Ids).Concat(RustTargetPasses.Pipeline.Ids)
      .Distinct().Order(StringComparer.Ordinal)
  ];

  /// <summary>What is wrong with the pipeline options: an unknown level, or an optimization number the pipeline does not have; the foundation refuses an empty item.</summary>
  private static string? PipelineMisuse(IReadOnlyDictionary<string, string> options) {
    if (LevelOf(options) is null)
      return $"unknown optimization level '{options[Opt]}'; the levels are none and default";

    if (!options.TryGetValue(DisableOpt, out var list))
      return null;

    var unknown = list.Split(',').FirstOrDefault(id => !_optimizations.Contains(id));
    return unknown switch {
      null => null,
      _ when !IrRule.IsId(unknown) => $"'{unknown}' is not an optimization number of the form O####",
      _ => $"unknown optimization '{unknown}'; the pipeline has {string.Join(", ", _optimizations)}"
    };
  }

  /// <summary>Which target passes run by the options of a command line: those of the level that <c>--disable-opt</c> does not name.</summary>
  private static TargetPassOptions PassesOf(IReadOnlyDictionary<string, string> options) => new() {
    Level = LevelOf(options) ?? OptimizationLevel.Default,
    Disabled = options.TryGetValue(DisableOpt, out var list) ? list.Split(',').ToHashSet() : []
  };

  /// <summary>
  /// Runs the pass pipeline on an imported module with the options of the command line; with
  /// <c>--explain-opt</c> every rule application is written to the error stream as a note.
  /// </summary>
  private static (IrModule Module, IReadOnlyList<RuleApplication> Applications) Optimized(string file, IrModule module, IReadOnlyDictionary<string, string> options, CliContext context, Backend backend) {
    var disabled = options.TryGetValue(DisableOpt, out var list) ? list.Split(',').ToHashSet() : [];
    // The specialization of the arbitrary-precision integer is required: it runs at every level, --opt none too.
    var specialized = disabled.Contains(PyIntSpecialization.Id) ? new OptimizationResult(module, []) : PyIntSpecialization.Run(module);
    var result = Optimizer.Run(specialized.Module, new OptimizerOptions {
      Level = LevelOf(options)!.Value,
      Disabled = disabled,
      Target = backend.Target,
      Prover = new Polypiler.Core.Verification.Proofs.SmtRecognitionProver()
    });
    IReadOnlyList<RuleApplication> applications = [.. specialized.Applications, .. result.Applications];
    if (options.ContainsKey(ExplainOpt)) {
      foreach (var application in applications)
        context.Note($"{file}: opt {application}");

      foreach (var note in result.Notes)
        context.Note($"{file}: opt {note}");
    }

    return (result.Module, applications);
  }

  /// <summary>Checks a module and writes it in the target language into the output directory.</summary>
  /// <param name="module">The module to emit.</param>
  /// <param name="options">
  /// The options of a command line that <see cref="Execute"/> accepts, by their names: <c>--to</c> with
  /// the target language, <c>--output</c> with the directory to write into, which is created when it
  /// does not exist, and the options of that target language.
  /// </param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics, as text.</param>
  /// <returns>The process exit code.</returns>
  public static int Emit(IrModule module, IReadOnlyDictionary<string, string> options, TextWriter output, TextWriter error)
    => Emit(module, options, Context(output, error), _backends.First(candidate => candidate.Name == options[To]));

  /// <summary>Checks a module and writes it as C# by a capability table of the caller's.</summary>
  /// <param name="module">The module to emit.</param>
  /// <param name="options">The options as <see cref="Emit(IrModule, IReadOnlyDictionary{string, string}, TextWriter, TextWriter)"/> takes them; <c>--to</c> is not read.</param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics, as text.</param>
  /// <param name="capabilities">What each profile has, and what replaces what it lacks.</param>
  /// <returns>The process exit code.</returns>
  public static int Emit(IrModule module, IReadOnlyDictionary<string, string> options, TextWriter output, TextWriter error, CSharpCapabilities capabilities)
    => Emit(module, options, Context(output, error), CSharp(capabilities, Implementations));

  /// <summary>The context of an emission outside a command line: diagnostics as text, paths relative to the current directory.</summary>
  private static CliContext Context(TextWriter output, TextWriter error)
    => new(output, error, "", DiagnosticsFormat.Text, false, Directory.GetCurrentDirectory());

  /// <summary>
  /// Lowers, checks and writes a module; with <c>--explain-opt</c> and the record of a translation,
  /// every change of a target pass is written to the error stream as a note, as the rules are.
  /// </summary>
  private static int Emit(IrModule module, IReadOnlyDictionary<string, string> options, CliContext context, Backend backend, Record? record = null) {
    try {
      module = backend.Lower(module, options);
    } catch (InvalidDataException exception) {
      context.Report(Diagnostic.Create(PrecomputedImplementations.DamagedDescriptor, null, exception.Message));
      return InternalError;
    }

    // The helpers legalization bound into the module are optimised with the code that calls them; the
    // backend checks the module it is going to write, a function value the passes bound to its one
    // callee no longer in it. A module the verifier rejects gets no pass: the backend says why it
    // cannot be written, as it would of the passes' result.
    var disabled = options.TryGetValue(DisableOpt, out var names) ? names.Split(',').ToHashSet() : [];
    var passes = IrVerifier.Verify(module).Count > 0 ? new OptimizationResult(module, [])
      : IrPassPipeline.Default.Run(module, new OptimizerOptions {
        Level = LevelOf(options) ?? OptimizationLevel.Default,
        Roots = context.Layout?.Tests.ToHashSet(StringComparer.Ordinal) ?? [],
        Disabled = disabled
      });
    module = passes.Module;
    var problems = backend.Check(module, options);
    if (problems.Count > 0) {
      foreach (var problem in problems)
        context.Report(problem);

      return problems.Any(problem => problem.Code == IrVerifier.Code) ? InternalError : UnsupportedConstruct;
    }

    if (record is not null) {
      if (options.ContainsKey(ExplainOpt)) {
        foreach (var application in passes.Applications)
          context.Note($"{record.File}: opt {application}");

        foreach (var note in passes.Notes)
          context.Note($"{record.File}: opt {note}");
      }

      record = record with { Applications = [.. record.Applications, .. passes.Applications] };
    }

    // The files are written below the directory as resolved, and named on standard output as typed.
    var directory = options[Output];
    try {
      var full = context.Resolve(directory);
      Directory.CreateDirectory(full);
      var emission = backend.Emit(module, options, PassesOf(options), context.Layout);
      if (record is not null && options.ContainsKey(ExplainOpt))
        foreach (var application in emission.Applications)
          context.Note($"{record.File}: opt {application}");

      var outputs = new List<ManifestOutput>();
      var helpers = new List<EmittedHelper>();
      foreach (var generated in emission.Files) {
        helpers.AddRange(generated.Helpers);
        var bytes = _utf8.GetBytes(generated.Content);
        // A crate's sources lie in a folder of the output (src/lib.rs).
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(full, generated.Path))!);
        File.WriteAllBytes(Path.Combine(full, generated.Path), bytes);
        outputs.Add(new(ManifestWriter.PortablePath(generated.Path), ManifestWriter.Sha256(bytes), ManifestOutputRole.Source));
        context.Output.WriteLine(Path.Combine(directory, generated.Path));
      }

      if (record is not null)
        ManifestWriter.Write(TranslationManifest(module, options, context, backend, record, full, outputs, helpers), full);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) {
      CliPaths.NotCreatable(directory, exception.Message, context);
      return (int)ExitCode.EnvironmentError;
    }

    return (int)ExitCode.Success;
  }

  private static (IrModule? Module, int ExitCode, IReadOnlyList<ManifestMessage> Caveats) ImportCSharp(string file, string source, IReadOnlyDictionary<string, string> options, CliContext context) {
    Polypiler.Core.Frontends.CSharp.ImportResult imported;
    try {
      imported = CSharpFrontend.Import([new CSharpSource(file, source)], false, options.TryGetValue(Instantiate, out var list) ? Instantiations(list)! : []);
    } catch (ArgumentException unknown) {
      // An instantiation that names no generic method of the class, or a type that is none, is a mistake of the command line.
      return (null, Refuse(context, unknown.Message), []);
    }

    foreach (var diagnostic in imported.Diagnostics)
      context.Report(diagnostic);

    // What the imported code does differently from the source is a note, as it is for JavaScript.
    if (imported.Module?.Caveats.Contains(CSharpFrontend.InvariantCulture) == true)
      context.Note($"{file}: note {CSharpFrontend.InvariantCulture}: {CSharpFrontend.InvariantCultureDescription}");

    var failure = imported.Diagnostics.Any(diagnostic => diagnostic.Code is CSharpFrontend.CompilerError or CSharpFrontend.ConstantOverflow)
      ? InvalidSource
      : UnsupportedConstruct;
    IReadOnlyList<ManifestMessage> caveats = imported.Module?.Caveats.Contains(CSharpFrontend.InvariantCulture) == true
      ? [new ManifestMessage(CSharpFrontend.InvariantCulture, CSharpFrontend.InvariantCultureDescription)]
      : [];
    return (imported.Module, failure, caveats);
  }

  /// <summary>
  /// Imports JavaScript, all of the file or the functions <c>--export</c> names. What the imported
  /// code is known to do differently from the original is written as a note; it is no error.
  /// </summary>
  private static (IrModule? Module, int ExitCode, IReadOnlyList<ManifestMessage> Caveats) ImportJavaScript(string file, string source, IReadOnlyDictionary<string, string> options, CliContext context) {
    var imported = JavaScriptFrontend.Import(file, source, options.TryGetValue(Export, out var names) ? Names(names) : null, Required);
    foreach (var diagnostic in imported.Diagnostics)
      context.Report(diagnostic);

    // Why every parameter, local and result has its type: the answer to "why is this a double?".
    if (options.ContainsKey(ExplainTypes))
      foreach (var explanation in imported.Explanations)
        context.Output.WriteLine($"{file}: type {explanation}");

    foreach (var caveat in imported.Caveats)
      context.Note($"{file}: note {caveat.Id}: {caveat.Description}");

    var failure = imported.Diagnostics.Any(diagnostic => diagnostic.Code == JavaScriptFrontend.SyntaxError)
      ? InvalidSource
      : UnsupportedConstruct;
    return (imported.Module, failure, [.. imported.Caveats.Select(caveat => new ManifestMessage(caveat.Id, caveat.Description))]);
  }

  private static (IrModule? Module, int ExitCode, IReadOnlyList<ManifestMessage> Caveats) ImportPerl(string file, string source, IReadOnlyDictionary<string, string> options, CliContext context) {
    var contractLines = new StringBuilder();
    if (options.TryGetValue(PerlContractOption, out var rawContracts)) {
      foreach (var item in rawContracts.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
        var resolved = context.Resolve(item);
        if (File.Exists(resolved)) {
          contractLines.AppendLine(File.ReadAllText(resolved));
        } else if (item.Contains(':')) {
          contractLines.AppendLine(item);
        } else {
          if (!CliPaths.RequireInput(item, context))
            return (null, (int)ExitCode.EnvironmentError, []);
        }
      }
    }

    var imported = PerlFrontend.Import(file, source, new PerlImportOptions { Contracts = contractLines.ToString() });
    foreach (var diagnostic in imported.Diagnostics)
      context.Report(diagnostic);

    if (imported.TestFunctions.Count > 0)
      context.Layout = new CrateLayout(null, new Dictionary<string, string>(), imported.TestFunctions);

    var failure = imported.Diagnostics.Any(diagnostic => diagnostic.Code == PerlFrontend.CompileFailedCode) ? InvalidSource : UnsupportedConstruct;
    return (imported.Module, failure, []);
  }

  /// <summary>
  /// Imports Python, parsed by the installed CPython. What the imported code is known to do
  /// differently from the original is written as a note, as it is for JavaScript.
  /// </summary>
  private static (IrModule? Module, int ExitCode, IReadOnlyList<ManifestMessage> Caveats) ImportPython(string file, string source, IReadOnlyDictionary<string, string> options, CliContext context) {
    var imported = PythonFrontend.Import(file, source);
    foreach (var diagnostic in imported.Diagnostics)
      context.Report(diagnostic);

    foreach (var caveat in imported.Caveats)
      context.Note($"{file}: note {caveat.Id}: {caveat.Description}");

    var failure = imported.Diagnostics.Any(diagnostic => diagnostic.Code == PythonFrontend.SyntaxError) ? InvalidSource : UnsupportedConstruct;
    return (imported.Module, failure, [.. imported.Caveats.Select(caveat => new ManifestMessage(caveat.Id, caveat.Description))]);
  }

  private static (IrModule? Module, int ExitCode, IReadOnlyList<ManifestMessage> Caveats) ImportBatch(
    string file, string source, IReadOnlyDictionary<string, string> options, CliContext context) {
    var imported = BatchFrontend.Import(file, source);
    foreach (var diagnostic in imported.Diagnostics)
      context.Report(diagnostic);
    return (imported.Module, imported.Diagnostics.FirstOrDefault()?.ExitCode is { } code ? (int)code : InvalidSource, []);
  }

  /// <summary>The text of a file a JavaScript module requires, or <see langword="null"/> when there is none.</summary>
  private static string? Required(string file) => File.Exists(file) ? File.ReadAllText(file) : null;

  private static string[] Names(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  /// <summary>
  /// Imports Rust with the overflow mode of <c>--overflow-checks</c>, else with the one Cargo builds
  /// the source with: the <c>overflow-checks</c> of the profile <c>--cargo-profile</c> names
  /// (<c>dev</c> by default) in the <c>Cargo.toml</c> that applies, the profile's default without
  /// one. A profile the manifest does not define, or a manifest that is no TOML, is <c>PLY1503</c>.
  /// The mode and its origin are a note and a caveat of the manifest.
  /// </summary>
  private static (IrModule? Module, int ExitCode, IReadOnlyList<ManifestMessage> Caveats) ImportRust(string file, string source, IReadOnlyDictionary<string, string> options, CliContext context) {
    OverflowModeChoice choice;
    if (options.TryGetValue(OverflowChecksOption, out var value))
      choice = new(value == "panic" ? OverflowChecks.Panic : OverflowChecks.Wrap, OverflowChecksOption);
    else
      try {
        choice = CargoOverflowMode.ForSource(context.Resolve(file), options.GetValueOrDefault(CargoProfileOption, CargoOverflowMode.DefaultProfile));
      } catch (CargoProfileException unreadable) {
        context.Report(RustFrontend.UnreadableOverflowModeDescriptor.At(string.Format(CultureInfo.InvariantCulture, RustFrontend.UnreadableOverflowModeDescriptor.MessageFormat, unreadable.Message), file, 1, 1));
        return (null, (int)ExitCode.UsageError, []);
      }

    var description = choice.Mode == OverflowChecks.Panic
      ? $"overflow checks on, an overflowing operator panics ({choice.Origin})"
      : $"overflow checks off, an overflowing operator wraps ({choice.Origin})";
    context.Note($"{file}: note {RustOverflowCaveat}: {description}");
    // A file module is read from beside the file that declares it.
    var imported = RustFrontend.Import(file, source, new RustImportOptions(choice.Mode) {
      DebugAssertions = options.TryGetValue(DebugAssertionsOption, out var debug) ? debug == "on" : null,
      ReadFile = path => File.Exists(context.Resolve(path)) ? File.ReadAllText(context.Resolve(path)) : null
    });
    foreach (var diagnostic in imported.Diagnostics)
      context.Report(diagnostic);

    var failure = imported.Diagnostics.Any(diagnostic => diagnostic.ExitCode == ExitCode.InvalidSource) ? InvalidSource : UnsupportedConstruct;
    // The crate's modules and tests go to a backend that writes them: a crate whose root is src/lib.rs is named after its folder.
    var full = Path.GetFullPath(context.Resolve(file));
    var package = Path.GetFileName(full) == "lib.rs" && Path.GetFileName(Path.GetDirectoryName(full)) == "src" ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(full))) : null;
    context.Layout = new CrateLayout(package, imported.Modules, imported.Tests);
    return (imported.Module, failure, [new ManifestMessage(RustOverflowCaveat, description)]);
  }

  /// <summary>What is wrong with <c>--overflow-checks</c>: a value other than <c>panic</c> or <c>wrap</c>.</summary>
  private static string? OverflowChecksMisuse(IReadOnlyDictionary<string, string> options)
    => options.TryGetValue(OverflowChecksOption, out var mode) && mode is not ("panic" or "wrap")
      ? $"unknown overflow mode '{mode}'; the modes are panic and wrap"
      : options.TryGetValue(DebugAssertionsOption, out var debug) && debug is not ("on" or "off")
      ? $"unknown debug-assertions setting '{debug}'; the settings are on and off"
      : null;

  /// <summary>
  /// The instantiations of <c>--instantiate</c>, whose values the command line gives joined by commas:
  /// split again at the commas outside brackets; <see langword="null"/> when one is not of the form
  /// <c>Method[type,…]</c>.
  /// </summary>
  private static List<CSharpInstantiation>? Instantiations(string list) {
    var items = new List<string>();
    var (depth, start) = (0, 0);
    for (var index = 0; index <= list.Length; ++index) {
      if (index < list.Length && list[index] != ',') {
        depth += list[index] switch { '[' => 1, ']' => -1, _ => 0 };
        continue;
      }

      if (depth != 0 && index < list.Length)
        continue;

      items.Add(list[start..index]);
      start = index + 1;
    }

    var parsed = new List<CSharpInstantiation>();
    foreach (var item in items) {
      if (!CSharpInstantiation.TryParse(item, out var instantiation))
        return null;

      parsed.Add(instantiation!);
    }

    return parsed;
  }

  /// <summary>What is wrong with <c>--instantiate</c>: an item that is not of the form <c>Method[type,…]</c>.</summary>
  private static string? InstantiateMisuse(IReadOnlyDictionary<string, string> options)
    => options.TryGetValue(Instantiate, out var list) && Instantiations(list) is null
      ? $"the option '{Instantiate}' takes instantiations of the form Method[type,...], not '{list}'"
      : null;

  private static string? ExportMisuse(IReadOnlyDictionary<string, string> options)
    => options.TryGetValue(Export, out var names) && Names(names).Length == 0 ? $"the option '{Export}' names no function" : null;

  /// <summary>The JavaScript backend with a capability table; <c>--profile</c> names the language level.</summary>
  private static Backend JavaScript(JavaScriptCapabilities capabilities, Func<IrModule> implementations) => new(
    "javascript",
    NativeAccuracy.Node,
    options => JavaScriptProfileOf(options).Profile!.ToString(),
    new RuleTarget(capabilities.RemainderFromQuotient),
    [new(Profile, "<name>"), new(Module, Choice(JavaScriptProfile.ModuleNames)), new(Global, "<name>"), new(TypedArrays, null), new(Float, FloatChoice), new(Bind, "<library=module-or-global>")],
    options => FloatMisuse(options) ?? JavaScriptProfileOf(options).Error ?? BindMisuse(options),
    (module, options) => JavaScriptEmitter.Legalize(module, implementations, JavaScriptProfileOf(options).Profile!, FloatModeOf(options), capabilities),
    (module, options) => JavaScriptEmitter.Check(module, JavaScriptProfileOf(options).Profile!, capabilities),
    (module, options, passes, _) => JavaScriptEmitter.Emit(module, JavaScriptProfileOf(options).Profile!, FloatModeOf(options), capabilities, passes));

  /// <summary>The JavaScript profile the options of a command line name, or what is wrong with them.</summary>
  private static (JavaScriptProfile? Profile, string? Error) JavaScriptProfileOf(IReadOnlyDictionary<string, string> options) {
    JavaScriptProfile.TryParse(
      options.GetValueOrDefault(Profile), options.GetValueOrDefault(Module), options.GetValueOrDefault(Global), options.ContainsKey(TypedArrays),
      out var profile, out var error);
    return (profile is null ? null : profile with { Bindings = BindingsOf(options) }, error);
  }

  /// <summary>The C# backend with a capability table.</summary>
  private static Backend CSharp(CSharpCapabilities capabilities, Func<IrModule> implementations) => new("csharp",
    NativeAccuracy.DotNet,
    options => CSharpProfileOf(options).ToString(),
    new RuleTarget(capabilities.RemainderFromQuotient),
    [new(Namespace, "<name>"), new(Profile, "<name>"), new(LangVersion, "<version>"), new(Float, FloatChoice)],
    options => FloatMisuse(options) ?? CSharpMisuse(options),
    (module, options) => CSharpEmitter.Legalize(module, implementations, CSharpProfileOf(options), FloatModeOf(options), capabilities),
    (module, options) => CSharpEmitter.Check(module, CSharpProfileOf(options), capabilities),
    (module, options, passes, _) => CSharpEmitter.Emit(module, CSharpProfileOf(options), FloatModeOf(options), options.GetValueOrDefault(Namespace), capabilities, passes));

  /// <summary>The Perl backend; <c>--profile</c> accepts <c>perl/5.6</c>, <c>perl/5.8</c> or <c>perl/5</c>, <c>--byte-storage</c> chooses <c>array</c> or <c>string</c>, and <c>--object-model</c> chooses <c>blessed</c>, <c>moose</c> or <c>inside-out</c>.</summary>
  private static Backend Perl(Func<IrModule> implementations) => new("perl",
    PerlEmitter.Runtime,
    options => PerlProfileOf(options).Profile!.ToString(),
    RuleTarget.Neutral,
    [new(Profile, Choice(PerlProfile.Names)), new(ByteStorageOption, Choice(PerlProfile.ByteStorageNames)), new(ObjectModelOption, Choice(PerlProfile.ObjectModelNames)), new(Float, FloatChoice)],
    options => FloatMisuse(options) ?? PerlProfileOf(options).Error,
    (module, options) => PerlEmitter.Legalize(module, implementations, PerlProfileOf(options).Profile!, FloatModeOf(options)),
    (module, options) => PerlEmitter.Check(module, PerlProfileOf(options).Profile!),
    (module, options, _, layout) => PerlEmitter.EmitTarget(module, PerlProfileOf(options).Profile!, new PerlEmitOptions {
      PackageName = layout?.Package,
      TestFunctions = layout?.Tests is { Count: > 0 } tests ? tests.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal)
    }));

  private static (PerlProfile? Profile, string? Error) PerlProfileOf(IReadOnlyDictionary<string, string> options) {
    PerlProfile.TryParse(options.GetValueOrDefault(Profile), options.GetValueOrDefault(ByteStorageOption), options.GetValueOrDefault(ObjectModelOption), out var profile, out var error);
    return (profile, error);
  }

  /// <summary>The Python backend with a capability table; <c>--profile</c> names the version of Python.</summary>
  private static Backend Python(PythonCapabilities capabilities, Func<IrModule> implementations) => new("python",
    PythonEmitter.Runtime,
    options => PythonProfileOf(options).Profile!.Name,
    RuleTarget.Neutral,
    [new(Profile, Choice(PythonProfile.Names)), new(Float, FloatChoice)],
    options => FloatMisuse(options) ?? PythonProfileOf(options).Error,
    (module, options) => PythonEmitter.Legalize(module, implementations, PythonProfileOf(options).Profile!, FloatModeOf(options), capabilities),
    (module, options) => PythonEmitter.Check(module, PythonProfileOf(options).Profile!, capabilities),
    (module, options, passes, _) => PythonEmitter.Emit(module, PythonProfileOf(options).Profile!, FloatModeOf(options), capabilities, passes));

  /// <summary>The Python profile <c>--profile</c> names, the default one without it, or what is wrong with the name.</summary>
  private static (PythonProfile? Profile, string? Error) PythonProfileOf(IReadOnlyDictionary<string, string> options) {
    PythonProfile.TryParse(options.GetValueOrDefault(Profile), out var profile, out var error);
    return (profile, error);
  }

  /// <summary>The Rust backend with a capability table; <c>--profile</c> names the edition, <c>--rust-version</c> the MSRV.</summary>
  private static Backend Rust(RustCapabilities capabilities, Func<IrModule> implementations) => new("rust",
    "rust",
    options => RustProfileOf(options).Profile!.ToString(),
    new RuleTarget(false),
    [new(Profile, "<name>"), new(RustVersion, "<version>"), new(TrapPolicyOption, Choice(RustProfile.TrapPolicies)), new(AllowUnsafe, null), new(Float, FloatChoice)],
    options => FloatMisuse(options) ?? RustProfileOf(options).Error,
    (module, options) => RustEmitter.Legalize(module, implementations, RustProfileOf(options).Profile!, capabilities),
    (module, options) => RustEmitter.Check(module, RustProfileOf(options).Profile!, capabilities),
    (module, options, passes, layout) => RustEmitter.Emit(module, RustProfileOf(options).Profile!, capabilities, passes, new RustEmitOptions {
      Mode = FloatModeOf(options),
      PackageName = layout?.Package,
      Modules = layout?.Modules ?? new Dictionary<string, string>(),
      CrateRoot = layout?.Modules.Count > 0,
      TestFunctions = layout?.Tests ?? []
    }));

  /// <summary>The Rust profile the options of a command line name, unsafe code allowed by <c>--allow-unsafe</c>, or what is wrong with them.</summary>
  private static (RustProfile? Profile, string? Error) RustProfileOf(IReadOnlyDictionary<string, string> options) {
    RustProfile.TryParse(options.GetValueOrDefault(Profile), options.GetValueOrDefault(RustVersion), options.GetValueOrDefault(TrapPolicyOption), out var profile, out var error);
    return (profile?.WithUnsafe(options.ContainsKey(AllowUnsafe)), error);
  }

  /// <summary>The values <c>--float</c> takes.</summary>
  private static string FloatChoice => Choice(["strict", "accurate"]);

  /// <summary>What is wrong with <c>--float</c>: a value other than <c>strict</c> or <c>accurate</c>.</summary>
  private static string? FloatMisuse(IReadOnlyDictionary<string, string> options)
    => options.TryGetValue(Float, out var mode) && mode is not ("strict" or "accurate")
      ? $"unknown floating-point mode '{mode}'; the modes are strict and accurate"
      : null;

  /// <summary>The floating-point mode of <c>--float</c>; <c>accurate</c> by default (ADR 0005).</summary>
  private static FloatMode FloatModeOf(IReadOnlyDictionary<string, string> options)
    => options.GetValueOrDefault(Float) == "strict" ? FloatMode.Strict : FloatMode.Accurate;

  /// <summary>
  /// The portable floating-point implementations, as the build precomputed them, read the first time
  /// a translation needs one: what a target lacks is emitted from it.
  /// </summary>
  private static IrModule Implementations() => PrecomputedImplementations.Module;

  /// <summary>What is wrong with the options of the C# backend: the namespace first, then the profile and its language version.</summary>
  private static string? CSharpMisuse(IReadOnlyDictionary<string, string> options) {
    if (options.TryGetValue(Namespace, out var name) && !CSharpEmitter.IsNamespace(name))
      return $"'{name}' is not a namespace name";

    return CSharpProfile.TryParse(options.GetValueOrDefault(Profile, CSharpProfile.Default.Name), options.GetValueOrDefault(LangVersion), out _, out var problem) ? null : problem;
  }

  /// <summary>The profile the options name: <c>--profile</c>, or the default one, at the language version of <c>--langversion</c>.</summary>
  private static CSharpProfile CSharpProfileOf(IReadOnlyDictionary<string, string> options)
    => CSharpProfile.Parse(options.GetValueOrDefault(Profile, CSharpProfile.Default.Name), options.GetValueOrDefault(LangVersion));

  private static string Choice(IEnumerable<string> names) => $"<{string.Join('|', names)}>";

  /// <summary>Names in quotes, the last joined by "and", the others by commas: 'a', 'b' and 'c'.</summary>
  private static string Listed(IEnumerable<string> names) {
    var quoted = names.Select(name => $"'{name}'").ToList();
    return quoted.Count < 2 ? string.Concat(quoted) : $"{string.Join(", ", quoted[..^1])} and {quoted[^1]}";
  }

  /// <summary>Reports a command line the command cannot carry out as <c>PLY0003</c>.</summary>
  private static int Refuse(CliContext context, string message) {
    context.Report(Diagnostic.Create(CliDiagnostics.CommandLine, null, message));
    return (int)ExitCode.UsageError;
  }

  /// <summary>What the manifest of a translation records beyond the emitted module: the input and what the stages before the backend found.</summary>
  /// <param name="File">The input as typed.</param>
  /// <param name="Bytes">The bytes of the input.</param>
  /// <param name="Language">The source language.</param>
  /// <param name="Caveats">What the imported code is known to do differently from the source.</param>
  /// <param name="Applications">Every rule the optimizer applied.</param>
  private sealed record Record(string File, byte[] Bytes, string Language, IReadOnlyList<ManifestMessage> Caveats, IReadOnlyList<RuleApplication> Applications);

  private static readonly UTF8Encoding _utf8 = new(false);

  /// <summary>What carrying the helpers obliges to: for each licence identifier of theirs, keeping its notice.</summary>
  private static List<string> Obligations(IReadOnlyList<EmittedHelper> helpers)
    => [.. helpers.SelectMany(helper => helper.Licence.Split(" AND ", StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
      .Select(licence => $"{licence}: keep the copyright and licence notice of the helpers under it ({EmittedHelper.GrantUrl} and the notices of their origins)")];

  /// <summary>
  /// The manifest of a translation, in the state <c>generated</c>. Every path in it is relative to the
  /// output directory, so the command line it records writes the same output again when run there.
  /// </summary>
  private static Manifest TranslationManifest(IrModule module, IReadOnlyDictionary<string, string> options, CliContext context, Backend backend, Record record, string output, IReadOnlyList<ManifestOutput> outputs, IReadOnlyList<EmittedHelper> helpers) {
    var input = InputPath(output, context.Resolve(record.File));
    List<string> command = ["translate", input, From, options[From], To, options[To], Output, "."];
    foreach (var (name, value) in options.Where(option => !_required.Contains(option.Key)))
      command.AddRange(name is ExplainOpt or ExplainTypes or TypedArrays or AllowUnsafe ? [name] : [name, value]);

    command.AddRange(context.ContributedArguments);
    var mode = FloatModeOf(options);
    return new Manifest {
      Compiler = new(ProductInfo.Name, ProductInfo.Version, IrModuleFormat.Version),
      Command = command,
      Inputs = [new(input, ManifestWriter.Sha256(record.Bytes), null)],
      Frontend = new(record.Language, null),
      Target = new(backend.Name, backend.ProfileName(options), backend.Runtime, "any"),
      Outputs = outputs,
      Api = [.. module.Functions.Where(function => function.IsPublic).Select(function => new ManifestApiSymbol(function.Name,
        $"{function.ReturnType} {function.Name}({string.Join(", ", function.Parameters.Select(parameter => $"{parameter.Type} {parameter.Name}"))})"))],
      Intrinsics = [.. FloatManifest.Operations(module).Select(op => $"INTRINSICS.{op}")],
      FloatingPoint = FloatManifest.HasFloat(module)
        ? new(mode == FloatMode.Strict ? "strict" : "accurate", [.. FloatManifest.Natives(module, mode, backend.Runtime).Select(native => new ManifestNativeFunction(
          $"INTRINSICS.{native.Op}", native.Format.ToString(), native.Contract.ToString(), native.Runtime, native.Slack, native.Bound))])
        : null,
      Helpers = [.. helpers.Select(helper => new ManifestHelper(helper.Symbol, helper.Licence, helper.Origin, helper.CallSites, helper.Size, helper.DependsOn))],
      NativeDependencies = [.. module.ForeignFunctions.Select(function => function.Library).Distinct().Select(library => new ManifestNativeDependency(library, "unspecified", "NOASSERTION"))],
      Licensing = new(OutputLicence.Of(OutputLicence.Header(_utf8.GetString(record.Bytes)), helpers.Select(helper => helper.Licence)), Obligations(helpers), []),
      Caveats = [.. record.Caveats, .. ForeignCaveats(module, options, backend)],
      Optimization = new(options.GetValueOrDefault(Opt, "default"), options.TryGetValue(DisableOpt, out var disabled) ? disabled.Split(',') : [],
        [.. record.Applications.Select(application => application.Id).Distinct().Select(id => new ManifestPass(id, "unproven"))]),
      Warnings = ManifestMessage.WarningsOf(context.Diagnostics.Items),
      Toolchains = [],
      Commands = new(CliContext.Invocation(command), null)
    };
  }

  /// <summary>
  /// The path of the input relative to the output directory, with <c>/</c>; only its file name where
  /// the relative path would climb to the root of the file system or cross to another drive, since
  /// such a path names the host's directories as an absolute one does.
  /// </summary>
  /// <param name="output">The full path of the output directory.</param>
  /// <param name="input">The full path of the input.</param>
  /// <returns>The path the manifest records.</returns>
  public static string InputPath(string output, string input) {
    var relative = Path.GetRelativePath(output, input);
    var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
    var depth = output[Path.GetPathRoot(output)!.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries).Length;
    var climbs = relative.Split(separators).TakeWhile(segment => segment == "..").Count();
    return ManifestWriter.PortablePath(Path.IsPathRooted(relative) || climbs >= depth ? Path.GetFileName(input) : relative);
  }

  /// <summary>An option of the command.</summary>
  /// <param name="Name">The option as the command line writes it.</param>
  /// <param name="Value">What the help shows as its value, or <see langword="null"/> for an option that takes none.</param>
  private sealed record OptionSpec(string Name, string? Value);

  /// <summary>A source language of the command.</summary>
  /// <param name="Name">The name <c>--from</c> takes.</param>
  /// <param name="Options">The options only this language has.</param>
  /// <param name="Misuse">What is wrong with the values of those options, or <see langword="null"/>.</param>
  /// <param name="Import">
  /// Turns a file name, its text and the options into a module. When there is none it has written
  /// the reasons and the exit code says of which kind they are.
  /// </param>
  private sealed record Frontend(
    string Name,
    OptionSpec[] Options,
    Func<IReadOnlyDictionary<string, string>, string?> Misuse,
    Func<string, string, IReadOnlyDictionary<string, string>, CliContext, (IrModule? Module, int ExitCode, IReadOnlyList<ManifestMessage> Caveats)> Import) {

    /// <summary>Whether an option belongs to this language.</summary>
    public bool Has(string option) => this.Options.Any(candidate => candidate.Name == option);

  }

  /// <summary>A target language of the command.</summary>
  /// <param name="Name">The name <c>--to</c> takes.</param>
  /// <param name="Runtime">The runtime of the generated code, by its toolchain key.</param>
  /// <param name="ProfileName">The profile the options name, as the header of a generated file states it.</param>
  /// <param name="Target">What the capability table tells the optimizer about the costs of the target.</param>
  /// <param name="Options">The options this language reads; another target may read one of them its own way.</param>
  /// <param name="Misuse">What is wrong with the values of those options, or <see langword="null"/>.</param>
  /// <param name="Lower">The module with the floating-point operations the target lacks replaced by their portable implementations.</param>
  /// <param name="Check">Why a module cannot be emitted with the options given; empty when it can.</param>
  /// <param name="Emit">Writes a module that passed the check, running the target passes the options let run.</param>
  /// <summary>How a source with modules of its own is laid out.</summary>
  /// <param name="Package">The package name the source gives, the folder of a crate's <c>src/lib.rs</c>; <see langword="null"/> for none.</param>
  /// <param name="Modules">The module of each function outside the root, by IR name: its path from the root, <c>util::bits</c>.</param>
  /// <param name="Tests">The functions that are the source's tests, by IR name.</param>
  internal sealed record CrateLayout(string? Package, IReadOnlyDictionary<string, string> Modules, IReadOnlyList<string> Tests);

  private sealed record Backend(
    string Name,
    string Runtime,
    Func<IReadOnlyDictionary<string, string>, string> ProfileName,
    RuleTarget Target,
    OptionSpec[] Options,
    Func<IReadOnlyDictionary<string, string>, string?> Misuse,
    Func<IrModule, IReadOnlyDictionary<string, string>, IrModule> Lower,
    Func<IrModule, IReadOnlyDictionary<string, string>, IReadOnlyList<Diagnostic>> Check,
    Func<IrModule, IReadOnlyDictionary<string, string>, TargetPassOptions, CrateLayout?, TargetEmission> Emit) {

    /// <summary>Whether an option belongs to this language.</summary>
    public bool Has(string option) => this.Options.Any(candidate => candidate.Name == option);

  }

}
