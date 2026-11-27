using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Binds the end-to-end Batch emission scenarios.</summary>
[Binding, Scope(Feature = "Emit Windows Batch integer kernels")]
public sealed class BatchBackendSteps {

  private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
  private string _source = "";
  private string _errors = "";
  private int _status;

  [Given("^the pinned C# countdown fixture$")]
  public void Countdown() => this._source = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.cs"));

  [Given("^a C# kernel returning an Int64 value$")]
  public void WideInteger() => this._source = "public static class Kernel { public static long Run() => 1L; }";

  [Given("^the pinned C# map alias fixture$")]
  public void MapAlias() => this._source = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "map-alias.cs"));

  [Given("^a C# dictionary kernel reading a missing key$")]
  public void MissingMapKey() => this._source = """
    using System.Collections.Generic;
    public static class Kernel {
      public static int Run() {
        var map = new Dictionary<string, int>();
        return map["missing"];
      }
    }
    """;

  [Given("^a C# kernel allocating an Int64 dictionary$")]
  public void WideMap() => this._source = """
    using System.Collections.Generic;
    public static class Kernel {
      public static int Run() {
        var map = new Dictionary<string, long>();
        return map.Count;
      }
    }
    """;

  [When("^it is translated under the cmd profile$")]
  public void Translate() {
    var source = Path.Combine(this._directory.FullName, "Kernel.cs");
    File.WriteAllText(source, this._source);
    using var errors = new StringWriter();
    this._status = Program.Run(["translate", source, "--from", "csharp", "--to", "batch", "--profile", "cmd", "--opt", "none",
      "--output", Path.Combine(this._directory.FullName, "out")], TextWriter.Null, errors);
    this._errors = errors.ToString();
  }

  [Then("^the native and translated results are both ([0-9]+)$")]
  public void SameStatus(int expected) {
    Assert.That(this._status, Is.Zero, this._errors);
    var csharp = GeneratedCSharp.CompileSource(this._source);
    Assert.That(csharp.Method("Run").Invoke(null, null), Is.EqualTo(expected));
    Assert.That(this.RunNative().ExitCode, Is.EqualTo(expected));
  }

  [Then("^the native and translated dictionaries both return ([0-9]+) without output$")]
  public void MapResult(int expected) {
    Assert.That(this._status, Is.Zero, this._errors);
    var original = GeneratedCSharp.CompileSource(this._source);
    Assert.That(original.Method("Run").Invoke(null, null), Is.EqualTo(expected));
    var observed = this.RunNative();
    Assert.That(observed.ExitCode, Is.EqualTo(expected));
    Assert.That(observed.StandardOutput, Is.Empty);
    Assert.That(observed.StandardError, Is.Empty);
  }

  [Then("^the native Batch script reports (KeyNotFound|DuplicateKey)$")]
  public void BatchTrap(string trap) {
    Assert.That(this._status, Is.Zero, this._errors);
    var observed = this.RunNative();
    Assert.That(observed.ExitCode, Is.EqualTo(97));
    Assert.That(observed.StandardOutput, Is.Empty);
    Assert.That(observed.StandardError, Does.Contain($"PLY_BATCH_TRAP={trap}"));
  }

  private (int ExitCode, byte[] StandardOutput, string StandardError) RunNative() {
    new RequiresToolchainAttribute("cmd").Check(Environment.GetEnvironmentVariable("POLYPILER_REQUIRE_TOOLCHAINS"));
    var directory = Path.Combine(this._directory.FullName, "out");
    var script = Directory.EnumerateFiles(directory, "*.bat").Single();
    return BatchBackendTests.NativeRun(directory, Path.GetFileName(script));
  }

  [Then("^it reports (PLY[0-9]+) and writes no output file$")]
  public void Refusal(string code) {
    Assert.That(this._status, Is.EqualTo(TranslateCommand.UnsupportedConstruct));
    Assert.That(this._errors, Does.Contain(code));
    Assert.That(Directory.Exists(Path.Combine(this._directory.FullName, "out")), Is.False);
  }

  [AfterScenario]
  public void CleanUp() => this._directory.Delete(recursive: true);

}
