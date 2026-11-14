namespace Polypiler.Tests.Acceptance.Support;

/// <summary>What a command line did: its exit code and what it wrote to each stream.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Stdout">Everything written to standard output.</param>
/// <param name="Stderr">Everything written to standard error.</param>
public sealed record CommandResult(int ExitCode, string Stdout, string Stderr) {

  /// <summary>Both streams, standard output first, as a reader of the console sees them.</summary>
  public string Output => this.Stdout + this.Stderr;

}
