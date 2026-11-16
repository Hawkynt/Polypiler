using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Modules;

/// <summary>A file of a module graph and the lines it takes in the linked source.</summary>
/// <param name="Path">The file, as the graph resolved it.</param>
/// <param name="FirstLine">The 1-based line of the linked source its first line is.</param>
/// <param name="Lines">How many lines it takes.</param>
internal sealed record LinkedFile(string Path, int FirstLine, int Lines);

/// <summary>
/// The files of a module graph linked into the one script the import reads: every module's body at
/// the top level, the modules a file requires before it, and only what the entry file reaches of
/// them. Each file keeps its lines and its columns, so that a position of the script is a position of
/// one file (<see cref="Locate"/>).
/// </summary>
/// <param name="Text">The script.</param>
/// <param name="Files">The files in the order they stand in the script, the entry file last.</param>
internal sealed record LinkedSource(string Text, IReadOnlyList<LinkedFile> Files) {

  /// <summary>The file and the line of that file a line of the script is.</summary>
  public (string File, int Line) Locate(int line) {
    var file = this.Files.Last(file => file.FirstLine <= line);
    return (file.Path, line - file.FirstLine + 1);
  }

  /// <summary>A diagnostic of the script, which the import places at the start of a node, as one of the file it points into.</summary>
  public Diagnostic Locate(Diagnostic diagnostic) {
    var (file, line) = this.Locate(diagnostic.Line);
    return diagnostic with { File = file, Line = line };
  }

}

/// <summary>
/// Links a JavaScript file and the files it requires into one script (#1365). A file is a module of
/// one of three shapes:
/// <list type="bullet">
/// <item>a factory: the UMD wrapper <c>(function (root, factory) { … factory(require('a'), require('b')) … }(global, function (A, B) { … }))</c>,
/// whose factory body is the module, whose parameters are the modules the wrapper's CommonJS branch
/// requires, in order, and whose <c>return</c> is what it exports;</item>
/// <item>a wrapper: an immediately invoked function <c>(function (global) { … })(this)</c> whose body is
/// the module and assigns what it exports to <c>module.exports</c>;</item>
/// <item>CommonJS: a file whose top level assigns <c>module.exports</c> or requires another file.</item>
/// </list>
/// What only finds the environment - a test of <c>typeof module</c>, <c>define.amd</c> or <c>window</c>,
/// an assignment to <c>module.exports</c> or to the global object, a check that a required module is
/// there - is folded away, as a library has no environment to probe. A module's exports are names of its
/// top level: <c>B.x</c> and a destructured <c>const { x } = B</c> are the declaration of <c>x</c> in
/// the module <c>B</c> is, and a module whose export is one object, as <c>module.exports = OpCodes</c>,
/// is reached through that object's name. Of the files the entry requires, a declaration nothing of the
/// entry reaches is left out, and so is a function of an object literal nothing calls.
/// </summary>
internal sealed class ModuleGraph {

  /// <summary>The names whose <c>typeof</c> only finds out where the code runs.</summary>
  private static readonly HashSet<string> _environment = ["module", "exports", "define", "require", "window", "self", "global", "globalThis"];

  private readonly Func<string, string?> _read;
  private readonly Dictionary<string, Module> _modules = new(StringComparer.Ordinal);
  private readonly List<Module> _order = [];
  private readonly List<Diagnostic> _diagnostics = [];

  /// <summary>The names the link has renamed declarations to, which no later renaming may take again.</summary>
  private readonly HashSet<string> _renamed = new(StringComparer.Ordinal);

  private ModuleGraph(Func<string, string?> read) => this._read = read;

  /// <summary>
  /// Links a file with the files it requires; <see langword="null"/> when the file is no module of the
  /// three shapes, which is then imported as it is.
  /// </summary>
  /// <param name="path">The file.</param>
  /// <param name="source">Its text.</param>
  /// <param name="read">Reads a file the graph requires: its text, or <see langword="null"/> when there is none.</param>
  /// <param name="diagnostics">Why the files cannot be linked; empty when they can.</param>
  public static LinkedSource? Link(string path, string source, Func<string, string?> read, out IReadOnlyList<Diagnostic> diagnostics) {
    var graph = new ModuleGraph(read);
    diagnostics = graph._diagnostics;
    // A file that is no script - an ES module - or no module of the three shapes is imported as it is.
    if (Parse(path, source, graph._diagnostics) is not { } program || Module.Of(path, source, program) is not { } entry) {
      graph._diagnostics.Clear();
      return null;
    }

    graph._modules[Key(path)] = entry;
    if (entry.Problem is { } problem)
      graph._diagnostics.Add(problem);
    graph.Load(entry, [entry]);
    if (graph._diagnostics.Count > 0)
      return null;

    foreach (var module in graph._order)
      graph.Rewrite(module);
    graph.Collisions();
    if (graph._diagnostics.Count > 0)
      return null;

    return graph.Compose(entry);
  }

  private static string Key(string path) => Path.GetFullPath(path);

  private static Program? Parse(string path, string source, List<Diagnostic> diagnostics) {
    try {
      return new Parser(new ParserOptions { EcmaVersion = EcmaVersion.ES2022 }).ParseScript(source);
    } catch (ParseErrorException failure) {
      diagnostics.Add(JavaScriptFrontend.SyntaxErrorDescriptor.At(failure.Error!.Description, path, failure.Error.LineNumber, failure.Error.Column + 1));
      return null;
    }
  }

  /// <summary>Loads the modules a module requires, before it, each once; a cycle and a file that is no module are refused.</summary>
  private void Load(Module module, List<Module> path) {
    foreach (var (binding, request) in module.Requests) {
      var file = Resolve(module.Path, request.Specifier);
      var text = file is null ? null : this._read(file);
      if (file is null || text is null) {
        this._diagnostics.Add(module.Refuse(request.Node, $"the required file '{request.Specifier}', which cannot be read"));
        continue;
      }

      if (this._modules.TryGetValue(Key(file), out var known)) {
        if (path.Contains(known))
          this._diagnostics.Add(module.Refuse(request.Node, $"a cycle of required files through '{request.Specifier}'"));
        module.Imports[binding] = known;
        continue;
      }

      if (Parse(file, text, this._diagnostics) is not { } program)
        continue;

      if (Module.Of(file, text, program) is not { } required) {
        this._diagnostics.Add(module.Refuse(request.Node, $"the required file '{request.Specifier}', which is no module"));
        continue;
      }

      this._modules[Key(file)] = required;
      module.Imports[binding] = required;
      if (required.Problem is { } problem)
        this._diagnostics.Add(problem);
      this.Load(required, [.. path, required]);
    }

    this._order.Add(module);
  }

  /// <summary>The file a specifier of <c>require</c> names relative to the requiring file: as it is, with <c>.js</c>, or its <c>index.js</c>.</summary>
  private string? Resolve(string from, string specifier) {
    if (!specifier.StartsWith("./", StringComparison.Ordinal) && !specifier.StartsWith("../", StringComparison.Ordinal))
      return null;

    var directory = Path.GetDirectoryName(from)!;
    var full = Path.GetFullPath(Path.Combine(directory, specifier));
    return new[] { full, full + ".js", Path.Combine(full, "index.js") }.FirstOrDefault(candidate => this._read(candidate) is not null);
  }

  /// <summary>Rewrites a module's references to the modules it requires: <c>B.x</c> to the name <c>x</c> has in <c>B</c>.</summary>
  private void Rewrite(Module module) {
    // A name taken of a module is the declaration of that name in it.
    foreach (var (name, binding, at) in module.Taken) {
      var required = module.Imports[binding];
      if (required.Exports.TryGetValue(name, out var local)) {
        if (local != name)
          this._diagnostics.Add(module.Refuse(at, $"'{name}', which the module '{binding}' exports under another name"));
      } else {
        this._diagnostics.Add(required.Opaque is { } opaque ? required.Refuse(opaque, "an export that is no name of the module") : module.Refuse(at, $"'{name}', which the module '{binding}' does not export by name"));
      }
    }

    foreach (var statement in module.Kept)
      this.Rewrite(module, statement, null);
  }

  private void Rewrite(Module module, Node node, Node? parent) {
    if (node is MemberExpression { Computed: false, Object: Identifier { Name: var name }, Property: Identifier { Name: var member } } access
        && module.Imports.TryGetValue(name, out var required)) {
      if (required.Object is { } whole) {
        if (whole != name)
          this._diagnostics.Add(module.Refuse(access, $"a module bound to '{name}' whose export is the object '{whole}'"));
      } else if (required.Exports.TryGetValue(member, out var local)) {
        module.Replace(access.Start, access.End, local);
      } else if (required.Opaque is { } opaque) {
        this._diagnostics.Add(required.Refuse(opaque, "an export that is no name of the module"));
      } else {
        this._diagnostics.Add(module.Refuse(access, $"'{member}', which the module '{name}' does not export"));
      }

      return;
    }

    if (node is Identifier { Name: var used } identifier && module.Imports.TryGetValue(used, out var bound) && bound.Object != used
        && !(parent is MemberExpression { Computed: false } owner && owner.Property == identifier)
        && !(parent is Property { Computed: false } property && property.Key == identifier && !property.Shorthand)) {
      this._diagnostics.Add(module.Refuse(identifier, $"the module '{used}' as a value"));
      return;
    }

    foreach (var child in node.ChildNodes)
      this.Rewrite(module, child, node);
  }

  /// <summary>
  /// A name that two modules declare at their top level, which in the one script would be one: renamed
  /// apart in a module that does not export it (#1376) - every reference to it there, a shorthand
  /// property keeping its key - and refused where both export it.
  /// </summary>
  private void Collisions() {
    var owners = new Dictionary<string, Module>(StringComparer.Ordinal);
    foreach (var module in this._order) {
      foreach (var (name, node) in module.Declarations.ToList()) {
        if (!owners.TryGetValue(name, out var first)) {
          owners[name] = module;
          continue;
        }

        if (!first.Exposes(name)) {
          this.Rename(first, name);
          owners[name] = module;
        } else if (!module.Exposes(name)) {
          this.Rename(module, name);
        } else {
          this._diagnostics.Add(module.Refuse(node, $"the name '{name}', which '{Path.GetFileName(first.Path)}' declares as well"));
        }
      }
    }
  }

  /// <summary>Renames every reference to a name in a module - no member name, no key of a property, a shorthand property keeping its key - to a name no file of the graph uses.</summary>
  private void Rename(Module module, string name) {
    var fresh = name;
    for (var suffix = 2; this._renamed.Contains(fresh) || this._order.Any(other => System.Text.RegularExpressions.Regex.IsMatch(other.Source, $@"(?<![\w$]){System.Text.RegularExpressions.Regex.Escape(fresh)}(?![\w$])")); ++suffix)
      fresh = $"{name}_{suffix}";

    this._renamed.Add(fresh);

    void Visit(Node node, Node? parent) {
      if (node is Identifier { Name: var used } identifier && used == name && !module.Covers(identifier.Start, identifier.End)) {
        switch (parent) {
          case MemberExpression { Computed: false } member when member.Property == identifier:
          case MethodDefinition { Computed: false } method when method.Key == identifier:
          case PropertyDefinition { Computed: false } field when field.Key == identifier:
            return;
          case Property { Shorthand: true } shorthand:
            // { x } of a renamed x keeps the key x; key and value are one identifier, written once.
            if (shorthand.Key == identifier)
              module.Replace(identifier.Start, identifier.End, $"{name}: {fresh}");
            return;
          case Property { Computed: false } property when property.Key == identifier:
            return;
          default:
            module.Replace(identifier.Start, identifier.End, fresh);
            return;
        }
      }

      foreach (var child in node.ChildNodes)
        Visit(child, node);
    }

    foreach (var statement in module.Kept)
      Visit(statement, null);
  }

  /// <summary>The script: every module's text with what it folds away blanked, then what the entry does not reach blanked as well.</summary>
  private LinkedSource Compose(Module entry) {
    var files = new List<LinkedFile>();
    var text = new StringBuilder();
    var line = 1;
    foreach (var module in this._order) {
      var rewritten = module.Rewritten();
      var lines = rewritten.Count(character => character == '\n') + 1;
      files.Add(new LinkedFile(module.Path, line, lines));
      text.Append(rewritten).Append('\n');
      line += lines;
    }

    var linked = new LinkedSource(text.ToString(), files);
    return Reachability.Shake(linked, files[^1]);
  }

  /// <summary>A required file: where the specifier stands and what it says.</summary>
  /// <param name="Node">The <c>require</c> call, or the factory parameter it is bound to.</param>
  /// <param name="Specifier">The text given to <c>require</c>.</param>
  private sealed record Request(Node Node, string Specifier);

  /// <summary>One file of the graph: its shape, its body, what it requires and exports and how its text changes.</summary>
  private sealed class Module {

    private readonly List<(int Start, int End, string Text)> _edits = [];

    private Module(string path, string source) {
      this.Path = path;
      this.Source = source;
    }

    public string Path { get; }

    public string Source { get; }

    /// <summary>The statements of the module that stay in the script.</summary>
    public List<Statement> Kept { get; } = [];

    /// <summary>The files the module requires, by the name it binds each to, in the order of the source.</summary>
    public List<(string Binding, Request Request)> Requests { get; } = [];

    /// <summary>The module each binding of a required file is, once loaded.</summary>
    public Dictionary<string, Module> Imports { get; } = new(StringComparer.Ordinal);

    /// <summary>What the module exports by name: the name of each export and the name of its declaration.</summary>
    public Dictionary<string, string> Exports { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The first export that is no name of the module - a value computed in the export itself - or
    /// <see langword="null"/>. Only a module that takes something of this one is refused for it: the
    /// entry's own exports are not read.
    /// </summary>
    public Node? Opaque { get; private set; }

    /// <summary>The names the module takes of required files under their own names - <c>const { x } = B</c>, <c>const x = B.x</c> - each with the binding of its file.</summary>
    public List<(string Name, string Binding, Node At)> Taken { get; } = [];

    /// <summary>The name of the one object the module exports - <c>module.exports = OpCodes</c> - or <see langword="null"/>.</summary>
    public string? Object { get; private set; }

    /// <summary>The names the module declares at its top level, each with its declaration.</summary>
    public IEnumerable<(string Name, Node Node)> Declarations {
      get {
        foreach (var statement in this.Kept) {
          switch (statement) {
            case FunctionDeclaration { Id: { } id } function:
              yield return (id.Name, function);
              break;
            case ClassDeclaration { Id: { } id } type:
              yield return (id.Name, type);
              break;
            case VariableDeclaration declaration:
              foreach (var declarator in declaration.Declarations)
                if (declarator.Id is Identifier { Name: var name })
                  yield return (name, declarator);
              break;
          }
        }
      }
    }

    /// <summary>A diagnostic of a construct the link does not take, at a node of this file.</summary>
    public Diagnostic Refuse(Node node, string what)
      => JavaScriptFrontend.UnsupportedDescriptor.At($"'{what}' is not supported yet", this.Path, node.Location.Start.Line, node.Location.Start.Column + 1);

    /// <summary>Whether the module exports a name, by name or as its one object, so that another module may refer to it.</summary>
    public bool Exposes(string name) => this.Exports.ContainsValue(name) || this.Object == name;

    /// <summary>Whether an edit already covers a part of a range.</summary>
    public bool Covers(int start, int end) => this._edits.Exists(edit => edit.Start < end && start < edit.End);

    /// <summary>Replaces a range of the file by a name, padded with blanks to the length of the range so that every later column stays.</summary>
    public void Replace(int start, int end, string name) => this._edits.Add((start, end, name.PadRight(end - start)));

    /// <summary>Blanks a range of the file: every character but a line break becomes a blank.</summary>
    public void Blank(int start, int end)
      => this._edits.Add((start, end, new string([.. this.Source[start..end].Select(character => character is '\n' or '\r' ? character : ' ')])));

    /// <summary>The file with its edits applied.</summary>
    public string Rewritten() {
      var text = new StringBuilder(this.Source);
      foreach (var (start, end, replacement) in this._edits.OrderByDescending(edit => edit.Start)) {
        text.Remove(start, end - start);
        text.Insert(start, replacement);
      }

      return text.ToString();
    }

    /// <summary>The module a file is, or <see langword="null"/> when it is none of the three shapes.</summary>
    public static Module? Of(string path, string source, Program program) {
      var statements = program.Body.Where(statement => statement is not (Directive or EmptyStatement)).ToList();
      var module = new Module(path, source);
      if (statements is [ExpressionStatement { Expression: CallExpression { Callee: FunctionExpression wrapper } call }]) {
        var factory = call.Arguments.OfType<FunctionExpression>().LastOrDefault();
        if (factory is not null) {
          module.Factory(statements[0], wrapper, call, factory);
          return module;
        }

        if (Exported(wrapper.Body)) {
          module.Wrapper(statements[0], wrapper);
          return module;
        }

        return null;
      }

      if (!program.Body.Any(statement => Requires(statement) is not null) && !Exported(program))
        return null;

      foreach (var statement in program.Body)
        module.Statement(statement, false);
      return module;
    }

    /// <summary>Whether a node assigns <c>module.exports</c> anywhere in it.</summary>
    private static bool Exported(Node node)
      => node is AssignmentExpression { Left: MemberExpression { Object: Identifier { Name: "module" }, Property: Identifier { Name: "exports" } } }
        || node.ChildNodes.Any(Exported);

    /// <summary>A UMD factory: the wrapper and its call are blanked around the factory's body, whose parameters are the files the wrapper requires.</summary>
    private void Factory(Statement statement, FunctionExpression wrapper, CallExpression call, FunctionExpression factory) {
      var index = call.Arguments.ToList().IndexOf(factory);
      var parameter = index < wrapper.Params.Count ? wrapper.Params[index] as Identifier : null;
      var calls = parameter is null ? [] : Calls(wrapper.Body, parameter.Name).ToList();
      var required = calls.FirstOrDefault(made => made.Arguments.All(argument => Specifier(argument) is not null));
      if (required is null) {
        this.Problem = this.Refuse(wrapper, "a factory that its wrapper does not call with the files it requires");
      } else {
        for (var position = 0; position < factory.Params.Count; ++position) {
          if (factory.Params[position] is not Identifier { Name: var name } bound) {
            this.Problem = this.Refuse(factory.Params[position], "a factory parameter that is no name");
            continue;
          }

          if (position < required.Arguments.Count)
            this.Requests.Add((name, new Request(required.Arguments[position], Specifier(required.Arguments[position])!)));
          else
            this.Problem = this.Refuse(bound, $"the factory parameter '{name}', which the wrapper gives no required file");
        }
      }

      var body = (FunctionBody)factory.Body;
      this.Blank(statement.Start, body.Start + 1);
      this.Blank(body.End - 1, this.Source.Length);
      this.Blank(0, statement.Start);
      foreach (var inner in body.Body)
        this.Statement(inner, true);
    }

    /// <summary>An immediately invoked wrapper: its call is blanked around its body, whose parameters are the global object.</summary>
    private void Wrapper(Statement statement, FunctionExpression wrapper) {
      var body = (FunctionBody)wrapper.Body;
      this.Blank(0, body.Start + 1);
      this.Blank(body.End - 1, this.Source.Length);
      var globals = wrapper.Params.OfType<Identifier>().Select(parameter => parameter.Name).ToList();
      foreach (var inner in body.Body)
        this.Statement(inner, false, globals);
    }

    /// <summary>Why the file cannot be linked whatever the others are, or <see langword="null"/>.</summary>
    public Diagnostic? Problem { get; private set; }

    /// <summary>Takes a statement of the module's body: kept, or blanked where it only exports, requires or probes the environment.</summary>
    private void Statement(Statement statement, bool factory, IReadOnlyList<string>? globals = null) {
      var modules = this.Requests.Select(request => request.Binding).ToList();
      switch (statement) {
        case Directive or EmptyStatement:
          this.Blank(statement.Start, statement.End);
          return;
        case ReturnStatement { Argument: var value } when factory:
          this.Export(value);
          this.Blank(statement.Start, statement.End);
          return;
        case IfStatement { Test: var test } when Probes(test) || IsAbsent(test, modules):
          foreach (var exported in Assignments(statement))
            this.Export(exported);
          this.Blank(statement.Start, statement.End);
          return;
        case ExpressionStatement { Expression: AssignmentExpression { Operator: Operator.Assignment, Left: MemberExpression target, Right: var value } }
            when IsExports(target) || target.Object is Identifier { Name: var owner } && (globals ?? []).Contains(owner):
          if (IsExports(target))
            this.Export(value);
          this.Blank(statement.Start, statement.End);
          return;
        case VariableDeclaration declaration when declaration.Declarations.All(declarator => Requires(declarator.Init) is not null || IsTaken(declarator, modules)):
          foreach (var declarator in declaration.Declarations)
            this.Bind(declarator);
          this.Blank(statement.Start, statement.End);
          return;
      }

      this.Kept.Add(statement);
    }

    /// <summary>
    /// Whether a declaration only takes something of a required file: destructures it, gives it another
    /// name (<c>const C = B;</c>) or takes one export under its own name (<c>const x = B.x;</c>).
    /// </summary>
    private static bool IsTaken(VariableDeclarator declarator, List<string> modules) => declarator switch {
      { Id: ObjectPattern or Identifier, Init: Identifier { Name: var bound } } => modules.Contains(bound),
      { Id: Identifier { Name: var name }, Init: MemberExpression { Computed: false, Object: Identifier { Name: var owner }, Property: Identifier { Name: var member } } } => member == name && modules.Contains(owner),
      _ => false
    };

    /// <summary>Binds what a declaration takes from a required file: the file itself, another name of it, or the names it destructures.</summary>
    private void Bind(VariableDeclarator declarator) {
      if (declarator is { Id: Identifier { Name: var alias }, Init: Identifier { Name: var bound } }) {
        this.Requests.Add((alias, this.Requests.First(request => request.Binding == bound).Request));
        return;
      }

      // const x = B.x is const { x } = B: the name it has in the module.
      if (declarator is { Id: Identifier { Name: var taken }, Init: MemberExpression { Object: Identifier { Name: var owner } } }) {
        this.Taken.Add((taken, owner, declarator));
        return;
      }

      if (Requires(declarator.Init) is { } specifier) {
        if (declarator.Id is Identifier { Name: var name })
          this.Requests.Add((name, new Request(declarator.Init!, specifier)));
        else
          this.Problem = this.Refuse(declarator, "a required file that is destructured");
        return;
      }

      foreach (var property in ((ObjectPattern)declarator.Id).Properties) {
        // Every name a module destructures is the name it has in that module: the script has one scope.
        if (property is not Property { Computed: false, Key: Identifier { Name: var key }, Value: Identifier { Name: var local } } || key != local)
          this.Problem = this.Refuse(property, "a destructured export under another name");
        else
          this.Taken.Add((key, ((Identifier)declarator.Init!).Name, property));
      }
    }

    /// <summary>Records what a module exports: an object literal of names, or the name of one object.</summary>
    private void Export(Node? value) {
      switch (value) {
        case null:
          // return; exports nothing.
          break;
        case Identifier { Name: var name }:
          this.Object = name;
          break;
        case ObjectExpression literal:
          foreach (var member in literal.Properties) {
            if (member is Property { Computed: false, Key: Identifier { Name: var key }, Value: Identifier { Name: var local } })
              this.Exports[key] = local;
            else
              this.Opaque ??= member;
          }

          break;
        default:
          this.Opaque ??= value;
          break;
      }
    }

    /// <summary>The assignments of <c>module.exports</c> inside a statement, their values.</summary>
    private static IEnumerable<Node> Assignments(Node node) {
      if (node is AssignmentExpression { Left: MemberExpression target, Right: var value } && IsExports(target)) {
        yield return value;
        yield break;
      }

      foreach (var child in node.ChildNodes)
        foreach (var assigned in Assignments(child))
          yield return assigned;
    }

    /// <summary>Whether a member is <c>module.exports</c>.</summary>
    private static bool IsExports(MemberExpression member)
      => member is { Computed: false, Object: Identifier { Name: "module" }, Property: Identifier { Name: "exports" } };

    /// <summary>Whether a test only finds out where the code runs: <c>typeof module</c>, <c>module.exports</c>, <c>define.amd</c> and their combinations.</summary>
    private static bool Probes(Node test) => test switch {
      UnaryExpression { Operator: Operator.TypeOf, Argument: Identifier { Name: var name } } => _environment.Contains(name),
      UnaryExpression { Operator: Operator.LogicalNot, Argument: var inner } => Probes(inner),
      MemberExpression { Computed: false, Object: Identifier { Name: var name } } => _environment.Contains(name),
      Identifier { Name: var name } => _environment.Contains(name),
      StringLiteral => true,
      BinaryExpression { Left: var left, Right: var right } => Probes(left) && Probes(right),
      _ => false
    };

    /// <summary>Whether a test asks whether a required module is missing, <c>!B</c>: once linked it never is.</summary>
    private static bool IsAbsent(Node test, List<string> modules)
      => test is UnaryExpression { Operator: Operator.LogicalNot, Argument: Identifier { Name: var name } } && modules.Contains(name);

    /// <summary>The specifier of a <c>require('…')</c> call, or <see langword="null"/>.</summary>
    private static string? Requires(Node? node)
      => node is CallExpression { Callee: Identifier { Name: "require" }, Arguments: [StringLiteral { Value: var specifier }] } ? specifier : null;

    /// <summary>The specifier of a <c>require('…')</c> argument, or <see langword="null"/>.</summary>
    private static string? Specifier(Node node) => Requires(node);

    /// <summary>The calls of a named function anywhere in a node.</summary>
    private static IEnumerable<CallExpression> Calls(Node node, string name) {
      if (node is CallExpression { Callee: Identifier { Name: var callee } } call && callee == name)
        yield return call;

      foreach (var child in node.ChildNodes)
        foreach (var found in Calls(child, name))
          yield return found;
    }

    /// <summary>Whether a statement requires a file at the top level.</summary>
    public static string? Requires(Statement statement)
      => statement is VariableDeclaration declaration ? declaration.Declarations.Select(declarator => Requires(declarator.Init)).FirstOrDefault(found => found is not null) : null;

  }

}
