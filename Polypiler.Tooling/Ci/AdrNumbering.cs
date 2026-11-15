using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core;

namespace Polypiler.Tooling.Ci;

/// <summary>
/// The numbering rule of the decision records (<c>docs/adr/README.md</c>): every
/// <c>docs/adr/&lt;nnnn&gt;-&lt;slug&gt;.md</c> has a four-digit number no other record uses, lies in
/// the block of the area its line <c>Area: &lt;area&gt;</c> names, and is listed in the index
/// <c>docs/adr/README.md</c>, which lists no record that does not exist.
/// </summary>
public static partial class AdrNumbering {

  /// <summary>The folder of the records, relative to the repository root.</summary>
  public const string Folder = "docs/adr";

  /// <summary>The index of the records, relative to the repository root.</summary>
  public const string Index = "docs/adr/README.md";

  /// <summary>The blocks of numbers per area, and the names an <c>Area:</c> line may give each.</summary>
  public static IReadOnlyList<(int First, int Last, string[] Areas)> Blocks { get; } = [
    (1, 29, ["foundation", "infrastructure", "cli", "verification", "specs"]),
    (30, 59, ["catalogue", "catalog", "packaging", "adapters", "upstream"]),
    (60, 99, ["retro"]),
    (100, 199, ["ir", "types", "base"]),
    (200, 299, ["intrinsics", "analysis"]),
    (300, 399, ["optimization", "legalization", "de-dynamicization"]),
    (400, 499, ["recognition", "recovery"]),
    (500, 599, ["native", "assembler"]),
    (600, 699, ["interop"]),
    (700, 799, ["objects", "object model", "language profiles"]),
    (800, 899, ["permutation"]),
    (900, 999, ["bytecode", "portable formats"]),
    (1000, 2999, ["languages"])
  ];

  /// <summary>Checks the records of a repository.</summary>
  /// <param name="root">The repository root.</param>
  /// <returns>A <c>PLY0075</c> for every breach of the rule; none for a repository without <c>docs/adr</c>.</returns>
  public static IReadOnlyList<Diagnostic> Check(string root) {
    ArgumentNullException.ThrowIfNull(root);
    var folder = Path.Combine(root, Folder);
    if (!Directory.Exists(folder))
      return [];

    var problems = new List<Diagnostic>();
    var records = new List<(int Number, string File)>();
    foreach (var path in Directory.GetFiles(folder, "*.md").Order(StringComparer.Ordinal)) {
      var file = Path.GetFileName(path);
      if (file == "README.md")
        continue;

      var match = RecordName().Match(file);
      if (!match.Success) {
        problems.Add(CiDiagnostics.InconsistentWith($"ADR file {file} does not follow <nnnn>-<slug>.md"));
        continue;
      }

      var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
      records.Add((number, file));
      var area = AreaLine().Match(File.ReadAllText(path));
      if (!area.Success) {
        problems.Add(CiDiagnostics.InconsistentWith($"ADR file {file} has no line 'Area: <area>' after its title"));
        continue;
      }

      var name = area.Groups["area"].Value.Trim();
      var block = Blocks.FirstOrDefault(candidate => candidate.Areas.Contains(name, StringComparer.OrdinalIgnoreCase));
      if (block.Areas is null)
        problems.Add(CiDiagnostics.InconsistentWith($"ADR file {file} names the unknown area '{name}'"));
      else if (number < block.First || number > block.Last)
        problems.Add(CiDiagnostics.InconsistentWith($"ADR file {file} lies outside the block {block.First:D4}–{block.Last:D4} of its area '{name}'"));
    }

    foreach (var group in records.GroupBy(record => record.Number).Where(group => group.Count() > 1))
      problems.Add(CiDiagnostics.InconsistentWith($"ADR number {group.Key:D4} is used by {string.Join(" and ", group.Select(record => record.File))}"));

    var index = Path.Combine(root, Index);
    var listed = File.Exists(index) ? IndexLink().Matches(File.ReadAllText(index)).Select(link => link.Groups["file"].Value).ToHashSet(StringComparer.Ordinal) : [];
    foreach (var record in records.Where(record => !listed.Contains(record.File)))
      problems.Add(CiDiagnostics.InconsistentWith($"ADR file {record.File} is not listed in {Index}"));
    foreach (var file in listed.Where(file => records.All(record => record.File != file)).Order(StringComparer.Ordinal))
      problems.Add(CiDiagnostics.InconsistentWith($"{Index} lists {file}, which does not exist"));

    return problems;
  }

  [GeneratedRegex(@"^(?<number>\d{4})-[a-z0-9]+(-[a-z0-9]+)*\.md$")]
  private static partial Regex RecordName();

  [GeneratedRegex(@"\A[^\n]*\n(?:[ \t]*\r?\n)*Area:(?<area>[^\r\n]*)")]
  private static partial Regex AreaLine();

  [GeneratedRegex(@"\]\((?<file>\d{4}-[a-z0-9-]+\.md)\)")]
  private static partial Regex IndexLink();

}
