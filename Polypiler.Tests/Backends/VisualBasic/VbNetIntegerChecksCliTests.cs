using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary><c>--vb-integer-checks</c> of <c>polypiler translate --to visualbasic</c>: a Visual Basic .NET project with integer checks on; with a classic profile a usage error.</summary>
[TestFixture]
public sealed class VbNetIntegerChecksCliTests {

  private static (int Status, string Errors, string? Project, string? Source) Translate(params string[] options) {
    var folder = Directory.CreateTempSubdirectory();
    try {
      var input = Path.Combine(folder.FullName, "Hash.cs");
      File.WriteAllText(input, "public static class Hash { public static int Mix(int a, int b) { return a * 31 + b; } }\n");
      var output = Path.Combine(folder.FullName, "out");
      using var errors = new StringWriter();
      var status = Program.Run(["translate", input, "--from", "csharp", "--to", "visualbasic", .. options, "--output", output], TextWriter.Null, errors);
      string? Read(string pattern) => Directory.Exists(output) && Directory.EnumerateFiles(output, pattern).SingleOrDefault() is { } file ? File.ReadAllText(file) : null;
      return (status, errors.ToString(), Read("*.vbproj"), Read("*.vb"));
    } finally {
      folder.Delete(recursive: true);
    }
  }

  [Test]
  public void IntegerChecks_WriteTheWrappingArithmeticOut() {
    var checks = Translate("--profile", "vbnet/vb16.9", "--vb-integer-checks");
    Assert.That(checks.Status, Is.Zero, checks.Errors);
    Assert.That(checks.Project, Does.Contain("<RemoveIntegerChecks>false</RemoveIntegerChecks>"));
    Assert.That(checks.Source, Does.Contain("PlyMulWrap(").And.Contain("integer checks on"));
    var removed = Translate("--profile", "vbnet/vb16.9");
    Assert.That(removed.Status, Is.Zero, removed.Errors);
    Assert.That(removed.Project, Does.Contain("<RemoveIntegerChecks>true</RemoveIntegerChecks>"));
    Assert.That(removed.Source, Does.Not.Contain("PlyMulWrap"));
  }

  [Test]
  public void IntegerChecks_OfAClassicProfile_IsAUsageError() {
    var refused = Translate("--profile", "visualbasic/vba7", "--vb-integer-checks");
    Assert.That(refused.Status, Is.EqualTo(2));
    Assert.That(refused.Errors, Does.Contain("--vb-integer-checks needs a Visual Basic .NET profile"));
  }

}
