using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Tooling.Ci;

/// <summary>
/// The test case filter of <c>dotnet test --filter</c>, evaluated for a test that is known only by its
/// categories. The grammar is the one of the .NET test platform: conditions
/// <c>&lt;property&gt;&lt;operator&gt;&lt;value&gt;</c> with the operators <c>=</c>, <c>!=</c>, <c>~</c>
/// (contains) and <c>!~</c> (does not contain), a bare value meaning <c>FullyQualifiedName~value</c>,
/// combined with <c>&amp;</c>, which binds tighter, <c>|</c> and parentheses; <c>\</c> escapes the next
/// character. <c>TestCategory</c> holds when any category of the test satisfies <c>=</c> or <c>~</c> and
/// when none satisfies the value of <c>!=</c> or <c>!~</c>, ignoring case. Of any other property nothing
/// is known: <c>=</c> and <c>~</c> on it fail and <c>!=</c> and <c>!~</c> hold, so a filter by name never
/// counts as running a category.
/// </summary>
public sealed class TestFilter {

  private readonly Func<IReadOnlyCollection<string>, bool> _matches;

  private TestFilter(Func<IReadOnlyCollection<string>, bool> matches) => this._matches = matches;

  /// <summary>The filter that selects every test: <c>dotnet test</c> without <c>--filter</c>.</summary>
  public static TestFilter All { get; } = new(_ => true);

  /// <summary>Whether a test with the given categories passes the filter.</summary>
  /// <param name="categories">The categories of the test.</param>
  /// <returns><see langword="true"/> when the test runs.</returns>
  public bool Selects(IReadOnlyCollection<string> categories) => this._matches(categories);

  /// <summary>Parses a filter expression.</summary>
  /// <param name="text">The expression, without the quotes of the shell.</param>
  /// <returns>The filter.</returns>
  /// <exception cref="FormatException">The expression is empty or not well formed.</exception>
  public static TestFilter Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var parser = new Parser(text);
    var filter = parser.Or();
    if (!parser.AtEnd)
      throw new FormatException($"unexpected '{text[parser.Position]}' at {parser.Position + 1} of the filter '{text}'");

    return new(filter);
  }

  /// <summary>
  /// The filters of every <c>dotnet test</c> in a shell text: the value of <c>--filter</c>, written as
  /// the next word, after <c>=</c> or after <c>:</c>, in double or single quotes or none; a
  /// <c>dotnet test</c> without one selects every test.
  /// </summary>
  /// <param name="run">The text of a step's <c>run</c>.</param>
  /// <returns>One filter per <c>dotnet test</c>, in order.</returns>
  /// <exception cref="FormatException">A filter is not well formed.</exception>
  public static IReadOnlyList<TestFilter> InCommands(string run) {
    ArgumentNullException.ThrowIfNull(run);
    var filters = new List<TestFilter>();
    foreach (var command in Commands(run)) {
      if (command is not ["dotnet", "test", ..])
        continue;

      string? expression = null;
      for (var index = 2; index < command.Count; ++index) {
        var word = command[index];
        if (word == "--filter" && index + 1 < command.Count)
          expression = command[++index];
        else if (word.StartsWith("--filter=", StringComparison.Ordinal) || word.StartsWith("--filter:", StringComparison.Ordinal))
          expression = word["--filter=".Length..];
      }

      filters.Add(expression is null ? All : Parse(expression));
    }

    return filters;
  }

  /// <summary>The simple commands of a shell text, as words: split at line breaks not escaped by <c>\</c> and at <c>;</c>, <c>&amp;&amp;</c>, <c>||</c> and <c>|</c> outside quotes.</summary>
  private static List<List<string>> Commands(string run) {
    var commands = new List<List<string>> { new() };
    var word = new StringBuilder();
    var inWord = false;
    char? quote = null;

    void EndWord() {
      if (inWord)
        commands[^1].Add(word.ToString());
      word.Clear();
      inWord = false;
    }

    void EndCommand() {
      EndWord();
      if (commands[^1].Count > 0)
        commands.Add([]);
    }

    for (var index = 0; index < run.Length; ++index) {
      var character = run[index];
      if (quote is { } open) {
        if (character == open)
          quote = null;
        else
          word.Append(character);
        continue;
      }

      switch (character) {
        case '"' or '\'':
          quote = character;
          inWord = true;
          break;
        case '\\' when index + 1 < run.Length && run[index + 1] is '\n' or '\r':
          index += run[index + 1] == '\r' && index + 2 < run.Length && run[index + 2] == '\n' ? 2 : 1;
          EndWord();
          break;
        case '\n' or ';' or '|' or '&':
          EndCommand();
          break;
        case ' ' or '\t' or '\r':
          EndWord();
          break;
        default:
          word.Append(character);
          inWord = true;
          break;
      }
    }

    EndWord();
    return commands;
  }

  private sealed class Parser(string text) {

    public int Position { get; private set; }

    public bool AtEnd => this.Position == text.Length;

    public Func<IReadOnlyCollection<string>, bool> Or() {
      var terms = new List<Func<IReadOnlyCollection<string>, bool>> { this.And() };
      while (this.Accept('|'))
        terms.Add(this.And());
      return terms.Count == 1 ? terms[0] : categories => terms.Any(term => term(categories));
    }

    private Func<IReadOnlyCollection<string>, bool> And() {
      var factors = new List<Func<IReadOnlyCollection<string>, bool>> { this.Factor() };
      while (this.Accept('&'))
        factors.Add(this.Factor());
      return factors.Count == 1 ? factors[0] : categories => factors.All(factor => factor(categories));
    }

    private Func<IReadOnlyCollection<string>, bool> Factor() {
      if (this.Accept('(')) {
        var inner = this.Or();
        if (!this.Accept(')'))
          throw new FormatException($"missing ')' at {this.Position + 1} of the filter '{text}'");
        return inner;
      }

      return this.Condition();
    }

    private Func<IReadOnlyCollection<string>, bool> Condition() {
      var property = this.Word();
      string op;
      if (this.Accept('!'))
        op = this.Accept('=') ? "!=" : this.Accept('~') ? "!~" : throw new FormatException($"expected '=' or '~' after '!' at {this.Position + 1} of the filter '{text}'");
      else if (this.Accept('='))
        op = "=";
      else if (this.Accept('~'))
        op = "~";
      else
        return Compare("FullyQualifiedName", "~", property);

      return Compare(property, op, this.Word());
    }

    private static Func<IReadOnlyCollection<string>, bool> Compare(string property, string op, string value) {
      if (!property.Equals("TestCategory", StringComparison.OrdinalIgnoreCase))
        return _ => op[0] == '!';

      Func<string, bool> test = op[^1] == '=' ? category => category.Equals(value, StringComparison.OrdinalIgnoreCase) : category => category.Contains(value, StringComparison.OrdinalIgnoreCase);
      return op[0] == '!' ? categories => !categories.Any(test) : categories => categories.Any(test);
    }

    private string Word() {
      var word = new StringBuilder();
      while (!this.AtEnd && text[this.Position] is not ('(' or ')' or '&' or '|' or '=' or '!' or '~')) {
        if (text[this.Position] == '\\' && this.Position + 1 < text.Length)
          ++this.Position;
        word.Append(text[this.Position++]);
      }

      var trimmed = word.ToString().Trim();
      return trimmed.Length > 0 ? trimmed : throw new FormatException($"expected a property or value at {this.Position + 1} of the filter '{text}'");
    }

    private bool Accept(char character) {
      if (this.AtEnd || text[this.Position] != character)
        return false;

      ++this.Position;
      return true;
    }

  }

}
