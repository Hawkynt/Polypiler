using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The diagnostics of <c>polypiler translate</c> beyond the grammar (#180): an identifier no registry
/// knows, a known language the command has no component for, an export the input does not have, and a
/// toolchain <c>--verify</c> needs that this machine lacks.
/// </summary>
public static class TranslateDiagnostics {

  /// <summary><c>PLY0006</c>: <c>--from</c>, <c>--to</c> or a profile names an identifier no registry knows; the message is <see cref="UnknownLanguage"/> or <see cref="UnknownProfile"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnknownIdentifier = new("PLY0006", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "adr-0004.identifiers");

  /// <summary><c>PLY0013</c>: an export the input does not have, or an input without any exportable function; the message is <see cref="ExportNotFound"/> or <see cref="NothingToExport"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Export = new("PLY0013", DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "translate.export");

  /// <summary><c>PLY0014</c>: a known language without a frontend or a backend for the profile; the message is <see cref="NoFrontend"/> or <see cref="NoBackend"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoComponent = new("PLY0014", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "translate.components");

  /// <summary><c>PLY0021</c>: the toolchain <c>--verify</c> builds and runs the output with is not installed; the message names the key and the probe.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ToolchainMissing = new("PLY0021", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "toolchains.installed");

  /// <summary>A language identifier no registry knows: the identifier.</summary>
  public const string UnknownLanguage = "unknown language '{0}'";

  /// <summary>A profile the language does not have: the language and the profile.</summary>
  public const string UnknownProfile = "unknown profile '{1}' for language '{0}'";

  /// <summary>An export the input does not have: the name and the input as typed.</summary>
  public const string ExportNotFound = "export '{0}' not found in '{1}'";

  /// <summary>An input without an exportable function: the input as typed.</summary>
  public const string NothingToExport = "'{0}' contains no exportable function";

  /// <summary>A known source language without a frontend: the language and the profile.</summary>
  public const string NoFrontend = "no frontend for '{0}' with profile '{1}'";

  /// <summary>A known target language without a backend: the language and the profile.</summary>
  public const string NoBackend = "no backend for '{0}' with profile '{1}'";

  /// <summary>A toolchain that is not on the search path: the key and the executable looked for.</summary>
  public const string NotInstalled = "toolchain '{0}' is not installed (probe: {1} --version)";

}
