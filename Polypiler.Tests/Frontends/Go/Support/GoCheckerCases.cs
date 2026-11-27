using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Go.Support;

/// <summary>A constant a case states: its name, type and exact value as go/types writes them.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type.</param>
/// <param name="Value">The exact value.</param>
internal sealed record GoStatedConstant(string Name, string Type, string Value);

/// <summary>
/// One case of <c>tests/fixtures/go/checker/*.txtar</c>: a Go file and what the frontend must decide
/// of it - <c>valid</c>, <c>invalid</c> (a type error), <c>syntax</c> (a syntax error) or
/// <c>unsupported</c> - with a part of the message and the constants it states.
/// </summary>
/// <param name="Name">The file and section, for example <c>statements/missing_return</c>.</param>
/// <param name="Want">What the frontend must decide.</param>
/// <param name="Message">A part of the message, or empty.</param>
/// <param name="Source">The Go source.</param>
/// <param name="Constants">The constants the case states.</param>
internal sealed record GoCheckerCase(string Name, string Want, string Message, string Source, IReadOnlyList<GoStatedConstant> Constants) {

  /// <inheritdoc/>
  public override string ToString() => this.Name;

}

/// <summary>Reads the cases of the Go frontend's checker, which the unit tests run and the Go layer compares with go.</summary>
internal static partial class GoCheckerCases {

  /// <summary>The folder of the case files.</summary>
  public static string Folder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "checker");

  [GeneratedRegex(@"^-- (\S+) --\n", RegexOptions.Multiline)]
  private static partial Regex Header();

  [GeneratedRegex(@"^// want: (\w+)(?: ""(.*)"")?\n")]
  private static partial Regex WantLine();

  [GeneratedRegex(@"^// const (\S+) (.+?) (\S+)$", RegexOptions.Multiline)]
  private static partial Regex ConstLine();

  /// <summary>Every case, in the order of the files and sections.</summary>
  public static IReadOnlyList<GoCheckerCase> All { get; } = Read();

  private static List<GoCheckerCase> Read() {
    var cases = new List<GoCheckerCase>();
    foreach (var file in Directory.EnumerateFiles(Folder, "*.txtar").Order(System.StringComparer.Ordinal)) {
      var parts = Header().Split(File.ReadAllText(file).ReplaceLineEndings("\n"));
      for (var index = 1; index + 1 < parts.Length; index += 2) {
        var body = parts[index + 1];
        var want = WantLine().Match(body);
        var constants = ConstLine().Matches(body).Select(match => new GoStatedConstant(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value)).ToList();
        cases.Add(new($"{Path.GetFileNameWithoutExtension(file)}/{parts[index]}", want.Groups[1].Value, want.Groups[2].Value.Replace("\\\"", "\""), body, constants));
      }
    }

    return cases;
  }

}
