using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// A fixture of <c>tests/fixtures/visualbasic/classic</c> with its cases as functions of the module
/// itself, <c>Probe0</c>, <c>Probe1</c>, …, each of which returns the value of its case through
/// <c>CStr</c>, or <c>!</c> and the error number, as the oracle's driver prints it - so that the
/// interpreter and every backend print what LibreOffice prints, in the locale of the import.
/// </summary>
internal static class ClassicCases {

  /// <summary>The folder of the fixtures.</summary>
  public static string Folder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "classic");

  /// <summary>The lines of a file of the folder that are not empty.</summary>
  public static IReadOnlyList<string> Lines(string file) => [.. File.ReadAllLines(Path.Combine(Folder, file)).Where(line => line.Length > 0)];

  /// <summary>The lines of a file of the oracle's evidence (docs/adr/evidence/2600).</summary>
  public static IReadOnlyList<string> Evidence(string file)
    => [.. File.ReadAllLines(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "adr", "evidence", "2600", file)).Where(line => line.Length > 0)];

  /// <summary>The name of the function of a case.</summary>
  public static string Name(int index) => "Probe" + index.ToString(CultureInfo.InvariantCulture);

  /// <summary>The text of a fixture with a function per case after it.</summary>
  public static string Source(string fixture, IReadOnlyList<string> cases) {
    var text = new StringBuilder(File.ReadAllText(Path.Combine(Folder, fixture)));
    for (var index = 0; index < cases.Count; ++index) {
      var name = Name(index);
      text.Append("\nPublic Function ").Append(name).Append("() As String\n    On Error GoTo Failed\n    ").Append(name).Append(" = CStr(").Append(cases[index])
        .Append(")\n    Exit Function\nFailed:\n    ").Append(name).Append(" = \"!\" & CStr(Err.Number)\nEnd Function\n");
    }

    return text.ToString();
  }

  /// <summary>A fixture and the functions of its cases, imported in a locale.</summary>
  public static IrModule Import(string fixture, string cases, VisualBasicLocale? locale = null) {
    var result = VisualBasicFrontend.Import(fixture, Source(fixture, Lines(cases)), new VisualBasicImportOptions { Locale = locale ?? VisualBasicLocale.Default });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  /// <summary>What the functions of the cases return in the interpreter.</summary>
  public static IReadOnlyList<string> Interpret(IrModule module, int count) => [.. Enumerable.Range(0, count).Select(index => (string)IrInterpreter.Call(module, Name(index), [])!)];

  /// <summary>The indices of the rows on which the oracle printed the documented value.</summary>
  public static IReadOnlyList<int> Agreeing(IReadOnlyList<string> expected, IReadOnlyList<string> evidence)
    => [.. Enumerable.Range(0, expected.Count).Where(index => expected[index] == evidence[index])];

}
