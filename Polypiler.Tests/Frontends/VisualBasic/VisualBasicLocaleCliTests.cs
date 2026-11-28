using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary><c>--vb-locale</c> of <c>polypiler translate --from visualbasic</c> (#1565): the locale the module's CStr and CDbl follow, a name outside the four a usage error.</summary>
[TestFixture]
public sealed class VisualBasicLocaleCliTests {

  private static (int Status, string Errors, string? Source) Translate(params string[] locale) {
    var folder = Directory.CreateTempSubdirectory();
    try {
      var input = Path.Combine(folder.FullName, "Text.bas");
      File.WriteAllText(input, "Option Explicit\nPublic Function F() As String\n    F = CStr(1.5)\nEnd Function\n");
      var output = Path.Combine(folder.FullName, "out");
      using var errors = new StringWriter();
      var status = Program.Run(["translate", input, "--from", "visualbasic", .. locale, "--to", "python", "--output", output], TextWriter.Null, errors);
      var file = Directory.Exists(output) ? Directory.EnumerateFiles(output, "*.py").SingleOrDefault() : null;
      return (status, errors.ToString(), file is null ? null : File.ReadAllText(file));
    } finally {
      folder.Delete(recursive: true);
    }
  }

  [Test]
  public void Locale_GivesTheDecimalSeparator() {
    var german = Translate("--vb-locale", "de-DE");
    Assert.That(german.Status, Is.Zero, german.Errors);
    Assert.That(german.Source, Does.Contain("_ply_format_vb(1.5, 15, \",\")"));
    var plain = Translate();
    Assert.That(plain.Status, Is.Zero, plain.Errors);
    Assert.That(plain.Source, Does.Contain("_ply_format_vb(1.5, 15, \".\")"));
  }

  [Test]
  public void UnknownLocale_IsAUsageError() {
    var refused = Translate("--vb-locale", "xx-XX");
    Assert.That(refused.Status, Is.EqualTo(2));
    Assert.That(refused.Errors, Does.Contain("unknown Visual Basic locale 'xx-XX'").And.Contain("en-US, en-GB, de-DE, fr-FR"));
  }

}
