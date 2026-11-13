using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Cli;

/// <summary>
/// The <c>translate</c> command of the prototype:
/// <c>polypiler translate &lt;file&gt; --from &lt;csharp|javascript&gt; --to &lt;javascript|csharp&gt; --output &lt;directory&gt;</c>.
/// </summary>
public static class TranslateCommand {

  /// <summary>The code for a command line that names an unknown language or lacks an argument.</summary>
  public const string Usage = "PLY0001";

  /// <summary>Exit code when the source, or the IR for the chosen target, uses a construct that is not supported.</summary>
  public const int UnsupportedConstruct = 3;

  /// <summary>Exit code when the source is not valid in its own language.</summary>
  public const int InvalidSource = 4;

  /// <summary>Exit code for an error inside Polypiler itself.</summary>
  public const int InternalError = 70;

  private const string From = "--from";
  private const string To = "--to";
  private const string Output = "--output";
  private const string Namespace = "--namespace";
  private const string Export = "--export";

  private static readonly string[] _required = [From, To, Output];

  /// <summary>The source languages; another one is another entry.</summary>
  private static readonly Frontend[] _frontends = [
    new("csharp", [], _ => null, ImportCSharp),
    new("javascript", [Export], ExportMisuse, ImportJavaScript)
  ];

  /// <summary>The target languages; another one is another entry.</summary>
  private static readonly Backend[] _backends = [
    new("javascript", [], _ => null, IrVerifier.Verify, (module, _) => JavaScriptEmitter.Emit(module)),
    new("csharp", [Namespace], NamespaceMisuse, CSharpEmitter.Check, (module, options) => CSharpEmitter.Emit(module, options.GetValueOrDefault(Namespace)))
  ];

  /// <summary>How the command is called, as the help and every usage error show it.</summary>
  public static string UsageLine { get; } =
    $"{ProductInfo.Name} translate <file> {From} {Choice(_frontends.Select(frontend => frontend.Name))}"
    + $" {To} {Choice(_backends.Select(backend => backend.Name))} {Output} <directory>"
    + string.Concat(_frontends.SelectMany(frontend => frontend.Options).Select(option => $" [{option} <name>[,<name>...]]"))
    + string.Concat(_backends.SelectMany(backend => backend.Options).Select(option => $" [{option} <name>]"));

  /// <summary>Runs the command.</summary>
  /// <param name="arguments">The arguments after the word <c>translate</c>.</param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics and the caveats of an import.</param>
  /// <returns>The process exit code.</returns>
  public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error) {
    string? file = null;
    var known = _required
      .Concat(_frontends.SelectMany(frontend => frontend.Options))
      .Concat(_backends.SelectMany(backend => backend.Options))
      .ToList();
    var options = new Dictionary<string, string>();
    for (var index = 0; index < arguments.Count; ++index) {
      var argument = arguments[index];
      if (!argument.StartsWith("--", StringComparison.Ordinal)) {
        if (file is not null)
          return Refuse(error, $"more than one source file: '{file}' and '{argument}'");

        file = argument;
      } else if (!known.Contains(argument)) {
        return Refuse(error, $"unknown option '{argument}'");
      } else if (index + 1 == arguments.Count) {
        return Refuse(error, $"the option '{argument}' needs a value");
      } else {
        options[argument] = arguments[++index];
      }
    }

    if (file is null)
      return Refuse(error, "no source file was given");

    foreach (var option in _required)
      if (!options.ContainsKey(option))
        return Refuse(error, $"the option '{option}' is missing");

    var frontend = _frontends.FirstOrDefault(candidate => candidate.Name == options[From]);
    if (frontend is null)
      return Refuse(error, $"unknown source language '{options[From]}'; the prototype reads {Listed(_frontends.Select(candidate => candidate.Name))}");

    var backend = _backends.FirstOrDefault(candidate => candidate.Name == options[To]);
    if (backend is null)
      return Refuse(error, $"unknown target language '{options[To]}'; the prototype writes {Listed(_backends.Select(candidate => candidate.Name))}");

    // An option that is not required belongs to a source or to a target language, and only to the chosen one.
    var foreign = options.Keys.FirstOrDefault(option => !_required.Contains(option) && !frontend.Options.Contains(option) && !backend.Options.Contains(option));
    var misuse = foreign is null ? frontend.Misuse(options) ?? backend.Misuse(options)
      : _frontends.Any(candidate => candidate.Options.Contains(foreign)) ? $"the option '{foreign}' does not apply to the source language '{frontend.Name}'"
      : $"the option '{foreign}' does not apply to the target language '{backend.Name}'";
    if (misuse is not null)
      return Refuse(error, misuse);

    string source;
    try {
      source = File.ReadAllText(file);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return Refuse(error, $"the source file '{file}' cannot be read: {exception.Message}");
    }

    var (module, exitCode) = frontend.Import(file, source, options, error);
    return module is null ? exitCode : Emit(module, options, output, error);
  }

  /// <summary>Checks a module and writes it in the target language into the output directory.</summary>
  /// <param name="module">The module to emit.</param>
  /// <param name="options">
  /// The options of a command line that <see cref="Run"/> accepts, by their names: <c>--to</c> with
  /// the target language, <c>--output</c> with the directory to write into, which is created when it
  /// does not exist, and the options of that target language.
  /// </param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics.</param>
  /// <returns>The process exit code.</returns>
  public static int Emit(IrModule module, IReadOnlyDictionary<string, string> options, TextWriter output, TextWriter error) {
    var backend = _backends.First(candidate => candidate.Name == options[To]);
    var problems = backend.Check(module);
    if (problems.Count > 0) {
      foreach (var problem in problems)
        error.WriteLine(problem);

      return problems.Any(problem => problem.Code == IrVerifier.Code) ? InternalError : UnsupportedConstruct;
    }

    var directory = options[Output];
    try {
      Directory.CreateDirectory(directory);
      foreach (var generated in backend.Emit(module, options)) {
        var path = Path.Combine(directory, generated.Path);
        File.WriteAllText(path, generated.Content);
        output.WriteLine(path);
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return Refuse(error, $"the output directory '{directory}' cannot be written: {exception.Message}");
    }

    return Program.Success;
  }

  private static (IrModule? Module, int ExitCode) ImportCSharp(string file, string source, IReadOnlyDictionary<string, string> options, TextWriter error) {
    var imported = CSharpFrontend.Import(file, source);
    foreach (var diagnostic in imported.Diagnostics)
      error.WriteLine(diagnostic);

    var failure = imported.Diagnostics.Any(diagnostic => diagnostic.Code is CSharpFrontend.CompilerError or CSharpFrontend.ConstantOverflow)
      ? InvalidSource
      : UnsupportedConstruct;
    return (imported.Module, failure);
  }

  /// <summary>
  /// Imports JavaScript, all of the file or the functions <c>--export</c> names. What the imported
  /// code is known to do differently from the original is written as a note; it is no error.
  /// </summary>
  private static (IrModule? Module, int ExitCode) ImportJavaScript(string file, string source, IReadOnlyDictionary<string, string> options, TextWriter error) {
    var imported = JavaScriptFrontend.Import(file, source, options.TryGetValue(Export, out var names) ? Names(names) : null);
    foreach (var diagnostic in imported.Diagnostics)
      error.WriteLine(diagnostic);

    foreach (var caveat in imported.Caveats)
      error.WriteLine($"{file}: note {caveat.Id}: {caveat.Description}");

    var failure = imported.Diagnostics.Any(diagnostic => diagnostic.Code == JavaScriptFrontend.SyntaxError)
      ? InvalidSource
      : UnsupportedConstruct;
    return (imported.Module, failure);
  }

  private static string[] Names(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  private static string? ExportMisuse(IReadOnlyDictionary<string, string> options)
    => options.TryGetValue(Export, out var names) && Names(names).Length == 0 ? $"the option '{Export}' names no function" : null;

  private static string? NamespaceMisuse(IReadOnlyDictionary<string, string> options)
    => options.TryGetValue(Namespace, out var name) && !CSharpEmitter.IsNamespace(name) ? $"'{name}' is not a namespace name" : null;

  private static string Choice(IEnumerable<string> names) => $"<{string.Join('|', names)}>";

  private static string Listed(IEnumerable<string> names) => string.Join(" and ", names.Select(name => $"'{name}'"));

  private static int Refuse(TextWriter error, string message) {
    error.WriteLine(new Diagnostic(Usage, DiagnosticSeverity.Error, message));
    error.WriteLine("usage: " + UsageLine);
    return Program.UsageError;
  }

  /// <summary>A source language of the command.</summary>
  /// <param name="Name">The name <c>--from</c> takes.</param>
  /// <param name="Options">The options only this language has; each takes a list of names.</param>
  /// <param name="Misuse">What is wrong with the values of those options, or <see langword="null"/>.</param>
  /// <param name="Import">
  /// Turns a file name, its text and the options into a module. When there is none it has written
  /// the reasons and the exit code says of which kind they are.
  /// </param>
  private sealed record Frontend(
    string Name,
    string[] Options,
    Func<IReadOnlyDictionary<string, string>, string?> Misuse,
    Func<string, string, IReadOnlyDictionary<string, string>, TextWriter, (IrModule? Module, int ExitCode)> Import);

  /// <summary>A target language of the command.</summary>
  /// <param name="Name">The name <c>--to</c> takes.</param>
  /// <param name="Options">The options only this language has.</param>
  /// <param name="Misuse">What is wrong with the values of those options, or <see langword="null"/>.</param>
  /// <param name="Check">Why a module cannot be emitted; empty when it can.</param>
  /// <param name="Emit">Writes a module that passed the check.</param>
  private sealed record Backend(
    string Name,
    string[] Options,
    Func<IReadOnlyDictionary<string, string>, string?> Misuse,
    Func<IrModule, IReadOnlyList<Diagnostic>> Check,
    Func<IrModule, IReadOnlyDictionary<string, string>, IReadOnlyList<GeneratedFile>> Emit);

}
