using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Polypiler.Cli;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Backends.Testing;

/// <summary>One way of translating a source: the target language and every option that changes the text.</summary>
/// <param name="Target">The value of <c>--to</c>.</param>
/// <param name="Name">The variant as the golden file names it.</param>
/// <param name="Options">The options of the target, after <c>--to</c>.</param>
internal sealed record GoldenVariant(string Target, string Name, string[] Options) {

  /// <summary>The variant as a test name shows it.</summary>
  public override string ToString() => this.Name;

}

/// <summary>
/// The golden outputs of the backends: every source of <c>tests/fixtures/prototype</c> translated with
/// <c>--opt none</c> for every profile, module format, namespace form and floating-point mode of both
/// backends, as the emitters wrote them before the target syntax trees replaced their string building
/// (#1336). One file per source and target holds every variant: a line
/// <c>#### &lt;variant&gt; &lt;file&gt; &lt;length&gt;</c> followed by exactly that many characters of
/// the generated file, or <c>#### &lt;variant&gt; exit &lt;code&gt;</c> for a variant the translation
/// refuses. The manifest beside the files and the header line naming the licence of the helpers are
/// left out of the comparison: both came later than the recording. <see cref="GoldenOutputRecorder"/> writes them.
/// </summary>
internal static class GoldenOutputs {

  /// <summary>The marker every section of a golden file starts with.</summary>
  private const string Marker = "#### ";

  /// <summary>Where the golden files are.</summary>
  public static string Root { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "backend", "golden");

  /// <summary>Where the sources are.</summary>
  private static string Sources { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");

  private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyDictionary<string, string>>> _recorded = new(StringComparer.Ordinal);

  /// <summary>Every source of the prototype fixtures, as its path below <c>tests/fixtures/prototype</c> with forward slashes, in a fixed order.</summary>
  public static IReadOnlyList<string> All { get; } = [
    .. Directory.EnumerateFiles(Sources, "*", SearchOption.AllDirectories)
      .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal) || path.EndsWith(".mjs", StringComparison.Ordinal))
      .Select(path => Path.GetRelativePath(Sources, path).Replace(Path.DirectorySeparatorChar, '/'))
      .Order(StringComparer.Ordinal)
  ];

  /// <summary>The targets, each with its variants.</summary>
  public static IReadOnlyList<string> Targets { get; } = ["csharp", "javascript"];

  /// <summary>
  /// The variants of a target. C#: every profile, without and with a namespace (which the language
  /// version writes as a block or file-scoped), in both floating-point modes. JavaScript: every level
  /// with every module format it has, ECMAScript 5 with and without typed arrays, in both modes.
  /// </summary>
  public static IReadOnlyList<GoldenVariant> Variants(string target) => target == "csharp" ? _csharp : _javascript;

  private static readonly string[] _modes = ["accurate", "strict"];

  private static readonly GoldenVariant[] _csharp = [
    .. from profile in CSharpProfile.All
       from @namespace in new[] { false, true }
       from mode in _modes
       select new GoldenVariant("csharp", $"{profile.Name}{(@namespace ? "+ns" : "")}+{mode}",
         [.. (string[])["--profile", profile.Name], .. @namespace ? (string[])["--namespace", "Golden.Output"] : [], "--float", mode])
  ];

  private static readonly GoldenVariant[] _javascript = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       from mode in _modes
       select new GoldenVariant("javascript", $"{level}{(typed ? "+typed" : "")}+{module}+{mode}", [
         "--profile", level, "--module", module, .. module == "iife" ? (string[])["--global", "Golden"] : [],
         .. typed ? (string[])["--typed-arrays"] : [], "--float", mode
       ])
  ];

  /// <summary>The source language of a source file, by its extension.</summary>
  public static string LanguageOf(string source) => source.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "javascript";

  /// <summary>The golden file of a source and a target.</summary>
  public static string PathOf(string source, string target) => Path.Combine(Root, source.Replace('/', Path.DirectorySeparatorChar) + "." + target + ".golden");

  /// <summary>
  /// The section of a variant as a translation produces it now: the files written, each under its
  /// name, or the exit code of a refused translation.
  /// </summary>
  /// <param name="source">The source, as <see cref="All"/> has it.</param>
  /// <param name="variant">The variant.</param>
  /// <param name="workspace">A directory the translation may write into.</param>
  public static string Translate(string source, GoldenVariant variant, string workspace) {
    var output = Path.Combine(workspace, "out-" + Guid.NewGuid().ToString("N"));
    using var printed = new StringWriter();
    using var error = new StringWriter();
    var exitCode = TranslateCommand.Run(
      [Path.Combine(Sources, source.Replace('/', Path.DirectorySeparatorChar)), "--from", LanguageOf(source), "--to", variant.Target, "--output", output, "--opt", "none", .. variant.Options],
      printed, error);
    var section = new StringBuilder();
    if (exitCode != 0)
      return section.Append(Marker).Append(variant.Name).Append(" exit ").Append(exitCode.ToString(CultureInfo.InvariantCulture)).Append('\n').ToString();

    foreach (var file in Directory.GetFiles(output).Where(file => Path.GetFileName(file) != Manifest.FileName).Order(StringComparer.Ordinal)) {
      var content = WithoutHelperLine(File.ReadAllText(file));
      section.Append(Marker).Append(variant.Name).Append(' ').Append(Path.GetFileName(file)).Append(' ')
        .Append(content.Length.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(content);
    }

    return section.ToString();
  }

  /// <summary>
  /// A generated file without the header line that names the licence of its helpers, which the
  /// emitters write since #1351, after the golden files were recorded; the line itself is checked
  /// where the helpers are (TranslateLicenceTests).
  /// </summary>
  private static string WithoutHelperLine(string content) => _helperLine.Replace(content, "", 1);

  private static readonly Regex _helperLine = new("^// Helpers added by polypiler: [^\n]*\n", RegexOptions.CultureInvariant | RegexOptions.Multiline);

  /// <summary>The recorded section of every variant of a source and a target, by the name of the variant.</summary>
  public static IReadOnlyDictionary<string, string> Recorded(string source, string target)
    => _recorded.GetOrAdd(PathOf(source, target), path => new(() => Read(File.ReadAllText(path)))).Value;

  /// <summary>The sections of a golden file by the name of their variant; a variant of several files has them in one section.</summary>
  private static Dictionary<string, string> Read(string text) {
    var sections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
    var position = 0;
    while (position < text.Length) {
      var end = text.IndexOf('\n', position);
      var header = text[position..end];
      var parts = header[Marker.Length..].Split(' ');
      var section = sections.TryGetValue(parts[0], out var known) ? known : sections[parts[0]] = new StringBuilder();
      section.Append(header).Append('\n');
      position = end + 1;
      if (parts[1] == "exit")
        continue;

      var length = int.Parse(parts[2], CultureInfo.InvariantCulture);
      section.Append(text, position, length);
      position += length;
    }

    return sections.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
  }

}
