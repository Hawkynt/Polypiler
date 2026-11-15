using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The commands of the JS/TS layer (<c>tooling/ci/layers/js.yaml</c>), each a step of the
/// cross-language runner: <c>node &lt;file&gt;</c>, <c>node --test &lt;file&gt;</c> and
/// <c>tsc --noEmit -p &lt;dir&gt;</c>. JavaScript runs as written, never transpiled; the TypeScript
/// checker never emits, and no <c>npm install</c> runs in the sandbox.
/// </summary>
public static class JsCommands {

  /// <summary>The toolchain key of Node.js in <c>tooling/toolchains.lock</c>.</summary>
  public const string Node = "node";

  /// <summary>The toolchain key of the TypeScript checker <c>tsc</c> in <c>tooling/toolchains.lock</c>.</summary>
  public const string TypeScript = "typescript";

  /// <summary>The project file whose presence makes a fixture typed: the type check runs for it.</summary>
  public const string ProjectFile = "tsconfig.json";

  /// <summary>What a fixture without <see cref="ProjectFile"/> records for the type check.</summary>
  public const string NotApplicable = "typescript: not applicable";

  /// <summary>What a fixture with <see cref="ProjectFile"/> records for the type check.</summary>
  public const string Checked = "typescript: checked";

  /// <summary>The smoke fixtures of the layer.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "ci", "js");

  /// <summary>Runs a module: <c>node &lt;file&gt; [arguments]</c>.</summary>
  /// <param name="file">The module, relative to the working directory.</param>
  /// <param name="arguments">What the module reads from <c>process.argv</c> after its own name.</param>
  public static ProcessSpec Run(string file, params string[] arguments) => new(Node, [file, .. arguments]);

  /// <summary>Runs a test file with the built-in test runner: <c>node --test &lt;file&gt;</c>.</summary>
  /// <param name="file">The test file, relative to the working directory.</param>
  public static ProcessSpec Test(string file) => new(Node, "--test", file);

  /// <summary>Type-checks the project of a directory without emitting: <c>tsc --noEmit -p &lt;dir&gt;</c>.</summary>
  /// <param name="directory">The directory holding <see cref="ProjectFile"/>, relative to the working directory.</param>
  public static ProcessSpec TypeCheck(string directory) => new(TypeScript, "--noEmit", "-p", directory);

  /// <summary>Type-checks one file without a project and without emitting: <c>tsc --noEmit &lt;file&gt;</c>.</summary>
  /// <param name="file">The file, relative to the working directory.</param>
  public static ProcessSpec TypeCheckFile(string file) => new(TypeScript, "--noEmit", file);

  /// <summary>The type check of a fixture: <see cref="TypeCheck"/> of its root when it has a <see cref="ProjectFile"/>, none otherwise.</summary>
  /// <param name="files">The fixture's files, by name relative to its root.</param>
  public static ProcessSpec? TypeCheckOf(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    => files.ContainsKey(ProjectFile) ? TypeCheck(".") : null;

  /// <summary>What a fixture records for the type check: <see cref="Checked"/> or <see cref="NotApplicable"/>.</summary>
  /// <param name="files">The fixture's files, by name relative to its root.</param>
  public static string TypeScriptRecord(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    => TypeCheckOf(files) is null ? NotApplicable : Checked;

  /// <summary>Files of <see cref="FixtureFolder"/>, by their names.</summary>
  /// <param name="names">The names, relative to the folder.</param>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Fixtures(params string[] names)
    => names.ToDictionary(name => name, name => (ReadOnlyMemory<byte>)File.ReadAllBytes(Path.Combine(FixtureFolder, name)), StringComparer.Ordinal);

  /// <summary>
  /// Runs one command on the files in the sandbox of this host, as trusted code with the default
  /// limits, and hands back every file the directory held at the end. The command, its outcome, exit
  /// code and both streams go to the test's output, the evidence the layer's job keeps.
  /// </summary>
  /// <param name="files">The files of the working directory.</param>
  /// <param name="command">The command.</param>
  public static async Task<RunResult> RunAsync(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, ProcessSpec command) {
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(new(files, [], command, RunLimits.Default, TrustLevel.Trusted) { Collect = ["."] });
    var run = ToCommandResult(result);
    TestContext.Out.WriteLine($"$ {command} -> {result.Outcome}, exit code {result.ExitCode}, {result.Isolation}");
    TestContext.Out.WriteLine($"--- stdout\n{run.Stdout}--- stderr\n{run.Stderr}---");
    return result;
  }

  /// <summary>A run as the command steps of the features check it: exit code and both streams decoded.</summary>
  /// <param name="result">The run.</param>
  public static CommandResult ToCommandResult(RunResult result)
    => new(result.ExitCode, Encoding.UTF8.GetString(result.Stdout.Span), Encoding.UTF8.GetString(result.Stderr.Span));

}
