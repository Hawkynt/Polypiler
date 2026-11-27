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

  [Given("^a C# kernel calling a native DLL function$")]
  public void NativeInterop() => this._source = "using System.Runtime.InteropServices; public static class Kernel { [DllImport(\"geom\")] private static extern int geom_norm(int x); public static int Run() => geom_norm(1); }";

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
    new RequiresToolchainAttribute("cmd").Check(Environment.GetEnvironmentVariable("POLYPILER_REQUIRE_TOOLCHAINS"));
    var directory = Path.Combine(this._directory.FullName, "out");
    var script = Directory.EnumerateFiles(directory, "*.bat").Single();
    Assert.That(BatchBackendTests.NativeStatus(directory, Path.GetFileName(script)), Is.EqualTo(expected));
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
