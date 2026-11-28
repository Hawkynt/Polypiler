using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python.Support;

/// <summary>
/// One case of <c>tests/fixtures/python/syntax/*.jsonl</c>: a source, the minor version of Python 3 it
/// is parsed for, and what CPython decides of it - <c>valid</c> where <c>ast.parse</c> and
/// <c>compile</c> accept it, otherwise the class and message of the error, such as
/// <c>SyntaxError: invalid syntax</c>.
/// </summary>
/// <param name="File">The case file, without its extension.</param>
/// <param name="Name">The name of the case in its file.</param>
/// <param name="Version">The minor version.</param>
/// <param name="Source">The source, a str as <c>ast.parse</c> is given it.</param>
/// <param name="Want">What CPython decides.</param>
public sealed record PythonSyntaxCase(string File, string Name, int Version, string Source, string Want) {

  /// <inheritdoc/>
  public override string ToString() => $"{this.File}/{this.Name}";

}

/// <summary>A source and the tree CPython's <c>ast</c> gives it, as the oracle and <c>PyDump</c> write it.</summary>
/// <param name="Source">The source.</param>
/// <param name="Tree">The tree as one line of text.</param>
public sealed record PythonTreeCase(string Source, string Tree) {

  /// <inheritdoc/>
  public override string ToString() => this.Source.Split('\n')[0];

}

/// <summary>Reads the syntax cases of the Python frontend, which the unit tests run and the Python layer compares with CPython.</summary>
internal static class PythonSyntaxCases {

  /// <summary>The folder of the case files.</summary>
  public static string Folder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "python", "syntax");

  /// <summary>Every case of the case files but <c>trees.jsonl</c>, in the order of the files and lines.</summary>
  public static IReadOnlyList<PythonSyntaxCase> All { get; } = [.. Directory.EnumerateFiles(Folder, "*.jsonl").Where(file => Path.GetFileName(file) != "trees.jsonl")
    .Order(System.StringComparer.Ordinal).SelectMany(file => File.ReadLines(file).Select(line => {
      using var document = JsonDocument.Parse(line);
      var root = document.RootElement;
      return new PythonSyntaxCase(Path.GetFileNameWithoutExtension(file), root.GetProperty("name").GetString()!, root.GetProperty("version").GetInt32(),
        root.GetProperty("source").GetString()!, root.GetProperty("want").GetString()!);
    }))];

  /// <summary>The trees of <c>trees.jsonl</c>.</summary>
  public static IReadOnlyList<PythonTreeCase> Trees { get; } = [.. File.ReadLines(Path.Combine(Folder, "trees.jsonl")).Select(line => {
    using var document = JsonDocument.Parse(line);
    return new PythonTreeCase(document.RootElement.GetProperty("source").GetString()!, document.RootElement.GetProperty("tree").GetString()!);
  })];

}
