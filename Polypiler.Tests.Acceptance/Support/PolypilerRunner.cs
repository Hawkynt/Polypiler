using System.IO;
using System.Threading;
using Polypiler.Tests.Acceptance.Prototype;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// Runs <c>polypiler</c> command lines for a scenario in its workspace. The first command line of a
/// scenario, or of the first example row of a scenario outline, starts the real executable, so that
/// every scenario stays bound to what ships; every other runs in process through
/// <c>Program.Run</c>, which <c>InProcessTranslationTests</c> holds to the executable byte for byte.
/// Every result goes to the scenario's <see cref="CommandLog"/>.
/// </summary>
public sealed class PolypilerRunner(ScenarioWorkspace workspace, ScenarioIdentity identity, CommandLog log) {

  private readonly Lock _lock = new();
  private bool _executableStarted;

  /// <summary>Whether the next command line starts the executable.</summary>
  public bool StartsExecutable {
    get {
      lock (this._lock)
        return !this._executableStarted && identity.IsFirstOfItsScenario;
    }
  }

  /// <summary>The arguments of the last command line run.</summary>
  public string[] LastArguments { get; private set; } = [];

  /// <summary>Runs a command line, without the word <c>polypiler</c>, in the workspace.</summary>
  public CommandResult Run(params string[] arguments) {
    bool executable;
    lock (this._lock) {
      this.LastArguments = arguments;
      executable = !this._executableStarted && identity.IsFirstOfItsScenario;
      this._executableStarted = true;
    }

    return log.Add(executable ? Toolchain.Executable(workspace.Path, arguments) : InProcess(workspace.Path, arguments));
  }

  /// <summary>
  /// Runs a command line in process: a translation with a source file and an output directory through
  /// <see cref="Toolchain.TranslateInProcess"/>, a command line that names no file through
  /// <c>Program.Run</c> directly. A translation that names a file but cannot be run in process (no
  /// source file, no output directory) depends on the current directory of a process and starts the
  /// executable.
  /// </summary>
  public static CommandResult InProcess(string workingDirectory, string[] arguments) {
    if (Toolchain.CanTranslateInProcess(workingDirectory, arguments))
      return Toolchain.TranslateInProcess(workingDirectory, arguments);

    if (arguments.Length > 0 && arguments[0] == "translate")
      return Toolchain.Executable(workingDirectory, arguments);

    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = global::Polypiler.Cli.Program.Run(arguments, output, error);
    return new CommandResult(exitCode, output.ToString(), error.ToString());
  }

}
