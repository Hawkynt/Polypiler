using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Modules;

/// <summary>
/// Leaves out of a linked script what its entry file never reaches: a declaration of a required file
/// that no reached code names, and a function of an object literal held by a <c>const</c> - a
/// namespace such as <c>OpCodes</c> - that no reached code takes from it. What the entry file
/// declares or does, and what every file does besides declaring, is reached; so is everything a
/// reached declaration names, a class with all its members, and a namespace whole where it is used
/// as a value. Names are matched without regard to scopes, which only ever keeps more.
/// </summary>
internal sealed class Reachability {

  private readonly string _text;
  private readonly Dictionary<string, Statement> _declarations = new(System.StringComparer.Ordinal);
  private readonly Dictionary<string, ObjectExpression> _namespaces = new(System.StringComparer.Ordinal);
  private readonly HashSet<Statement> _statements = [];
  private readonly HashSet<Node> _properties = [];
  private readonly HashSet<string> _whole = new(System.StringComparer.Ordinal);
  private readonly Queue<Node> _pending = new();

  private Reachability(string text) => this._text = text;

  /// <summary>
  /// The script with what the entry file does not reach blanked. Every file of it parsed on its own,
  /// and the link changed nothing but names and blanks, so the script parses too.
  /// </summary>
  public static LinkedSource Shake(LinkedSource linked, LinkedFile entry) {
    var program = new Parser(new ParserOptions { EcmaVersion = EcmaVersion.ES2022 }).ParseScript(linked.Text);

    var shaker = new Reachability(linked.Text);
    foreach (var statement in program.Body) {
      foreach (var declared in Declared(statement))
        shaker._declarations.TryAdd(declared, statement);
      if (statement is VariableDeclaration { Kind: VariableDeclarationKind.Const, Declarations: [{ Id: Identifier { Name: var name }, Init: ObjectExpression literal }] })
        shaker._namespaces.TryAdd(name, literal);
    }

    foreach (var statement in program.Body) {
      // A namespace of the entry file is all of it, as everything else the entry declares.
      if (statement.Location.Start.Line >= entry.FirstLine && statement is VariableDeclaration { Declarations: [{ Id: Identifier { Name: var own } }] } && shaker._namespaces.ContainsKey(own))
        shaker.Whole(own);
      if (statement.Location.Start.Line >= entry.FirstLine || !Declared(statement).Any())
        shaker.Reach(statement);
    }
    shaker.Run();

    var text = linked.Text.ToCharArray();
    foreach (var statement in program.Body) {
      if (!shaker._statements.Contains(statement)) {
        Blank(text, statement.Start, statement.End);
        continue;
      }

      if (statement is VariableDeclaration { Declarations: [{ Id: Identifier { Name: var held }, Init: ObjectExpression literal }] } && shaker._namespaces.ContainsKey(held) && !shaker._whole.Contains(held))
        foreach (var property in literal.Properties.Where(property => !shaker._properties.Contains(property)))
          Blank(text, property.Start, shaker.AfterComma(property.End));
    }

    return linked with { Text = new string(text) };
  }

  /// <summary>The names a statement declares at the top level.</summary>
  private static IEnumerable<string> Declared(Statement statement) => statement switch {
    FunctionDeclaration { Id: { } id } => [id.Name],
    ClassDeclaration { Id: { } id } => [id.Name],
    VariableDeclaration declaration => declaration.Declarations.Select(declarator => declarator.Id).OfType<Identifier>().Select(id => id.Name),
    _ => []
  };

  private static void Blank(char[] text, int start, int end) {
    for (var index = start; index < end; ++index)
      if (text[index] is not ('\n' or '\r'))
        text[index] = ' ';
  }

  /// <summary>
  /// The position after the comma that follows a property, past blanks and comments; the end of the
  /// property when the literal closes after it. A literal always closes, so the text never ends first.
  /// </summary>
  private int AfterComma(int end) {
    var index = end;
    while (true) {
      if (char.IsWhiteSpace(this._text[index]))
        ++index;
      else if (string.CompareOrdinal(this._text, index, "//", 0, 2) == 0)
        index = this._text.IndexOf('\n', index);
      else if (string.CompareOrdinal(this._text, index, "/*", 0, 2) == 0)
        index = this._text.IndexOf("*/", index + 2, System.StringComparison.Ordinal) + 2;
      else
        return this._text[index] == ',' ? index + 1 : end;
    }
  }

  private void Reach(Statement statement) {
    if (!this._statements.Add(statement))
      return;

    // A namespace is reached property by property; any other declaration whole.
    if (statement is VariableDeclaration { Declarations: [{ Id: Identifier { Name: var name } }] } && this._namespaces.ContainsKey(name))
      return;

    this._pending.Enqueue(statement);
  }

  /// <summary>Reaches every function of a namespace: it is used as a value, or it is the entry's own.</summary>
  private void Whole(string name) {
    if (!this._whole.Add(name))
      return;

    foreach (var property in this._namespaces[name].Properties.Where(this._properties.Add))
      this._pending.Enqueue(property);
  }

  private void Run() {
    while (this._pending.TryDequeue(out var node)) {
      this.Scan(node, null);
      this.Stated(node);
    }
  }

  private static readonly System.Text.RegularExpressions.Regex _stated = new(@"@(?:type|param|returns?)\s*\{(?<type>[^{}]*)\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

  /// <summary>
  /// Reaches what the JSDoc types inside reached code name - <c>/** @type {CountryCode} */</c> of a
  /// field only <c>null</c> reaches (#1365): the type states what the field holds, which has to be there.
  /// </summary>
  private void Stated(Node node) {
    foreach (System.Text.RegularExpressions.Match tag in _stated.Matches(this._text[node.Start..node.End]))
      foreach (var name in tag.Groups["type"].Value.Split(['|', '[', ']', ' ', '<', '>', ',', '(', ')', '?', '!', '.'], System.StringSplitOptions.RemoveEmptyEntries))
        if (this._declarations.TryGetValue(name, out var declaration) && !this._namespaces.ContainsKey(name))
          this.Reach(declaration);
  }

  private void Scan(Node node, Node? parent) {
    switch (node) {
      case MemberExpression { Computed: false, Object: Identifier { Name: var name }, Property: Identifier { Name: var member } } when this._namespaces.TryGetValue(name, out var literal):
        this.Reach(this._declarations[name]);
        foreach (var property in literal.Properties.OfType<Property>().Where(property => property.Key is Identifier { Name: var key } && key == member && !property.Computed))
          if (this._properties.Add(property))
            this._pending.Enqueue(property);
        return;
      case Identifier { Name: var name } when !IsName(node, parent):
        if (this._namespaces.ContainsKey(name))
          this.Whole(name);

        if (this._declarations.TryGetValue(name, out var declaration))
          this.Reach(declaration);
        return;
    }

    foreach (var child in node.ChildNodes)
      this.Scan(child, node);
  }

  /// <summary>Whether an identifier is a name that refers to nothing: the property of a member, the key of a property or of a class member.</summary>
  private static bool IsName(Node node, Node? parent) => parent switch {
    MemberExpression { Computed: false } member => member.Property == node,
    Property { Computed: false, Shorthand: false } property => property.Key == node,
    MethodDefinition { Computed: false } method => method.Key == node,
    PropertyDefinition { Computed: false } field => field.Key == node,
    _ => false
  };

}
