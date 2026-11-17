using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Cli.Translate;

/// <summary>A runner that starts nothing: it records the plan and answers with a result of the test's.</summary>
internal sealed class FakeRunner(Func<RunPlan, RunResult> answer) : ICrossLanguageRunner {

  /// <summary>The plans it was given, in order.</summary>
  public List<RunPlan> Plans { get; } = [];

  public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
    this.Plans.Add(plan);
    return Task.FromResult(answer(plan));
  }

  /// <summary>A run that built and ended with an exit code and standard output.</summary>
  public static RunResult Completed(string stdout, int exitCode = 0)
    => new(RunOutcome.Completed, exitCode, Encoding.UTF8.GetBytes(stdout), ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, []);

  /// <summary>A run that ended otherwise, with its diagnostics.</summary>
  public static RunResult Ended(RunOutcome outcome, params Diagnostic[] diagnostics)
    => new(outcome, -1, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics);

}

/// <summary>A working directory with the fixtures of <c>tests/fixtures/cli/translate</c> and the command run in it.</summary>
internal sealed class TranslateWorkspace : IDisposable {

  /// <summary>The fixtures, relative to the workspace as to the repository.</summary>
  public const string Fixtures = "tests/fixtures/cli/translate";

  public TranslateWorkspace() {
    this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "polypiler-translate-" + Guid.NewGuid().ToString("N"));
    var target = System.IO.Path.Combine(this.Path, Fixtures);
    Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(System.IO.Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures)))
      File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
  }

  /// <summary>The directory.</summary>
  public string Path { get; }

  /// <summary>A path inside the workspace.</summary>
  public string Resolve(string relative) => System.IO.Path.Combine(this.Path, relative);

  /// <summary>Writes a file into the workspace and gives its relative path.</summary>
  public string Write(string relative, string text) {
    var full = this.Resolve(relative);
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
    File.WriteAllText(full, text);
    return relative;
  }

  /// <summary>Runs <c>polypiler</c> with the standard registry, or with <c>translate</c> verifying through a runner of the test's.</summary>
  public (int ExitCode, string Output, string Error) Run(ICrossLanguageRunner? runner, params string[] arguments) {
    var registry = Program.CreateRegistry();
    if (runner is not null) {
      registry = new CommandRegistry();
      registry.Add(new TranslateCommand(new TranslationVerifier(runner)));
    }

    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(arguments, output, error, registry, this.Path);
    return (exitCode, output.ToString(), error.ToString());
  }

  /// <summary>Runs <c>polypiler</c> with the standard registry.</summary>
  public (int ExitCode, string Output, string Error) Run(params string[] arguments) => this.Run(null, arguments);

  /// <summary>The manifest of an output directory, read and checked against the schema.</summary>
  public Manifest Manifest(string output) {
    var bag = new DiagnosticBag();
    var manifest = ManifestReader.Read(System.IO.Path.Combine(this.Resolve(output), Polypiler.Core.Verification.Manifests.Manifest.FileName), bag);
    if (manifest is null || bag.Items.Count > 0)
      throw new InvalidDataException(string.Join("\n", bag.Items));

    return manifest;
  }

  public void Dispose() => Directory.Delete(this.Path, true);

}
