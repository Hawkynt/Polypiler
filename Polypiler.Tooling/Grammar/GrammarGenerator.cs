using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Syntax;

namespace Polypiler.Tooling.Grammar;

/// <summary>One file a grammar generates: where it goes and its text.</summary>
/// <param name="Path">The output file.</param>
/// <param name="Text">The generated C#, with LF line endings.</param>
/// <param name="Rules">The number of rules of the grammar.</param>
public sealed record GeneratedParser(string Path, string Text, int Rules);

/// <summary>
/// Loads, checks and generates a grammar: one parser, or, for a grammar with <c>@guards generate</c>,
/// one per <c>@profile_output "Class axis=value…"</c>. The output goes beside the grammar as
/// <c>&lt;class&gt;.g.cs</c> unless <c>@output</c> or the caller names another file.
/// </summary>
public static class GrammarGenerator {

  private static readonly Regex _generatedMeta = new(@"^@class\s", RegexOptions.Multiline | RegexOptions.CultureInvariant);

  /// <summary>Whether a grammar's text declares the class it generates (<c>@class</c>): only such grammars have generated files.</summary>
  /// <param name="text">The grammar's text.</param>
  public static bool IsGenerated(string text) => _generatedMeta.IsMatch(text ?? throw new ArgumentNullException(nameof(text)));

  /// <summary>Reads and checks a grammar with its bases; the merged grammar and its texts.</summary>
  /// <param name="path">The grammar file.</param>
  /// <param name="tokens">The token names allowed where the grammar declares none.</param>
  /// <param name="read">Reads a file.</param>
  /// <exception cref="GrammarException">The grammar cannot be read or is invalid; the message lists every problem.</exception>
  public static GrammarLoader.Loaded Load(string path, IReadOnlySet<string>? tokens = null, Func<string, string>? read = null) {
    var loaded = GrammarLoader.Load(path, read);
    var problems = GrammarChecker.Check(loaded.Grammar, tokens);
    if (problems.Count > 0)
      throw new GrammarException(string.Join("\n", problems.Select(problem => $"{Path.GetFileName(path)}: {problem}")));

    return loaded;
  }

  /// <summary>Generates the parsers of a grammar.</summary>
  /// <param name="path">The grammar file.</param>
  /// <param name="output">The output file, where the grammar's own is not wanted; for one parser only.</param>
  /// <param name="ns">The namespace, where not the grammar's.</param>
  /// <param name="className">The class, where not the grammar's; for one parser only.</param>
  /// <param name="profile">The profile of a grammar with <c>@guards generate</c>, where not its <c>@profile_output</c> lines.</param>
  /// <param name="read">Reads a file.</param>
  /// <exception cref="GrammarException">The grammar cannot be read, is invalid, or names no output.</exception>
  public static IReadOnlyList<GeneratedParser> Generate(string path, string? output = null, string? ns = null, string? className = null,
    GrammarProfile? profile = null, Func<string, string>? read = null) {
    ArgumentNullException.ThrowIfNull(path);
    var loaded = Load(path, null, read);
    var grammar = loaded.Grammar;
    var name = Path.GetFileName(path);
    var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    var targets = new List<(string Class, GrammarProfile? Profile)>();
    var profiles = grammar.Metas.Where(meta => meta.Key == "profile_output").Select(meta => meta.Value ?? "").ToList();
    if (profile is null && profiles.Count > 0 && output is null && className is null) {
      foreach (var line in profiles) {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 1)
          throw new GrammarException($"{name}: @profile_output is empty");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in words.Skip(1)) {
          var at = word.IndexOf('=', StringComparison.Ordinal);
          if (at <= 0)
            throw new GrammarException($"{name}: @profile_output '{line}': '{word}' is not axis=value");

          values[word[..at]] = word[(at + 1)..];
        }

        targets.Add((words[0], new GrammarProfile(values)));
      }
    } else {
      targets.Add((className ?? grammar.Meta("class") ?? throw new GrammarException($"{name}: the grammar names no class: @class or --class"), profile));
    }

    var results = new List<GeneratedParser>();
    foreach (var (target, targetProfile) in targets) {
      var file = output ?? Path.Combine(directory, targets.Count == 1 && grammar.Meta("output") is { } own ? own : target + ".g.cs");
      var options = new GeneratorOptions(name, loaded.Sha256, target) { Namespace = ns, Profile = targetProfile };
      results.Add(new(file, ParserEmitter.Emit(grammar, options), grammar.Rules.Count));
    }

    return results;
  }

}
