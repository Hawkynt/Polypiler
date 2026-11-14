using System.CommandLine;

namespace Polypiler.Cli;

/// <summary>
/// The command options that mean the same on every command that has them (ADR 0004, "Naming"),
/// declared once here and attached by each command that takes them. Every member gives a new
/// option, so that one command may make it required without changing it for another; the command
/// keeps the instance it attached to read the value.
/// </summary>
public static class CommonOptions {

  /// <summary><c>--output &lt;path&gt;</c>: where the result goes, a directory for a command that produces an artefact.</summary>
  /// <remarks>A path as typed rather than a <c>FileSystemInfo</c>: the empty value is a value of the grammar, and <see cref="CliPaths"/> reports it.</remarks>
  public static Option<string> Output => new("--output") { Description = "where the result goes", HelpName = "path" };

  /// <summary><c>--force</c>: write into an output directory that is not empty.</summary>
  public static Option<bool> Force => new("--force") { Description = "write into an output directory that is not empty", Arity = ArgumentArity.Zero };

  /// <summary><c>--from &lt;language&gt;</c>: the identifier of the source language or format.</summary>
  public static Option<string> From => new("--from") { Description = "the source language", HelpName = "language" };

  /// <summary><c>--to &lt;language&gt;</c>: the identifier of the target language or format.</summary>
  public static Option<string> To => new("--to") { Description = "the target language", HelpName = "language" };

  /// <summary><c>--source-profile &lt;profile&gt;</c>: the profile of the source language.</summary>
  public static Option<string> SourceProfile => new("--source-profile") { Description = "the profile of the source language", HelpName = "profile" };

  /// <summary><c>--target-profile &lt;profile&gt;</c>: the profile of the target language.</summary>
  public static Option<string> TargetProfile => new("--target-profile") { Description = "the profile of the target language", HelpName = "profile" };

  /// <summary><c>--export &lt;name&gt;[,&lt;name&gt;...]</c>: the functions to take, a list.</summary>
  public static Option<string[]> Export => new("--export") { Description = "the functions to take", HelpName = "name" };

}
