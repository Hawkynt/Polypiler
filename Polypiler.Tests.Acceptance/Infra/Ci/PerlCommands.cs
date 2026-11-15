using System.Collections.Generic;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The command lines of the pinned Perl interpreter, as the cross-language runner starts them. The
/// runner gives every step a scrubbed environment, so <c>PERL5LIB</c> and <c>PERL5OPT</c> never reach
/// the interpreter; a module of the working directory is found through <c>-I.</c>.
/// </summary>
public static class PerlCommands {

  /// <summary>The key of the interpreter in <c>tooling/toolchains.lock</c>.</summary>
  public const string Toolchain = "perl";

  /// <summary>
  /// What the <c>prove</c> script of the Perl distribution runs. The runner starts toolchains by key
  /// only, and <c>prove</c> has none of its own, so the interpreter runs the same three statements of
  /// the core module <c>App::Prove</c> and gets the arguments of <c>prove</c> after <c>--</c>.
  /// </summary>
  public const string ProveProgram = "my $app = App::Prove->new; $app->process_args(@ARGV); exit($app->run ? 0 : 1)";

  /// <summary>Compiles a file without running it: <c>perl -c &lt;file&gt;</c>.</summary>
  /// <param name="file">The script or module, relative to the working directory.</param>
  public static ProcessSpec Check(string file) => new(Toolchain, ["-c", file]);

  /// <summary>Runs a script with the working directory on the module path: <c>perl -I. &lt;file&gt;</c>.</summary>
  /// <param name="file">The script, relative to the working directory.</param>
  public static ProcessSpec Run(string file) => new(Toolchain, ["-I.", file]);

  /// <summary>Runs a test file under <c>prove</c> with the working directory on the module path: <c>prove -I. &lt;file&gt;</c>.</summary>
  /// <param name="file">The test file, relative to the working directory.</param>
  public static ProcessSpec Test(string file) => Prove("-I.", file);

  /// <summary><c>prove</c> with its own arguments, as they are.</summary>
  /// <param name="arguments">The arguments of <c>prove</c>.</param>
  public static ProcessSpec Prove(params IReadOnlyList<string> arguments) => new(Toolchain, ["-MApp::Prove", "-e", ProveProgram, "--", .. arguments]);

}
