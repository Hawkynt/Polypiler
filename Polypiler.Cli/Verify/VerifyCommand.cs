using System;
using System.CommandLine;
using System.IO;
using System.Linq;

namespace Polypiler.Cli.Verify;

/// <summary>
/// The <c>verify</c> command (PRD §1.2, §10):
/// <c>polypiler verify &lt;directory&gt; [--against &lt;vectors&gt;] [--no-update] [--evidence &lt;directory&gt;]</c>.
/// It checks a generated directory against its manifest and independent vectors through
/// <see cref="ArtefactVerifier"/>; the exit code is the one of the first error it reported.
/// </summary>
public sealed class VerifyCommand : ICliCommand {

  private const string Against = "--against";
  private const string NoUpdate = "--no-update";
  private const string Evidence = "--evidence";

  private readonly Func<TextWriter, string, ArtefactVerifier> _verifier;

  /// <summary>Creates the command, which verifies on this host.</summary>
  public VerifyCommand() : this((output, workingDirectory) => new ArtefactVerifier(output, workingDirectory)) { }

  /// <summary>Creates the command with a verifier of its own.</summary>
  /// <param name="verifier">Creates the verifier from standard output and the working directory.</param>
  /// <exception cref="ArgumentNullException"><paramref name="verifier"/> is <see langword="null"/>.</exception>
  public VerifyCommand(Func<TextWriter, string, ArtefactVerifier> verifier) {
    ArgumentNullException.ThrowIfNull(verifier);
    this._verifier = verifier;
  }

  /// <inheritdoc/>
  public string Name => "verify";

  /// <inheritdoc/>
  public string Description => "check that a generated directory still builds, runs and matches independent test vectors";

  /// <inheritdoc/>
  public void Configure(Command command) {
    ArgumentNullException.ThrowIfNull(command);
    command.Arguments.Add(new Argument<string>("directory") { Description = "the generated directory, which holds polypiler.manifest.json" });
    command.Options.Add(new Option<string>(Against) { Description = "the vector file to compare with; by default the one the manifest records", HelpName = "vectors" });
    command.Options.Add(new Option<bool>(NoUpdate) { Description = "leave the manifest as it is" });
    command.Options.Add(new Option<string>(Evidence) { Description = "write an evidence bundle into this directory", HelpName = "directory" });
  }

  /// <inheritdoc/>
  public int Execute(ParseResult parse, CliContext context) {
    ArgumentNullException.ThrowIfNull(parse);
    ArgumentNullException.ThrowIfNull(context);
    var symbol = parse.CommandResult.Command;
    var directory = parse.GetValue((Argument<string>)symbol.Arguments[0])!;
    var request = new VerifyRequest(directory, Value(Against), !parse.GetValue((Option<bool>)Find(NoUpdate)), Value(Evidence));
    this._verifier(context.Output, context.WorkingDirectory).Run(request, context);
    return 0;

    Option Find(string name) => symbol.Options.Single(option => option.Name == name);

    string? Value(string name) => parse.GetValue((Option<string>)Find(name));
  }

}
