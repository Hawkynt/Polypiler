using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Interop;

/// <summary>
/// <c>translate --bind</c> (#1332, ADR 0007): a native call to JavaScript needs the binding of its library, which the
/// option names once per library; a malformed binding, one named twice and one of a library the module does not call
/// are a command line not understood. The manifest records every native function called through a binding as an
/// unverified external and every library as a native dependency.
/// </summary>
[TestFixture]
public sealed class ForeignCliTests {

  private string _directory = "";
  private string _out = "";
  private string _source = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-bind-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
    this._source = Path.Combine(this._directory, "LibC.cs");
    File.WriteAllText(this._source, InteropFixtures.SourceOf("LibC.Linux.cs"));
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private (int ExitCode, string Output, string Error) Translate(params string[] options) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", this._source, "--from", "csharp", "--output", this._out, .. options], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private Manifest Manifest() {
    var bag = new DiagnosticBag();
    var manifest = ManifestReader.Read(Path.Combine(this._out, Polypiler.Core.Verification.Manifests.Manifest.FileName), bag);
    Assert.That(bag.Items, Is.Empty);
    return manifest!;
  }

  /// <summary>Test design 12: without a binding, every native function is refused, exit 3, and nothing is written.</summary>
  [Test]
  public void WithoutABinding_TheTranslationIsRefused() {
    var (exitCode, _, error) = this.Translate("--to", "javascript", "--profile", "es2022");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct), error);
    Assert.That(error, Does.Contain("PLY1152").And.Contain("'strlen' (libc.so.6) is a native function"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>Test design 15: with a binding the module imports it, and the manifest records the externals and the library.</summary>
  [TestCase("es2022", "esm", null, "./libc-bindings.mjs", "import * as $libc_so_6 from \"./libc-bindings.mjs\";")]
  [TestCase("es5", "cjs", null, "./libc-bindings.mjs", "var $libc_so_6 = require(\"./libc-bindings.mjs\");")]
  [TestCase("es5", "iife", "LibCModule", "globalThis.LibC", "var $libc_so_6 = globalThis.LibC;")]
  public void WithABinding_TheModuleCallsIt(string level, string module, string? global, string binding, string reached) {
    var (exitCode, output, error) = this.Translate(["--to", "javascript", "--profile", level, "--module", module, .. global is null ? Array.Empty<string>() : ["--global", global], "--bind", $"libc.so.6={binding}"]);
    Assert.That(exitCode, Is.Zero, error);
    Assert.That(File.ReadAllText(output.Trim()), Does.Contain(reached));
    var manifest = this.Manifest();
    Assert.That(manifest.Caveats.Where(caveat => caveat.Code == TranslateCommand.UnverifiedExternal).Select(caveat => caveat.Message).ToList(),
      Has.Count.EqualTo(6).And.Some.Contains($"'strtol' of libc.so.6 is called through the binding {binding}"));
    Assert.That(manifest.NativeDependencies, Is.EqualTo(new[] { new ManifestNativeDependency("libc.so.6", "unspecified", "NOASSERTION") }));
    Assert.That(manifest.Command, Does.Contain("--bind").And.Contain($"libc.so.6={binding}"));
  }

  /// <summary>C# keeps the native calls, records the library, and no caveat: .NET calls the library itself.</summary>
  [Test]
  public void CSharp_RecordsTheLibraryAndNoCaveat() {
    var (exitCode, _, error) = this.Translate("--to", "csharp", "--profile", "net4.8");
    Assert.That(exitCode, Is.Zero, error);
    var manifest = this.Manifest();
    Assert.That(manifest.Caveats.Select(caveat => caveat.Code), Does.Not.Contain(TranslateCommand.UnverifiedExternal));
    Assert.That(manifest.NativeDependencies.Select(dependency => dependency.Name), Is.EqualTo(new[] { "libc.so.6" }));
  }

  /// <summary>Test design 16: what the command line cannot use is PLY0003, exit 2, and nothing is written.</summary>
  [TestCase("javascript", "libc.so.6", "'libc.so.6' is no binding")]
  [TestCase("javascript", "=./b.mjs", "binds no library or to nothing")]
  [TestCase("javascript", "libc.so.6=", "binds no library or to nothing")]
  [TestCase("javascript", "libc.so.6=./a.mjs,libc.so.6=./b.mjs", "the library 'libc.so.6' is bound twice")]
  [TestCase("javascript", "libc.so.6=./a.mjs,other.so=./b.mjs", "binds the library 'other.so', which the module calls nothing of")]
  [TestCase("csharp", "libc.so.6=./a.mjs", "the option '--bind' does not apply to the target language 'csharp'")]
  public void AnUnusableBinding_IsRefused(string to, string binding, string problem) {
    var (exitCode, _, error) = this.Translate(["--to", to, .. binding.Split(',').SelectMany(item => new[] { "--bind", item })]);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError), error);
    Assert.That(error, Does.Contain("PLY0003").And.Contain(problem));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>A script reads its binding from a global: a module path is no global.</summary>
  [Test]
  public void AScript_NeedsAGlobal() {
    var (exitCode, _, error) = this.Translate("--to", "javascript", "--profile", "es5", "--module", "iife", "--global", "M", "--bind", "libc.so.6=./b.mjs");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError), error);
    Assert.That(error, Does.Contain("'./b.mjs' is no global object the module format 'iife' can read the binding of 'libc.so.6' from"));
  }

}
