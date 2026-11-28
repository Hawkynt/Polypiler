using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Polypiler.Core.Syntax;
using Polypiler.Tooling.Grammar;

namespace Polypiler.Tests.Tooling.Grammar;

/// <summary>
/// Generates test grammars and compiles each with a hand-written half into one assembly, so that the
/// tests run the generated parsers: a lexer of names, numbers and operators, the token table of the
/// grammars, hooks <c>quiet</c> and a predicate <c>loud</c>, and a rendering of the results as text.
/// </summary>
internal static class GrammarHarness {

  /// <summary>The metas every test grammar starts with.</summary>
  public const string Header = """
    @token_type "Token<Kind>"
    @token_kind Kind
    @using "System.Linq"
    @tokens '''
    NAME Name
    NUMBER Number
    OP Op
    ENDMARKER End
    WORD - Word string
    TOKEN2 Name Word2
    '''

    """;

  private const string Half = """
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Polypiler.Core.Syntax;

    namespace NAMESPACE;

    public enum Kind { Name, Number, Op, End }

    public sealed class Lexer(string text) : ITokenSource<Token<Kind>> {
      private int _at;
      public Token<Kind> Next() {
        while (_at < text.Length && text[_at] == ' ')
          _at++;
        var start = _at;
        if (_at == text.Length)
          return Make(Kind.End, start);
        var c = text[_at];
        if (char.IsDigit(c)) {
          while (_at < text.Length && char.IsDigit(text[_at])) _at++;
          return Make(Kind.Number, start);
        }
        if (char.IsLetter(c)) {
          while (_at < text.Length && char.IsLetterOrDigit(text[_at])) _at++;
          return Make(Kind.Name, start);
        }
        _at += _at + 1 < text.Length && text.Substring(_at, 2) is "**" or "->" or "==" ? 2 : 1;
        return Make(Kind.Op, start);
      }
      private Token<Kind> Make(Kind kind, int start) => new(kind, new(1, start + 1, start), new(1, _at + 1, _at), text[start.._at]);
    }

    public sealed partial class P {
      private int _quiet;
      public P(string text, string? version) : base(new Lexer(text)) {
        if (version is not null)
          this.Profile = new GrammarProfile(new Dictionary<string, string> { ["version"] = version });
      }
      public static string Run(string text, string? version, out int secondPasses) {
        var parser = new P(text, version);
        try {
          return Render(parser.ParseStart());
        } catch (SyntaxErrorException error) {
          return "error: " + error.Error.Message + " @" + error.Error.Position.Offset;
        } finally {
          secondPasses = parser.SecondPasses;
        }
      }
      public static string Render(object? value) => value switch {
        null => "null",
        Token<Kind> token => token.Text,
        string text => text,
        System.Collections.IEnumerable items => "[" + string.Join(",", items.Cast<object?>().Select(Render)) + "]",
        _ => ReferenceEquals(value, Dummy) ? "dummy" : value.ToString()!
      };
      private void EnterQuiet() => _quiet++;
      private void LeaveQuiet() => _quiet--;
      private bool loud() => _quiet == 0;
      private string Quiet => _quiet.ToString();
      private string? Word() => this.Expect(Kind.Name)?.Text;
      private Token<Kind>? Word2() => this.Expect(Kind.Name);
      private string? SoftWord(string text) => this.ExpectSoftKeyword(text)?.Text;
    }
    """;

  /// <summary>Runs a generated parser: the rendered result or the error, and the number of second passes.</summary>
  public delegate string Parse(string text, string? version, out int secondPasses);

  /// <summary>Generates and compiles grammars given by name; a parser for each.</summary>
  /// <param name="grammars">The grammars by name, without <see cref="Header"/>.</param>
  /// <param name="profiles">The profile of a grammar with <c>@guards generate</c>, by name.</param>
  public static IReadOnlyDictionary<string, Parse> Build(IReadOnlyDictionary<string, string> grammars, IReadOnlyDictionary<string, GrammarProfile>? profiles = null) {
    var trees = new List<SyntaxTree>();
    foreach (var (name, text) in grammars) {
      var loaded = GrammarGenerator.Load(name + ".pgram", null, _ => Header + text);
      var options = new GeneratorOptions(name + ".pgram", loaded.Sha256, "P") {
        Namespace = "G." + name, Profile = profiles?.GetValueOrDefault(name)
      };
      trees.Add(CSharpSyntaxTree.ParseText(ParserEmitter.Emit(loaded.Grammar, options), path: name + ".g.cs"));
      trees.Add(CSharpSyntaxTree.ParseText(Half.Replace("NAMESPACE", "G." + name, StringComparison.Ordinal), path: name + ".cs"));
    }

    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Select(path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>().ToList();
    references.Add(MetadataReference.CreateFromFile(typeof(ParserBase<,>).Assembly.Location));
    var compilation = CSharpCompilation.Create("Grammars" + Guid.NewGuid().ToString("N"), trees, references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    using var image = new MemoryStream();
    var result = compilation.Emit(image);
    var complaints = result.Diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning && diagnostic.Id != "CS8981").ToList();
    if (complaints.Count > 0)
      throw new InvalidOperationException("the generated parsers do not compile cleanly:\n" + string.Join("\n", complaints.Take(20)));

    var assembly = Assembly.Load(image.ToArray());
    return grammars.Keys.ToDictionary(name => name, name => assembly.GetType($"G.{name}.P", true)!.GetMethod("Run")!.CreateDelegate<Parse>());
  }

}
