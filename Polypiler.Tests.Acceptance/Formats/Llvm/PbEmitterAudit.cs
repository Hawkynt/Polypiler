using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Formats.Llvm;

/// <summary>A data row of one of the numbered tables of the audit.</summary>
/// <param name="Table">The number of the table, 1 to 6.</param>
/// <param name="Line">The 1-based line of the row in the document.</param>
/// <param name="Cells">The cells, trimmed.</param>
public sealed record AuditRow(int Table, int Line, IReadOnlyList<string> Cells) {

  /// <inheritdoc />
  public override string ToString() => $"table {this.Table}, line {this.Line}: {this.Cells[0]}";

}

/// <summary>
/// <c>docs/llvm/pb-emitter-audit.md</c> read as its six numbered tables. A table belongs to the
/// <c>## N. ...</c> heading above it; its first row is the header and its second the separator.
/// </summary>
public sealed class PbEmitterAudit {

  /// <summary>The audit of the repository, relative to its root.</summary>
  public const string RepositoryPath = "docs/llvm/pb-emitter-audit.md";

  /// <summary>A citation of a line or a range of lines of a file: <c>LlvmEmitter.cs:145</c>, <c>Driver.cs:388-392</c>.</summary>
  public static Regex Citation { get; } = new(@"(?<file>[A-Za-z][\w.]*\.(?:cs|BAS|md|yaml|lock)):(?<first>\d+)(?:-(?<last>\d+))?", RegexOptions.CultureInvariant);

  private static readonly Regex _tableHeading = new(@"^## (?<number>[1-6])\. ", RegexOptions.CultureInvariant);

  private PbEmitterAudit(IReadOnlyList<AuditRow> rows) => this.Rows = rows;

  /// <summary>The data rows of the numbered tables, in document order.</summary>
  public IReadOnlyList<AuditRow> Rows { get; }

  /// <summary>The audit of the repository.</summary>
  public static PbEmitterAudit Repository => Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, RepositoryPath)));

  /// <summary>Reads the numbered tables of a Markdown text.</summary>
  /// <param name="markdown">The text.</param>
  public static PbEmitterAudit Parse(string markdown) {
    var rows = new List<AuditRow>();
    var table = 0;
    var rowOfTable = 0;
    var lines = markdown.Replace("\r\n", "\n").Split('\n');
    for (var index = 0; index < lines.Length; ++index) {
      var line = lines[index];
      if (line.StartsWith("## ", StringComparison.Ordinal)) {
        var heading = _tableHeading.Match(line);
        table = heading.Success ? int.Parse(heading.Groups["number"].Value) : 0;
        rowOfTable = 0;
        continue;
      }

      if (!line.StartsWith('|')) {
        rowOfTable = 0;
        continue;
      }

      if (table == 0 || ++rowOfTable <= 2)
        continue;
      rows.Add(new(table, index + 1, Cells(line)));
    }

    return new(rows);
  }

  /// <summary>The cells of a table row; a <c>\|</c> inside a cell does not split it.</summary>
  private static string[] Cells(string line)
    => [.. Regex.Split(line.Trim().Trim('|'), @"(?<!\\)\|").Select(cell => cell.Trim())];

  /// <summary>The rows of a table.</summary>
  /// <param name="table">The number of the table.</param>
  public IEnumerable<AuditRow> Table(int table) => this.Rows.Where(row => row.Table == table);

  /// <summary>The rows that cite no file and line.</summary>
  public IEnumerable<AuditRow> RowsWithoutCitation() => this.Rows.Where(row => !row.Cells.Any(cell => Citation.IsMatch(cell)));

  /// <summary>The citations of a file whose line lies beyond its end or whose range runs backwards.</summary>
  /// <param name="file">The file name as cited, <c>LlvmEmitter.cs</c>.</param>
  /// <param name="lineCount">The number of lines of the file.</param>
  public IEnumerable<string> CitationsOutside(string file, int lineCount)
    => from row in this.Rows
       from cell in row.Cells
       from Match citation in Citation.Matches(cell)
       where citation.Groups["file"].Value == file
       let first = int.Parse(citation.Groups["first"].Value)
       let last = citation.Groups["last"].Success ? int.Parse(citation.Groups["last"].Value) : first
       where first < 1 || last < first || last > lineCount
       select $"{row}: {citation.Value}";

  /// <summary>Whether the first cell of a row of a table names a construct in backticks.</summary>
  /// <param name="table">The number of the table.</param>
  /// <param name="construct">The construct, <c>IrBinary</c> or <c>IrTypeKind.Int</c>.</param>
  public bool Covers(int table, string construct) => this.Table(table).Any(row => row.Cells[0].Contains($"`{construct}`", StringComparison.Ordinal));

}

/// <summary>The cases of the switches of <c>LlvmEmitter.cs</c> at the pin, read from its text.</summary>
public static class PbEmitterSource {

  /// <summary>The repository the emitter lives in.</summary>
  public const string Repository = "Hawkynt/PB-Compiler";

  /// <summary>The emitter's path in that repository.</summary>
  public const string EmitterPath = "PowerBasic.Compiler/Ir/LlvmEmitter.cs";

  private static readonly Regex _caseType = new(@"^\s+(?<type>Ir[A-Z]\w*)\b(?!\.)", RegexOptions.CultureInvariant | RegexOptions.Multiline);
  private static readonly Regex _typeKind = new(@"\bIrTypeKind\.(?<kind>\w+)", RegexOptions.CultureInvariant);

  /// <summary>The IR instruction types the cases of <c>EmitInstruction</c> name.</summary>
  /// <param name="source">The text of the emitter.</param>
  public static IReadOnlyList<string> InstructionArms(string source)
    => CaseTypes(Body(source, "private string EmitInstruction(", "private static string FastMath("), "inst is ");

  /// <summary>The value types the cases of <c>Ref</c> name.</summary>
  /// <param name="source">The text of the emitter.</param>
  public static IReadOnlyList<string> RefCases(string source)
    => CaseTypes(Body(source, "private string Ref(", "private static string Suffix("), null);

  /// <summary>The <c>IrTypeKind</c> members the cases of <c>Ty</c> name, as <c>IrTypeKind.Int</c>.</summary>
  /// <param name="source">The text of the emitter.</param>
  public static IReadOnlyList<string> TypeKinds(string source)
    => [.. _typeKind.Matches(Body(source, "private static string Ty(", "private static string EscapeBytes(")).Select(match => "IrTypeKind." + match.Groups["kind"].Value).Distinct()];

  /// <summary>The text from one marker to the next.</summary>
  /// <exception cref="FormatException">A marker is not in the text: the emitter changed shape.</exception>
  private static string Body(string source, string start, string end) {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    var to = from < 0 ? -1 : source.IndexOf(end, from, StringComparison.Ordinal);
    return to < 0 ? throw new FormatException($"'{start}' followed by '{end}' is not in the emitter") : source[from..to];
  }

  /// <summary>The types that open a line of a switch, and those after a pattern prefix such as <c>inst is </c>.</summary>
  private static string[] CaseTypes(string body, string? prefix) {
    var types = _caseType.Matches(body).Select(match => match.Groups["type"].Value);
    if (prefix != null)
      types = types.Concat(Regex.Matches(body, Regex.Escape(prefix) + @"(?<type>Ir[A-Z]\w*)").Select(match => match.Groups["type"].Value));
    return [.. types.Distinct()];
  }

}
