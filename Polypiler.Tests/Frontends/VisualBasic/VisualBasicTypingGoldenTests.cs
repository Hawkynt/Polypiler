using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The translations of every classic Visual Basic fixture, as the frontend's own inference typed them
/// before the shared framework took over (#1591): to C#, JavaScript, Python and Visual Basic (vb6 and
/// vba7-64), each byte for byte, a refusal by its exit code and its diagnostics. One golden file per
/// fixture holds every target: a line <c>#### &lt;target&gt; &lt;file&gt; &lt;length&gt;</c> followed
/// by exactly that many characters of the generated file, or <c>#### &lt;target&gt; exit &lt;code&gt;
/// &lt;length&gt;</c> followed by the error output of a refused translation. The manifest is left out.
/// </summary>
[TestFixture]
public sealed class VisualBasicTypingGoldenTests {

  private const string Marker = "#### ";

  /// <summary>Where the golden files are.</summary>
  private static string Root { get; } = Path.Combine(ClassicCases.Folder, "golden");

  /// <summary>The targets, each with the options after <c>--to</c>.</summary>
  private static readonly (string Name, string[] Options)[] _targets = [
    ("csharp", ["csharp"]),
    ("javascript", ["javascript"]),
    ("python", ["python"]),
    ("visualbasic", ["visualbasic"]),
    ("vba7-64", ["visualbasic", "--profile", "visualbasic/vba7-64"])
  ];

  /// <summary>Every fixture of the folder.</summary>
  public static IEnumerable<string> Fixtures => Directory.EnumerateFiles(ClassicCases.Folder, "*.bas").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal);

  private string _workspace = "";

  [SetUp]
  public void SetUp() {
    this._workspace = Path.Combine(Path.GetTempPath(), "polypiler-vbgolden-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._workspace);
  }

  [TearDown]
  public void TearDown() => Directory.Delete(this._workspace, true);

  /// <summary>Every translation of a fixture, as one golden file holds them.</summary>
  private string Translations(string fixture) {
    var source = Path.Combine(ClassicCases.Folder, fixture);
    var text = new StringBuilder();
    foreach (var (name, options) in _targets) {
      var output = Path.Combine(this._workspace, name + "-" + Guid.NewGuid().ToString("N"));
      using var printed = new StringWriter();
      using var error = new StringWriter();
      var exitCode = TranslateCommand.Run([source, "--from", "visualbasic", "--to", .. options, "--output", output], printed, error);
      if (exitCode != 0) {
        var errors = error.ToString().Replace(source, fixture, StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal);
        text.Append(Marker).Append(name).Append(" exit ").Append(exitCode.ToString(CultureInfo.InvariantCulture)).Append(' ')
          .Append(errors.Length.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(errors);
        continue;
      }

      foreach (var file in Directory.GetFiles(output).Where(file => Path.GetFileName(file) != Manifest.FileName).Order(StringComparer.Ordinal)) {
        var content = File.ReadAllText(file);
        text.Append(Marker).Append(name).Append(' ').Append(Path.GetFileName(file)).Append(' ')
          .Append(content.Length.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(content);
      }
    }

    return text.ToString();
  }

  private static string PathOf(string fixture) => Path.Combine(Root, Path.GetFileNameWithoutExtension(fixture) + ".golden");

  [TestCaseSource(nameof(Fixtures))]
  public void Translations_AreByteIdenticalToTheRecording(string fixture)
    => Assert.That(this.Translations(fixture), Is.EqualTo(File.ReadAllText(PathOf(fixture))), fixture);

  [Test]
  public void EveryFixture_HasItsGoldenFile()
    => Assert.That(Directory.EnumerateFiles(Root, "*.golden").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal),
      Is.EqualTo(Fixtures.Select(fixture => Path.GetFileName(PathOf(fixture)))));

  /// <summary>Writes the golden files from the translations of now; run by hand, never by the suite.</summary>
  [Test, Explicit("records the golden files")]
  public void Record() {
    Directory.CreateDirectory(Root);
    foreach (var fixture in Fixtures)
      File.WriteAllText(PathOf(fixture), this.Translations(fixture));
  }

}
