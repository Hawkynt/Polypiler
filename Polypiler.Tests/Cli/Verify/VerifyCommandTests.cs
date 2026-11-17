using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Cli.Verify;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Cli.Verify.Support;

namespace Polypiler.Tests.Cli.Verify;

/// <summary><c>polypiler verify</c> on the command line, in process, with a scripted node.</summary>
[TestFixture]
public sealed class VerifyCommandTests {

  private VerifyWorkspace _workspace = null!;
  private ScriptedNode _node = null!;

  [SetUp]
  public void CopyTheFixtures() {
    this._workspace = new VerifyWorkspace();
    this._node = new ScriptedNode();
  }

  [TearDown]
  public void DeleteTheCopy() => this._workspace.Dispose();

  private (int ExitCode, string Output, string Error) Invoke(params string[] arguments) {
    var registry = new CommandRegistry();
    registry.Add(new VerifyCommand((output, workingDirectory) => new ArtefactVerifier(output, workingDirectory, this._node, RunLimits.Default)));
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(arguments, output, error, registry, this._workspace.Root);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void Verify_VerifiesTheDirectory_AndExitsWithZero() {
    var (exitCode, output, error) = this.Invoke("verify", "copy", "--against", VerifyWorkspace.Vectors);

    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1], Is.EqualTo("copy: verified (4 of 4 vectors)"));
    Assert.That(this._workspace.Manifest.State, Is.EqualTo(ManifestState.Verified));
  }

  [Test]
  public void NoUpdate_AndEvidence_ReachTheVerifier() {
    var (exitCode, _, _) = this.Invoke("verify", "copy", "--against=" + VerifyWorkspace.Vectors, "--no-update", "--evidence", "bundles");

    Assert.That(exitCode, Is.Zero);
    Assert.That(this._workspace.Manifest.State, Is.EqualTo(ManifestState.Generated));
    Assert.That(File.Exists(Path.Combine(this._workspace.Root, "bundles", "polypiler-verify", "copy", "evidence.json")), Is.True);
  }

  [Test]
  public void AMismatch_ExitsWithFive() {
    var (exitCode, output, error) = this.Invoke("verify", "copy", "--against", VerifyWorkspace.WrongVectors);

    Assert.That(exitCode, Is.EqualTo(5));
    Assert.That(output, Does.Contain("FAIL expected 0x024D0128 actual 0x024D0127"));
    Assert.That(error, Does.Contain("error PLY0051"));
  }

  [Test]
  public void ADirectoryWithoutAManifest_ExitsWithOne() {
    var (exitCode, _, error) = this.Invoke("verify", "no-manifest", "--against", VerifyWorkspace.Vectors);

    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.Contain("error PLY0016: no manifest in 'no-manifest'"));
  }

  [Test]
  public void WithoutTheDirectory_TheCommandLineIsRefused() {
    var (exitCode, output, error) = this.Invoke("verify");

    Assert.That((exitCode, output), Is.EqualTo((2, "")));
    Assert.That(error, Does.Contain("PLY0003: missing argument <directory>"));
    Assert.That(this._node.Plans, Is.Empty);
  }

  [Test]
  public void TheHelp_NamesTheOperandAndTheOptions() {
    var (exitCode, output, _) = this.Invoke("verify", "--help");

    Assert.That(exitCode, Is.Zero);
    Assert.That(output, Does.Contain("<directory>").And.Contain("--against <vectors>").And.Contain("--no-update").And.Contain("--evidence <directory>"));
  }

  [Test]
  public void TheShippedRegistry_HasVerify() {
    var verify = Program.CreateRegistry().Find("verify");

    Assert.That(verify, Is.InstanceOf<VerifyCommand>());
    Assert.That(verify!.Description, Does.StartWith("check that a generated directory still builds"));
  }

  [Test]
  public void TheArguments_AreChecked() {
    var command = new VerifyCommand();
    Assert.Multiple(() => {
      Assert.That(() => new VerifyCommand(null!), Throws.ArgumentNullException);
      Assert.That(() => command.Configure(null!), Throws.ArgumentNullException);
      Assert.That(() => command.Execute(null!, new CliContext(TextWriter.Null, TextWriter.Null, "", DiagnosticsFormat.Text, false, "")), Throws.ArgumentNullException);
      Assert.That(() => command.Execute(new RootCommand().Parse([]), null!), Throws.ArgumentNullException);
    });
  }

}
