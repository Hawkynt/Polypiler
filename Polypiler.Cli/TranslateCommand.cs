using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Cli;

/// <summary>
/// The <c>translate</c> command of the prototype:
/// <c>polypiler translate &lt;file.cs&gt; --from csharp --to javascript --output &lt;directory&gt;</c>.
/// </summary>
public static class TranslateCommand {

  /// <summary>The code for a command line that names an unknown language or lacks an argument.</summary>
  public const string Usage = "PLY0001";

  /// <summary>Exit code when the source uses a construct that is not supported.</summary>
  public const int UnsupportedConstruct = 3;

  /// <summary>Exit code when the source is not valid in its own language.</summary>
  public const int InvalidSource = 4;

  /// <summary>Exit code for an error inside Polypiler itself.</summary>
  public const int InternalError = 70;

  private static readonly string[] _options = ["--from", "--to", "--output"];

  /// <summary>Runs the command.</summary>
  /// <param name="arguments">The arguments after the word <c>translate</c>.</param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics.</param>
  /// <returns>The process exit code.</returns>
  public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error) {
    string? file = null;
    var options = new Dictionary<string, string>();
    for (var index = 0; index < arguments.Count; ++index) {
      var argument = arguments[index];
      if (!argument.StartsWith("--", StringComparison.Ordinal)) {
        if (file is not null)
          return Refuse(error, $"more than one source file: '{file}' and '{argument}'");

        file = argument;
      } else if (!_options.Contains(argument)) {
        return Refuse(error, $"unknown option '{argument}'");
      } else if (index + 1 == arguments.Count) {
        return Refuse(error, $"the option '{argument}' needs a value");
      } else {
        options[argument] = arguments[++index];
      }
    }

    if (file is null)
      return Refuse(error, "no source file was given");

    foreach (var option in _options)
      if (!options.ContainsKey(option))
        return Refuse(error, $"the option '{option}' is missing");

    if (options["--from"] != "csharp")
      return Refuse(error, $"unknown source language '{options["--from"]}'; the prototype reads 'csharp'");

    if (options["--to"] != "javascript")
      return Refuse(error, $"unknown target language '{options["--to"]}'; the prototype writes 'javascript'");

    string source;
    try {
      source = File.ReadAllText(file);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return Refuse(error, $"the source file '{file}' cannot be read: {exception.Message}");
    }

    var imported = CSharpFrontend.Import(file, source);
    if (imported.Module is null) {
      foreach (var diagnostic in imported.Diagnostics)
        error.WriteLine(diagnostic);

      return imported.Diagnostics.Any(diagnostic => diagnostic.Code == CSharpFrontend.CompilerError)
        ? InvalidSource
        : UnsupportedConstruct;
    }

    return Emit(imported.Module, options["--output"], output, error);
  }

  /// <summary>Verifies a module and writes its JavaScript into a directory.</summary>
  /// <param name="module">The module to emit.</param>
  /// <param name="directory">The directory to write into; it is created when it does not exist.</param>
  /// <param name="output">Receives the names of the files written.</param>
  /// <param name="error">Receives the diagnostics.</param>
  /// <returns>The process exit code.</returns>
  public static int Emit(IrModule module, string directory, TextWriter output, TextWriter error) {
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0) {
      foreach (var problem in problems)
        error.WriteLine(problem);

      return InternalError;
    }

    try {
      Directory.CreateDirectory(directory);
      foreach (var generated in JavaScriptEmitter.Emit(module)) {
        var path = Path.Combine(directory, generated.Path);
        File.WriteAllText(path, generated.Content);
        output.WriteLine(path);
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return Refuse(error, $"the output directory '{directory}' cannot be written: {exception.Message}");
    }

    return Program.Success;
  }

  private static int Refuse(TextWriter error, string message) {
    error.WriteLine(new Diagnostic(Usage, DiagnosticSeverity.Error, message));
    error.WriteLine($"usage: {ProductInfo.Name} translate <file.cs> --from csharp --to javascript --output <directory>");
    return Program.UsageError;
  }

}
