using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Lowering;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Resolution;

/// <summary>
/// Refuses, with the diagnostic of its family, every construct of a function outside the subset the
/// import supports, before any type is inferred: so that what follows sees only constructs it knows.
/// What only the types decide - a method a receiver does not have, an operation of two types - the
/// lowering refuses.
/// </summary>
internal sealed class SubsetCheck(ModuleModel module, IReadOnlyDictionary<PyNode, Variable> bound, List<Diagnostic> diagnostics) {

  /// <summary>The methods of lists the import refuses, by name.</summary>
  public static readonly IReadOnlySet<string> RefusedSequenceMethods = new HashSet<string> { "insert", "index", "count", "sort", "reverse" };

  /// <summary>The methods of strings and bytes the import refuses, by name.</summary>
  public static readonly IReadOnlySet<string> RefusedStringMethods = new HashSet<string> {
    "join", "split", "rsplit", "splitlines", "format", "decode", "upper", "lower", "strip", "lstrip", "rstrip", "startswith", "endswith", "find", "rfind",
    "replace", "hex", "fromhex", "isdigit", "isalpha", "isalnum", "isspace", "isupper", "islower", "zfill", "ljust", "rjust", "center", "title",
    "capitalize", "partition", "rpartition", "casefold", "swapcase", "expandtabs", "translate", "maketrans", "removeprefix", "removesuffix"
  };

  /// <summary>The methods of dicts and sets the import refuses, by name.</summary>
  public static readonly IReadOnlySet<string> RefusedDictMethods = new HashSet<string> {
    "setdefault", "update", "popitem", "union", "intersection", "difference", "symmetric_difference", "issubset", "issuperset", "isdisjoint", "fromkeys"
  };

  private FunctionModel? _function;
  private SourceFile File => module.File;

  /// <summary>Whether the statement being checked stands in an <c>except</c> clause, where a bare <c>raise</c> raises again.</summary>
  private int _handlers;

  /// <summary>How deep the statement being checked stands in loops and conditionals, where a nested <c>def</c> is refused.</summary>
  private int _conditional;

  /// <summary>Whether the statement being checked stands in a <c>finally</c> block, which nothing may leave.</summary>
  private int _finally;

  /// <summary>Checks the expressions of the module's own statements, which run once, at import.</summary>
  public void CheckModule() {
    this._function = null;
    foreach (var statement in module.Statements.Where(statement => statement.Kind is "Assign" or "AnnAssign" or "AugAssign"))
      foreach (var expression in ScopeResolver.Expressions(statement).Where(expression => !expression.Is("Name") || statement.Is("AugAssign")))
        this.Expression(expression);
  }

  /// <summary>Checks the body of a function and of the functions nested in it; every construct outside the subset adds a diagnostic.</summary>
  public void Check(FunctionModel function) {
    this._function = function;
    foreach (var statement in function.Body)
      this.Statement(statement);

    foreach (var nested in function.Nested.Values)
      this.Check(nested);
  }

  private void Refuse(PyNode node, DiagnosticDescriptor descriptor, string message) => diagnostics.Add(this.File.At(node, descriptor, message));

  private string Quote(PyNode node) => this.File.Quote(node);

  private void Statement(PyNode statement) {
    switch (statement.Kind) {
      case "Expr":
        var value = statement.Node("value")!;
        // A string or another constant as a statement is a docstring or does nothing; yield is a statement of a generator.
        if (value.Is("Yield") || value.Is("YieldFrom")) {
          if (value.Node("value") is { } yielded)
            this.Expression(yielded);
        } else if (!value.Is("Constant")) {
          this.Expression(value);
        }

        break;
      case "Return":
        if (this._finally > 0)
          this.Refuse(statement, PythonFrontend.UnsupportedExceptionDescriptor, "exception construct 'return in finally' is not supported by the Python frontend");

        if (statement.Node("value") is { } returned && !(returned.Is("Constant") && returned.Text("kind") == "none"))
          this.Expression(returned);

        break;
      case "Assign":
        this.Assign(statement);
        break;
      case "AugAssign":
        this.Target(statement.Node("target")!, false);
        this.Operator(statement, statement.Operator()!);
        this.Expression(statement.Node("value")!);
        break;
      case "AnnAssign":
        this.Target(statement.Node("target")!, false);
        if (statement.Node("value") is { } annotated)
          this.Expression(annotated);

        break;
      case "If":
      case "While":
        this.Expression(statement.Node("test")!);
        ++this._conditional;
        this.Statements(statement);
        --this._conditional;
        break;
      case "For":
        this.Target(statement.Node("target")!, true);
        this.Expression(statement.Node("iter")!);
        ++this._conditional;
        this.Statements(statement);
        --this._conditional;
        break;
      case "Match":
        this.Match(statement);
        break;
      case "Assert":
        this.Expression(statement.Node("test")!);
        if (statement.Node("msg") is { } message && !(message.Is("Constant") && message.Text("kind") == "str"))
          this.Refuse(message, PythonFrontend.UnsupportedStatementDescriptor, "statement 'assert' with a message other than a string literal is not supported by the Python frontend");

        break;
      case "Break" or "Continue":
        if (this._finally > 0)
          this.Refuse(statement, PythonFrontend.UnsupportedExceptionDescriptor, $"exception construct '{statement.Kind.ToLowerInvariant()} in finally' is not supported by the Python frontend");

        break;
      case "Pass":
        break;
      case "Raise":
        this.Raise(statement);
        break;
      case "Try":
        this.Try(statement);
        break;
      case "FunctionDef":
        this.Nested(statement);
        break;
      case "AsyncFunctionDef":
        this.Refuse(statement, PythonFrontend.EscapingFunctionDescriptor, Escaping(statement.Text("name")!) + " (coroutines: #752)");
        break;
      case "Global":
        foreach (var name in statement.Texts("names").Where(name => !module.GlobalVariables.ContainsKey(name)))
          this.Refuse(statement, PythonFrontend.UnresolvedDescriptor, $"'global {name}' is neither part of this import nor in the Python call map: '{name}' is assigned at no module level");

        break;
      case "Nonlocal":
        foreach (var name in statement.Texts("names").Where(name => this._function!.Parent?.Find(name) is null))
          this.Refuse(statement, PythonFrontend.UnresolvedDescriptor, $"'nonlocal {name}' is neither part of this import nor in the Python call map: no enclosing function binds '{name}'");

        break;
      case "With" or "AsyncWith":
        this.Refuse(statement, PythonFrontend.UnsupportedExceptionDescriptor, "exception construct 'with' is not supported by the Python frontend");
        break;
      case "TryStar":
        this.Refuse(statement, PythonFrontend.UnsupportedExceptionDescriptor, "exception construct 'except*' is not supported by the Python frontend");
        break;
      case "Delete":
        foreach (var target in statement.Nodes("targets"))
          this.Delete(target);

        break;
      case "Import" or "ImportFrom":
        this.Refuse(statement, PythonFrontend.UnresolvedDescriptor, $"'{this.Quote(statement)}' is neither part of this import nor in the Python call map: an import inside a function");
        break;
      case "ClassDef":
        this.Refuse(statement, PythonFrontend.UnsupportedClassDescriptor, $"'nested class {statement.Text("name")}' is outside the first Python subset (dataclasses, simple final classes, enums)");
        break;
      default:
        this.Refuse(statement, PythonFrontend.UnsupportedStatementDescriptor, $"statement '{Keyword(statement)}' is not supported by the Python frontend");
        break;
    }
  }

  private void Statements(PyNode statement) {
    foreach (var field in new[] { "body", "orelse" })
      foreach (var inner in statement.Nodes(field))
        this.Statement(inner);
  }

  private static string Keyword(PyNode statement) => statement.Kind switch {
    "TypeAlias" => "type",
    "AsyncFor" => "async for",
    _ => statement.Kind.ToLowerInvariant()
  };

  /// <summary>The message of a function value that escapes, a generator or a coroutine.</summary>
  public static string Escaping(string name)
    => $"the function value '{name}' escapes, or '{name}' is a generator or coroutine; only direct calls, nested functions called by name and lambdas passed to the listed built-ins are imported";

  private void Nested(PyNode statement) {
    var name = statement.Text("name")!;
    if (this._conditional > 0)
      this.Refuse(statement, PythonFrontend.UnsupportedFunctionDescriptor, $"'def {name} inside a loop or conditional' is not supported for functions by the Python frontend");
    else if (Collector.Feature(statement) is { } feature)
      this.Refuse(statement, PythonFrontend.UnsupportedFunctionDescriptor, $"'{feature}' is not supported for functions by the Python frontend");
    else if (this._function!.Nested.TryGetValue(name, out var nested) && !ReferenceEquals(nested.Node, statement))
      this.Refuse(statement, PythonFrontend.UnsupportedFunctionDescriptor, $"'redefinition of {name}' is not supported for functions by the Python frontend");
  }

  private void Raise(PyNode statement) {
    if (statement.Node("cause") is not null) {
      this.Refuse(statement, PythonFrontend.UnsupportedExceptionDescriptor, "exception construct 'raise … from' is not supported by the Python frontend");
      return;
    }

    if (statement.Node("exc") is not { } raised) {
      if (this._handlers == 0)
        this.Refuse(statement, PythonFrontend.UnsupportedExceptionDescriptor, "exception construct 'raise outside an except clause' is not supported by the Python frontend");

      return;
    }

    var callee = raised.Is("Call") ? raised.Node("func")! : raised;
    if (callee.Is("Name") && this.Resolve(callee) == Binding.Class) {
      if (Classes.Of(module, callee.Text("id")!) is { IsException: false })
        this.Refuse(raised, PythonFrontend.UnsupportedExceptionDescriptor, $"exception construct 'raise {this.Quote(raised)}, which is no exception' is not supported by the Python frontend");

      if (raised.Is("Call")) {
        if (raised.Nodes("keywords").Count > 0 || raised.Nodes("args").Count > 1)
          this.Refuse(raised, PythonFrontend.UnsupportedExceptionDescriptor, $"exception construct 'an exception made with other arguments than a message: {this.Quote(raised)}' is not supported by the Python frontend");

        foreach (var argument in raised.Nodes("args"))
          this.Expression(argument);
      }

      return;
    }

    this.Expression(raised);
  }

  private void Try(PyNode statement) {
    foreach (var inner in statement.Nodes("body"))
      this.Statement(inner);

    foreach (var handler in statement.Nodes("handlers")) {
      if (handler.Node("type") is { } type) {
        var names = type.Is("Tuple") ? type.Nodes("elts") : [type];
        foreach (var name in names) {
          if (!name.Is("Name") || this.Resolve(name) != Binding.Class || Classes.Of(module, name.Text("id")!) is { IsException: false })
            this.Refuse(name, PythonFrontend.UnsupportedExceptionDescriptor, $"exception construct 'except {this.Quote(name)}, which names no exception class' is not supported by the Python frontend");
        }
      }

      ++this._handlers;
      foreach (var inner in handler.Nodes("body"))
        this.Statement(inner);

      --this._handlers;
    }

    foreach (var inner in statement.Nodes("orelse"))
      this.Statement(inner);

    ++this._finally;
    foreach (var inner in statement.Nodes("finalbody"))
      this.Statement(inner);

    --this._finally;
  }

  private void Delete(PyNode target) {
    switch (target.Kind) {
      case "Subscript" when target.Node("slice") is { Kind: "Slice" }:
        this.Refuse(target, PythonFrontend.UnsupportedSliceDescriptor, $"slice form 'del {this.Quote(target)}' is not supported; slices are imported as values only");
        break;
      case "Subscript":
        // del d[k] of a dict; the lowering refuses it of a list.
        this.Expression(target.Node("value")!);
        this.Expression(target.Node("slice")!);
        break;
      case "Attribute":
        this.Refuse(target, PythonFrontend.DynamicAttributeDescriptor, $"attribute '{target.Text("attr")}' of class '{this.Quote(target.Node("value")!)}' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported (del)");
        break;
      default:
        this.Refuse(target, PythonFrontend.UnsupportedStatementDescriptor, "statement 'del' is not supported by the Python frontend");
        break;
    }
  }

  private void Assign(PyNode statement) {
    foreach (var target in statement.Nodes("targets")) {
      this.Target(target, false);
      if (target.Kind is "Tuple" or "List" && statement.Node("value") is { Kind: "Tuple" or "List" } values && values.Nodes("elts").Count != target.Nodes("elts").Count)
        this.Refuse(statement, PythonFrontend.UnsupportedStatementDescriptor, "statement 'tuple assignment whose right side is no tuple of as many values' is not supported by the Python frontend");
    }

    this.Expression(statement.Node("value")!);
  }

  private void Target(PyNode target, bool loop) {
    switch (target.Kind) {
      case "Name":
        return;
      case "Tuple" or "List":
        foreach (var element in target.Nodes("elts")) {
          if (!element.Is("Name"))
            this.Refuse(element, PythonFrontend.UnsupportedStatementDescriptor, $"statement '{(element.Is("Starred") ? "starred assignment target" : "nested tuple assignment")}' is not supported by the Python frontend");
        }

        return;
      case "Subscript" when !loop:
        if (target.Node("slice")!.Is("Slice"))
          this.Refuse(target, PythonFrontend.UnsupportedSliceDescriptor, $"slice form '{this.Quote(target)} = …' is not supported; slices are imported as values only");
        else
          this.Subscript(target);

        return;
      case "Attribute" when !loop:
        this.Expression(target.Node("value")!);
        return;
      default:
        this.Refuse(target, PythonFrontend.UnsupportedStatementDescriptor, $"statement '{(target.Is("Starred") ? "starred assignment target" : loop ? "a loop variable other than names" : "assignment to " + this.Quote(target))}' is not supported by the Python frontend");
        return;
    }
  }

  private void Match(PyNode statement) {
    this.Expression(statement.Node("subject")!);
    foreach (var matchCase in statement.Nodes("cases")) {
      this.Pattern(matchCase.Node("pattern")!, true);
      if (matchCase.Node("guard") is { } guard)
        this.Expression(guard);

      ++this._conditional;
      foreach (var inner in matchCase.Nodes("body"))
        this.Statement(inner);

      --this._conditional;
    }
  }

  /// <summary>Checks a pattern: literals, members of an enum, alternatives of them, a capture, the wildcard.</summary>
  private void Pattern(PyNode pattern, bool top) {
    switch (pattern.Kind) {
      case "MatchValue" when Literal(pattern.Node("value")!) || this.Member(pattern.Node("value")!):
      case "MatchSingleton":
        return;
      case "MatchAs" when pattern.Node("pattern") is null && (top || pattern.Text("name") is null):
        return;
      case "MatchOr":
        foreach (var alternative in pattern.Nodes("patterns"))
          this.Pattern(alternative, false);

        return;
      default:
        this.Refuse(pattern, PythonFrontend.UnsupportedPatternDescriptor, $"match pattern '{this.Quote(pattern)}' is not supported; supported are literals, members of an enum, '|', captures, '_' and guards");
        return;
    }
  }

  /// <summary>Whether a value pattern is a number or string literal, possibly negated.</summary>
  private static bool Literal(PyNode value) {
    if (value.Is("UnaryOp") && value.Operator() == "USub")
      value = value.Node("operand")!;

    return value.Is("Constant") && value.Text("kind") is "int" or "float" or "str";
  }

  /// <summary>Whether a value pattern is a member of an enum of the module: <c>E.A</c>.</summary>
  private bool Member(PyNode value)
    => value.Is("Attribute") && value.Node("value") is { Kind: "Name" } owner && module.Classes.TryGetValue(owner.Text("id")!, out var type)
      && type.IsEnum && type.Members.Any(member => member.Name == value.Text("attr"));

  private void Operator(PyNode node, string op) {
    if (op == "MatMult")
      this.Refuse(node, PythonFrontend.UnsupportedDescriptor, "'@' is not supported by the Python frontend");
  }

  private Binding Resolve(PyNode name) => ScopeResolver.Resolve(this._function, module, name.Text("id")!, bound, name);

  private void Expression(PyNode expression) {
    switch (expression.Kind) {
      case "Constant":
        var kind = expression.Text("kind");
        if (kind is not ("int" or "float" or "bool" or "none" or "str" or "bytes"))
          this.Refuse(expression, PythonFrontend.UnsupportedDescriptor, $"'{this.Quote(expression)}' is not supported by the Python frontend");
        else if (expression.Flag("surrogatePair"))
          this.Refuse(expression, PythonFrontend.UnsupportedStringDescriptor,
            "string operation 'a str of a lone high surrogate before a lone low one, which the UTF-16 of the IR holds as one code point,' is not supported by the Python frontend");

        break;
      case "Name":
        this.Name(expression);
        break;
      case "BinOp":
        this.Expression(expression.Node("left")!);
        this.Operator(expression, expression.Operator()!);
        this.Expression(expression.Node("right")!);
        break;
      case "UnaryOp":
        this.Expression(expression.Node("operand")!);
        break;
      case "BoolOp":
        foreach (var value in expression.Nodes("values"))
          this.Expression(value);

        break;
      case "Compare":
        this.Expression(expression.Node("left")!);
        // Every comparison operator Python has is in the subset; the operands decide.
        foreach (var right in expression.Nodes("comparators"))
          this.Expression(right);

        break;
      case "IfExp":
        this.Expression(expression.Node("test")!);
        this.Expression(expression.Node("body")!);
        this.Expression(expression.Node("orelse")!);
        break;
      case "Call":
        this.Call(expression);
        break;
      case "List" or "Tuple":
        foreach (var element in expression.Nodes("elts")) {
          if (element.Is("Starred"))
            this.Refuse(element, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{this.Quote(element)} in a display' is not supported by the Python frontend");
          else
            this.Expression(element);
        }

        break;
      case "Subscript":
        this.Subscript(expression);
        break;
      case "Attribute":
        this.Attribute(expression);
        break;
      case "ListComp" or "GeneratorExp":
        this.Comprehension(expression);
        break;
      case "NamedExpr":
        this.Expression(expression.Node("value")!);
        break;
      case "Lambda":
        this.Refuse(expression, PythonFrontend.EscapingFunctionDescriptor, Escaping(this.Quote(expression)) + " (escaping function values: #753)");
        break;
      case "Yield" or "YieldFrom":
        this.Refuse(expression, PythonFrontend.EscapingFunctionDescriptor, Escaping(this._function!.Name) + " (a yield used as a value: #760)");
        break;
      case "Await":
        this.Refuse(expression, PythonFrontend.EscapingFunctionDescriptor, Escaping(this._function!.Name) + " (coroutines: #752)");
        break;
      case "JoinedStr" or "TemplateStr":
        this.Refuse(expression, PythonFrontend.UnsupportedStringDescriptor, "string operation 'f-string' is not supported by the Python frontend");
        break;
      case "Dict":
        var values = expression.Nodes("values");
        for (var index = 0; index < values.Count; ++index) {
          // A key of None is the ** that unpacks a dict into the display.
          if (expression.Item("keys", index) is not { } key) {
            this.Refuse(values[index], PythonFrontend.UnsupportedDictDescriptor, $"dict or set operation '**{this.Quote(values[index])} in a display' is not supported by the Python frontend");
            continue;
          }

          this.Expression(key);
          this.Expression(values[index]);
        }

        break;
      case "Set":
        foreach (var element in expression.Nodes("elts")) {
          if (element.Is("Starred"))
            this.Refuse(element, PythonFrontend.UnsupportedDictDescriptor, $"dict or set operation '{this.Quote(element)} in a display' is not supported by the Python frontend");
          else
            this.Expression(element);
        }

        break;
      case "DictComp" or "SetComp":
        this.Comprehension(expression);
        break;
      case "Starred":
        this.Refuse(expression, PythonFrontend.UnsupportedFunctionDescriptor, $"'unpacked arguments in {this.Quote(expression)}' is not supported for functions by the Python frontend");
        break;
      default:
        this.Refuse(expression, PythonFrontend.UnsupportedDescriptor, $"'{this.Quote(expression)}' is not supported by the Python frontend");
        break;
    }
  }

  private void Comprehension(PyNode comprehension) {
    foreach (var generator in comprehension.Nodes("generators")) {
      if (generator.Number("is_async") == 1)
        this.Refuse(generator, PythonFrontend.EscapingFunctionDescriptor, Escaping(this._function!.Name) + " (coroutines: #752)");

      this.Target(generator.Node("target")!, true);
      this.Expression(generator.Node("iter")!);
      foreach (var condition in generator.Nodes("ifs"))
        this.Expression(condition);
    }

    foreach (var part in new[] { "elt", "key", "value" })
      if (comprehension.Node(part) is { } produced)
        this.Expression(produced);
  }

  private void Subscript(PyNode subscript) {
    this.Expression(subscript.Node("value")!);
    var index = subscript.Node("slice")!;
    switch (index.Kind) {
      case "Slice":
        foreach (var bound in new[] { "lower", "upper", "step" })
          if (index.Node(bound) is { } part)
            this.Expression(part);

        break;
      case "Tuple" when index.Nodes("elts").Any(element => element.Is("Slice")):
        this.Refuse(index, PythonFrontend.UnsupportedSliceDescriptor, $"slice form '{this.Quote(subscript)}' is not supported; slices are imported as values only");
        break;
      default:
        this.Expression(index);
        break;
    }
  }

  private void Attribute(PyNode attribute) {
    var owner = attribute.Node("value")!;
    // math.pi, E.A, C.f: a name of the module or of an import, read as an attribute.
    if (owner.Is("Name") && this.Resolve(owner) is Binding.Import or Binding.Class) {
      if (this.Resolve(owner) == Binding.Import && module.Imports[owner.Text("id")!] is { User: null, Name: null } library && !LibraryCallMap.Has(library.Module, attribute.Text("attr")!))
        this.Refuse(attribute, PythonFrontend.UnresolvedDescriptor, $"'{this.Quote(attribute)}' is neither part of this import nor in the Python call map");

      return;
    }

    if (attribute.Text("attr") is "__dict__" or "__class__")
      this.Refuse(attribute, PythonFrontend.DynamicAttributeDescriptor, $"attribute '{attribute.Text("attr")}' of class '{this.Quote(owner)}' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported");

    this.Expression(owner);
  }

  private void Name(PyNode name) {
    var id = name.Text("id")!;
    switch (this.Resolve(name)) {
      case Binding.Function or Binding.Nested:
        this.Refuse(name, PythonFrontend.EscapingFunctionDescriptor, Escaping(id) + " (escaping function values: #753)");
        break;
      case Binding.Unknown:
        this.Refuse(name, PythonFrontend.UnresolvedDescriptor, $"'{id}' is neither part of this import nor in the Python call map");
        break;
      case Binding.BuiltIn when id is not "None":
        if (!Classes.IsBuiltinException(id))
          this.Refuse(name, PythonFrontend.EscapingFunctionDescriptor, Escaping(id) + " (escaping function values: #753)");

        break;
    }
  }

  /// <summary>Whether an argument is a function value in a place that takes one: a lambda or the name of a function.</summary>
  private bool FunctionValue(PyNode argument)
    => argument.Is("Lambda") || (argument.Is("Name") && this.Resolve(argument) is Binding.Function or Binding.Nested);

  private void FunctionArgument(PyNode argument) {
    if (argument.Is("Lambda")) {
      if (argument.Node("args")!.Nodes("args").Count != 1 || argument.Node("args")!.Nodes("defaults").Count > 0)
        this.Refuse(argument, PythonFrontend.EscapingFunctionDescriptor, Escaping(this.Quote(argument)) + " (a lambda of other than one parameter)");
      else
        this.Expression(argument.Node("body")!);
    }
  }

  private void Call(PyNode call) {
    var callee = call.Node("func")!;
    var arguments = call.Nodes("args");
    var keywords = call.Nodes("keywords");
    if (arguments.Any(argument => argument.Is("Starred")) || keywords.Any(keyword => keyword.Text("arg") is null)) {
      this.Refuse(call, PythonFrontend.UnsupportedFunctionDescriptor, $"'*args and **kwargs in {this.Quote(call)}' is not supported for functions by the Python frontend");
      return;
    }

    var name = callee.Is("Name") ? callee.Text("id")! : null;
    var binding = name is null ? Binding.Unknown : this.Resolve(callee);
    if (this._function is null && binding is Binding.Function or Binding.Nested || this._function is null && binding == Binding.Class && Classes.Of(module, name!) is { IsEnum: false, IsException: false }) {
      this.Refuse(call, PythonFrontend.UnsupportedStatementDescriptor, $"statement 'module-level call of {name}, which runs code of the module when it is imported,' is not supported by the Python frontend");
      return;
    }

    if (name is not null && binding == Binding.BuiltIn) {
      switch (name) {
        case "map" or "filter":
          if (arguments.Count != 2 || keywords.Count > 0 || !this.FunctionValue(arguments[0])) {
            this.Refuse(call, PythonFrontend.EscapingFunctionDescriptor, Escaping(name) + " (map and filter take a lambda or a function of the module and one sequence)");
            return;
          }

          this.FunctionArgument(arguments[0]);
          this.Expression(arguments[1]);
          return;
        case "sorted" or "min" or "max":
          foreach (var argument in arguments)
            this.Expression(argument);

          foreach (var keyword in keywords) {
            if (keyword.Text("arg") == "key" && this.FunctionValue(keyword.Node("value")!))
              this.FunctionArgument(keyword.Node("value")!);
            else if (keyword.Text("arg") is "reverse" or "default" or "key")
              this.Expression(keyword.Node("value")!);
            else
              this.Refuse(keyword, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{name}({keyword.Text("arg")}=…)' is not supported by the Python frontend");
          }

          return;
        case "str" when arguments.Count <= 1 && keywords.Count == 0:
          foreach (var argument in arguments)
            this.Expression(argument);

          return;
        case "isinstance" when arguments.Count == 2 && keywords.Count == 0:
          this.Expression(arguments[0]);
          foreach (var named in arguments[1].Is("Tuple") ? arguments[1].Nodes("elts") : [arguments[1]])
            if (!named.Is("Name"))
              this.Expression(named);

          return;
        case "zip" when arguments.Count != 2:
          this.Refuse(call, PythonFrontend.UnsupportedSequenceDescriptor, "sequence operation 'zip of other than two sequences' is not supported by the Python frontend");
          return;
        case "enumerate" when arguments.Count is 0 or > 2:
          this.Refuse(call, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.Quote(call)}' is not supported");
          return;
        case "str":
          this.Refuse(call, PythonFrontend.UnsupportedStringDescriptor, $"string operation '{this.Quote(call)}' is not supported by the Python frontend");
          return;
      }
    } else if (name is not null && binding == Binding.Unknown) {
      var (descriptor, message) = name switch {
        "getattr" or "setattr" or "hasattr" or "delattr" or "vars" => (PythonFrontend.DynamicAttributeDescriptor,
          $"attribute '{name}' of class '{(arguments.Count > 0 ? this.Quote(arguments[0]) : "")}' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported"),
        "frozenset" => (PythonFrontend.UnsupportedDictDescriptor, $"dict or set operation '{this.Quote(call)}' is not supported by the Python frontend"),
        "repr" or "format" or "hex" or "oct" or "bin" or "ascii" => (PythonFrontend.UnsupportedStringDescriptor, $"string operation '{this.Quote(call)}' is not supported by the Python frontend"),
        "memoryview" or "iter" or "next" => (PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{name}' is not supported by the Python frontend"),
        "super" => (PythonFrontend.UnsupportedClassDescriptor, "'super()' is outside the first Python subset (dataclasses, simple final classes, enums)"),
        _ => (PythonFrontend.UnresolvedDescriptor, $"'{name}' is neither part of this import nor in the Python call map")
      };
      this.Refuse(callee, descriptor, message);
      return;
    } else if (name is null && callee.Is("Attribute")) {
      if (!this.Method(call, callee))
        return;
    } else if (name is null) {
      this.Refuse(callee, PythonFrontend.UnresolvedDescriptor, $"'{this.Quote(callee)}' is neither part of this import nor in the Python call map");
      return;
    } else if (binding is Binding.Local or Binding.Captured or Binding.Constant or Binding.Global) {
      this.Refuse(callee, PythonFrontend.EscapingFunctionDescriptor, Escaping(name) + " (a function value held by a variable: #753)");
      return;
    }

    foreach (var argument in arguments)
      this.Expression(argument);

    foreach (var keyword in keywords)
      this.Expression(keyword.Node("value")!);
  }

  /// <summary>
  /// The format of a call of <c>struct</c>: a constant of explicit byte order and the supported letters,
  /// with as many values as it packs; <see langword="false"/> where it was refused.
  /// </summary>
  private bool Layout(PyNode call, string method) {
    if (call.Nodes("args") is not [var formatNode, ..])
      return true;

    var (format, problem) = StructFormat.Parse(formatNode);
    if (format is null) {
      this.Refuse(formatNode, PythonFrontend.UnsupportedLayoutDescriptor, $"struct format or array typecode '{this.Quote(formatNode)}' is not supported: {problem}");
      return false;
    }

    if (method == "pack" && call.Nodes("args").Count - 1 != format.Fields.Count) {
      this.Refuse(call, PythonFrontend.UnsupportedLayoutDescriptor,
        $"struct format or array typecode '{format.Text}' is not supported: it packs {format.Fields.Count} values, the call gives {call.Nodes("args").Count - 1}, which raises struct.error");
      return false;
    }

    return true;
  }

  /// <summary>The byte order of <c>int.from_bytes</c> or <c>to_bytes</c>: a constant <c>"little"</c> or <c>"big"</c>, by position or by name; <see langword="false"/> where it was refused.</summary>
  private bool ByteOrder(PyNode call, int position) {
    var order = call.Nodes("args").ElementAtOrDefault(position) ?? call.Nodes("keywords").FirstOrDefault(keyword => keyword.Text("arg") == "byteorder")?.Node("value");
    if (order is null || (order.Is("Constant") && order.Text("value") is "little" or "big"))
      return true;

    this.Refuse(order, PythonFrontend.UnsupportedLayoutDescriptor, $"struct format or array typecode '{this.Quote(order)}' is not supported: the byte order is a constant \"little\" or \"big\"");
    return false;
  }

  /// <summary>Checks the callee of a call of a method; <see langword="false"/> where it was refused.</summary>
  private bool Method(PyNode call, PyNode callee) {
    var method = callee.Text("attr")!;
    var owner = callee.Node("value")!;
    if (owner.Is("Name") && this.Resolve(owner) == Binding.Import) {
      var binding = module.Imports[owner.Text("id")!];
      if (binding.User is null && !LibraryCallMap.Has(binding.Module, method)) {
        this.Refuse(callee, PythonFrontend.UnresolvedDescriptor, $"'{this.Quote(callee)}' is neither part of this import nor in the Python call map");
        return false;
      }

      return binding.User is not null || binding.Module != "struct" || this.Layout(call, method);
    }

    if (owner.Is("Name") && this.Resolve(owner) == Binding.Class)
      return true;

    if (owner.Is("Name") && owner.Text("id") == "int" && this.Resolve(owner) == Binding.BuiltIn)
      return method != "from_bytes" || this.ByteOrder(call, 1);

    var declared = module.Classes.Values.Any(type => type.Methods.ContainsKey(method));
    if (method == "to_bytes" && !declared && !this.ByteOrder(call, 1))
      return false;

    if (!declared && RefusedSequenceMethods.Contains(method)) {
      this.Refuse(callee, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{method}' is not supported by the Python frontend");
      return false;
    }

    if (!declared && RefusedStringMethods.Contains(method)) {
      this.Refuse(callee, PythonFrontend.UnsupportedStringDescriptor, $"string operation '{method}' is not supported by the Python frontend");
      return false;
    }

    if (!declared && RefusedDictMethods.Contains(method)) {
      this.Refuse(callee, PythonFrontend.UnsupportedDictDescriptor, $"dict or set operation '{method}' is not supported by the Python frontend");
      return false;
    }

    this.Expression(owner);
    return true;
  }

}
