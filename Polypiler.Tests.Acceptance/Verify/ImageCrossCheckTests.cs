using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Verify;

/// <summary>
/// The conformance file of the value images against an independent encoder: Python's
/// <c>struct.pack</c> and <c>int.to_bytes</c> in <c>tests/fixtures/verify/images/crosscheck/encode.py</c>
/// must produce every row's hex, with no difference.
/// </summary>
[TestFixture]
public sealed class ImageCrossCheckTests {

  private static string Images => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "verify", "images");

  [Test, Category("ExternalInterop"), RequiresToolchain("python")]
  public void PythonEncodesEveryConformanceRowAlike() {
    var result = new ProcessRunner().Run(["python3", "-I", Path.Combine(Images, "crosscheck", "encode.py"), Path.Combine(Images, "conformance.json")], TimeSpan.FromMinutes(1));
    var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.That((result.ExitCode, result.StandardError), Is.EqualTo((0, "")), result.StandardOutput);
    Assert.That(lines[^1], Does.Match(@"^\d+ rows, 0 difference\(s\)$"));
    Assert.That(lines[..^1], Has.All.EndsWith(" ok"));
  }

}
