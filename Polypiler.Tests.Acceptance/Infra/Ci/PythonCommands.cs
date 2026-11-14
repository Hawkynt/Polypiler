using System.Collections.Generic;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The command lines of the pinned Python interpreter, as the cross-language runner starts them. Every
/// one passes <c>-I</c>, which keeps the user's environment, site directory and the current directory
/// out of the interpreter, and <c>-B</c>, which writes no bytecode file. The working directory has to
/// be named explicitly where a module of it is imported: <c>unittest discover -s .</c> does that,
/// because <c>-I</c> leaves it off <c>sys.path</c>.
/// </summary>
public static class PythonCommands {

  /// <summary>The key of the interpreter in <c>tooling/toolchains.lock</c>.</summary>
  public const string Toolchain = "python";

  /// <summary>The options every command line starts with.</summary>
  public static IReadOnlyList<string> Isolation { get; } = ["-I", "-B"];

  /// <summary>Runs a script: <c>python -I -B &lt;file&gt;</c>.</summary>
  /// <param name="file">The script, relative to the working directory.</param>
  public static ProcessSpec Run(string file) => new(Toolchain, [.. Isolation, file]);

  /// <summary>
  /// Runs one test module of the working directory under <c>unittest</c>, verbosely:
  /// <c>python -I -B -m unittest discover -v -s . -p &lt;file&gt;</c>.
  /// </summary>
  /// <param name="file">The test module's file name, for example <c>test_adler32.py</c>.</param>
  public static ProcessSpec Test(string file) => new(Toolchain, [.. Isolation, "-m", "unittest", "discover", "-v", "-s", ".", "-p", file]);

}
