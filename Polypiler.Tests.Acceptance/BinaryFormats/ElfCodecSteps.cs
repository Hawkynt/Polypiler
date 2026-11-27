using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.BinaryFormats.Elf;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.BinaryFormats;

/// <summary>Binds the repository's ELF reader acceptance scenarios.</summary>
[Binding, Scope(Feature = "ELF reader")]
public sealed class ElfCodecSteps {

  private readonly ElfCodecAcceptanceTests.ElfFixture _fixture = new();
  private string _file = "";
  private BinaryResult<BinaryImage> _result = null!;

  [Given("^the ELF calls fixture built for i386$")]
  public void BuildI386() {
    RequireLlvm();
    this._file = this._fixture.Build("calls.s", "i386-unknown-linux-gnu");
  }

  [Given("^the corrupt ELF fixture \"([^\"]+)\"$")]
  public void SelectCorrupt(string fixture)
    => this._file = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "mach-bin-elf", "corrupt", fixture);

  [When("^the ELF reader opens the fixture$")]
  public void Read() => this._result = new ElfCodec().Read(File.ReadAllBytes(this._file), null, this._file);

  [Then("^the i386 ELF code and tables agree with llvm-readelf$")]
  public void Compare() {
    Assert.That(this._result.Problems, Is.Empty);
    Assert.That(this._result.Succeeded, Is.True);
    using var oracle = this._fixture.Oracle(this._file);
    ElfCodecAcceptanceTests.ElfFixture.Compare(this._result.Value!, oracle.RootElement[0], "x86", "protected32", 3, false, false);
    this._fixture.CompareTextSection(this._file, this._result.Value!);
  }

  [Then("^ELF reading fails with (PLY[0-9]+) mentioning \"([^\"]+)\"$")]
  public void Refusal(string code, string detail) {
    Assert.That(this._result.Succeeded, Is.False);
    var issue = this._result.Problems.Single();
    Assert.That(issue.Code, Is.EqualTo(code));
    Assert.That(issue.Message, Does.Contain(detail));
  }

  [AfterScenario]
  public void Cleanup() => this._fixture.Dispose();

  private static void RequireLlvm() {
    var require = Environment.GetEnvironmentVariable("POLYPILER_REQUIRE_TOOLCHAINS");
    foreach (var tool in new[] { "clang", "lld", "llvm" })
      new RequiresToolchainAttribute(tool).Check(require);
  }

}
