using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec;

/// <summary>
/// Every spec document under a root: the files <c>&lt;root&gt;/&lt;family&gt;/**/*.yaml</c> in ordinal
/// order of their relative paths, and the index of their IDs. Files directly in the root — the
/// diagnostics registry, the schemas — are not spec documents.
/// </summary>
public sealed class SpecTree {

  private SpecTree(string root, string repositoryRoot, IReadOnlyList<SpecDocument> documents, IReadOnlyList<Diagnostic> unreadable) {
    this.Root = root;
    this.RepositoryRoot = repositoryRoot;
    this.Documents = documents;
    this.Unreadable = unreadable;
    var byId = new Dictionary<string, SpecDocument>(StringComparer.Ordinal);
    foreach (var document in documents)
      if (document.Id is { } id)
        byId.TryAdd(id, document);
    this.ById = byId;
  }

  /// <summary>The spec root, as a full path.</summary>
  public string Root { get; }

  /// <summary>The repository root, as a full path: what test paths and family schema paths are relative to.</summary>
  public string RepositoryRoot { get; }

  /// <summary>The documents that are valid YAML, in ordinal order of <see cref="SpecDocument.Path"/>.</summary>
  public IReadOnlyList<SpecDocument> Documents { get; }

  /// <summary>One <c>PLY0047</c> per file that is not valid YAML, in path order; its file is the document's path.</summary>
  public IReadOnlyList<Diagnostic> Unreadable { get; }

  /// <summary>The documents by their <c>id</c>; where two share one, the first in path order.</summary>
  public IReadOnlyDictionary<string, SpecDocument> ById { get; }

  /// <summary>Reads every document under a root, the repository being the nearest directory above it that holds <c>.git</c>, else the one above the root.</summary>
  /// <param name="root">The spec root, <c>spec</c> in a checkout.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
  public static SpecTree Load(string root) {
    var full = Path.GetFullPath(root);
    return Load(full, FindRepositoryRoot(full) ?? Path.GetFullPath(Path.Combine(full, "..")));
  }

  /// <summary>Reads every document under a root.</summary>
  /// <param name="root">The spec root.</param>
  /// <param name="repositoryRoot">The repository root that test paths and family schema paths are relative to.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
  public static SpecTree Load(string root, string repositoryRoot) {
    var full = Path.GetFullPath(root);
    if (!Directory.Exists(full))
      throw new DirectoryNotFoundException($"spec root '{root}' does not exist");

    var documents = new List<SpecDocument>();
    var unreadable = new List<Diagnostic>();
    var paths = Directory.EnumerateDirectories(full)
      .SelectMany(folder => Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories))
      .Select(file => Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/'))
      .Order(StringComparer.Ordinal);
    foreach (var path in paths)
      try {
        documents.Add(SpecDocument.Parse(path, File.ReadAllText(Path.Combine(full, path))));
      } catch (YamlToJsonException exception) {
        unreadable.Add(Diagnostic.Create(SpecDiagnostics.Unreadable, new SourceRange(path, exception.Line, exception.Column, exception.Line, exception.Column), exception.Message));
      }

    return new(full, Path.GetFullPath(repositoryRoot), documents, unreadable);
  }

  /// <summary>The nearest directory, the given one included, that holds a <c>.git</c> file or folder.</summary>
  /// <param name="directory">Where to start.</param>
  /// <returns>Its full path, or <see langword="null"/> when no directory up to the file system's root holds one.</returns>
  public static string? FindRepositoryRoot(string directory) {
    for (var candidate = Path.GetFullPath(directory); candidate is not null; candidate = Path.GetDirectoryName(candidate))
      if (Path.Exists(Path.Combine(candidate, ".git")))
        return candidate;
    return null;
  }

  /// <summary>Resolves a reference from one document to another by ID: the check a family runs on every body value that names a spec document.</summary>
  /// <param name="document">The document that holds the reference.</param>
  /// <param name="pointer">The JSON pointer of the value.</param>
  /// <param name="id">The ID it names.</param>
  /// <returns><see langword="null"/> when a document has the ID, else <c>PLY0043: unknown spec id '&lt;id&gt;'</c> at the value.</returns>
  public Diagnostic? Resolve(SpecDocument document, string pointer, string id)
    => this.ById.ContainsKey(id) ? null : SpecDiagnostics.At(SpecDiagnostics.BrokenReference, document, pointer, $"unknown spec id '{id}'");

}
