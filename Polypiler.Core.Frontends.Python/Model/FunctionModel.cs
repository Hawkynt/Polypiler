using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>How a parameter takes its argument.</summary>
internal enum ParameterKind {

  /// <summary>By position or by name.</summary>
  Normal,

  /// <summary>By position only: before <c>/</c>.</summary>
  PositionalOnly,

  /// <summary>By name only: after <c>*</c>.</summary>
  KeywordOnly

}

/// <summary>A parameter or a local of a function, with the type the inference gave it; it is itself, compared by identity.</summary>
/// <param name="name">The name.</param>
/// <param name="node">Where it is first bound: the parameter, or the first assignment.</param>
internal sealed class Variable(string name, PyNode node) {

  /// <summary>The name.</summary>
  public string Name { get; } = name;

  /// <summary>Where it is first bound: the parameter, or the first assignment.</summary>
  public PyNode Node { get; } = node;

  /// <summary>The type: of the hint for a parameter that has one, otherwise of the values that reach it.</summary>
  public PyType Type { get; set; } = PyType.Unknown;

  /// <summary>The type hint of a parameter, or <see langword="null"/>.</summary>
  public PyNode? Hint { get; init; }

  /// <summary>Whether values of a narrower number type reach it and are converted when they are stored.</summary>
  public bool Widened { get; set; }

  /// <summary>How a parameter takes its argument.</summary>
  public ParameterKind Kind { get; init; }

  /// <summary>The default value of a parameter, or <see langword="null"/>.</summary>
  public PyNode? Default { get; init; }

  /// <summary>The value of the default, evaluated once where the <c>def</c> runs; set by the module's initialisation.</summary>
  public ConstantValue? DefaultValue { get; set; }

}

/// <summary>A function: a module-level <c>def</c>, a method of a class or a function nested in another.</summary>
internal sealed class FunctionModel {

  /// <summary>Describes a <c>def</c>.</summary>
  /// <param name="node">The <c>FunctionDef</c>.</param>
  /// <param name="owner">The class of a method, or <see langword="null"/>.</param>
  /// <param name="parent">The function a nested function stands in, or <see langword="null"/>.</param>
  public FunctionModel(PyNode node, ClassModel? owner = null, FunctionModel? parent = null) {
    this.Node = node;
    this.Name = node.Text("name")!;
    this.Owner = owner;
    this.Parent = parent;
    var arguments = node.Node("args")!;
    var positional = arguments.Nodes("posonlyargs").Select(argument => (argument, ParameterKind.PositionalOnly))
      .Concat(arguments.Nodes("args").Select(argument => (argument, ParameterKind.Normal))).ToList();
    var defaults = arguments.Nodes("defaults");
    var parameters = new List<Variable>();
    for (var index = 0; index < positional.Count; ++index) {
      var (argument, kind) = positional[index];
      var at = index - (positional.Count - defaults.Count);
      parameters.Add(new Variable(argument.Text("arg")!, argument) { Hint = argument.Node("annotation"), Kind = kind, Default = at >= 0 ? defaults[at] : null });
    }

    var keywordOnly = arguments.Nodes("kwonlyargs");
    for (var index = 0; index < keywordOnly.Count; ++index) {
      // A keyword-only parameter without a default has None in kw_defaults, which the dump leaves out of the list of nodes.
      var hasDefault = KeywordDefault(arguments, index) is not null;
      parameters.Add(new Variable(keywordOnly[index].Text("arg")!, keywordOnly[index]) {
        Hint = keywordOnly[index].Node("annotation"), Kind = ParameterKind.KeywordOnly, Default = hasDefault ? KeywordDefault(arguments, index) : null
      });
    }

    this.Parameters = parameters;
  }

  /// <summary>The default of the keyword-only parameter at an index, or <see langword="null"/>.</summary>
  private static PyNode? KeywordDefault(PyNode arguments, int index) => arguments.Item("kw_defaults", index);

  /// <summary>The <c>FunctionDef</c>, or the <c>Lambda</c>.</summary>
  public PyNode Node { get; }

  /// <summary>The name.</summary>
  public string Name { get; }

  /// <summary>The name of the function in the IR: unique in the module.</summary>
  public string IrName { get; set; } = "";

  /// <summary>The class of a method, or <see langword="null"/>.</summary>
  public ClassModel? Owner { get; }

  /// <summary>The function a nested function stands in, or <see langword="null"/>.</summary>
  public FunctionModel? Parent { get; }

  /// <summary>The functions nested in this one, by name.</summary>
  public Dictionary<string, FunctionModel> Nested { get; } = [];

  /// <summary>Whether a method is a <c>@staticmethod</c>: it has no receiver.</summary>
  public bool IsStatic { get; init; }

  /// <summary>Whether a method is a <c>@property</c> getter, called where the attribute is read.</summary>
  public bool IsProperty { get; init; }

  /// <summary>Whether the body holds a <c>yield</c>: a call makes a generator without running the body.</summary>
  public bool IsGenerator { get; set; }

  /// <summary>The names the function declares <c>nonlocal</c>: variables of the functions it stands in.</summary>
  public HashSet<string> Nonlocals { get; } = [];

  /// <summary>The names the function declares <c>global</c>: variables of the module.</summary>
  public HashSet<string> Globals { get; } = [];

  /// <summary>Whether the function belongs to the public surface of the module: its name does not start with an underscore.</summary>
  public bool IsPublic => this.Owner is null && this.Parent is null && !this.Name.StartsWith('_');

  /// <summary>The parameters, in order; for a method the receiver first.</summary>
  public IReadOnlyList<Variable> Parameters { get; }

  /// <summary>The parameters an argument binds: for a method other than a static one all but the receiver.</summary>
  public IEnumerable<Variable> Arguments => this.Owner is not null && !this.IsStatic ? this.Parameters.Skip(1) : this.Parameters;

  /// <summary>The locals beside the parameters, in the order they are first bound.</summary>
  public Dictionary<string, Variable> Locals { get; } = [];

  /// <summary>The statements of the body.</summary>
  public IReadOnlyList<PyNode> Body => this.Node.Nodes("body");

  /// <summary>The return type hint, or <see langword="null"/>.</summary>
  public PyNode? ReturnHint => this.Node.Node("returns");

  /// <summary>What the function returns: the join of the values its <c>return</c> statements give; for a generator the values it yields.</summary>
  public PyType Returns { get; set; } = PyType.Unknown;

  /// <summary>Whether a value of a narrower number type is returned and converted.</summary>
  public bool ReturnWidened { get; set; }

  /// <summary>Whether control can reach the end of the body, where the function returns <c>None</c>.</summary>
  public bool EndReachable { get; set; }

  /// <summary>The name of the function as diagnostics write it: <c>C.m</c> for a method, <c>f.g</c> for a nested function.</summary>
  public string Qualified => this.Owner is not null ? $"{this.Owner.Name}.{this.Name}" : this.Parent is not null ? $"{this.Parent.Qualified}.{this.Name}" : this.Name;

  /// <summary>A parameter or local of this function by its name, or <see langword="null"/>.</summary>
  public Variable? Own(string name)
    => this.Parameters.FirstOrDefault(parameter => parameter.Name == name) ?? this.Locals.GetValueOrDefault(name);

  /// <summary>
  /// A variable the function reads by a name: its own, or one of a function it stands in that it does
  /// not bind itself (a captured one, or one it declares <c>nonlocal</c>).
  /// </summary>
  public Variable? Find(string name) {
    if (!this.Nonlocals.Contains(name) && this.Own(name) is { } own)
      return own;

    return this.Globals.Contains(name) ? null : this.Parent?.Find(name);
  }

  /// <summary>Whether a name is bound in the function or in one it stands in.</summary>
  public bool Binds(string name) => this.Find(name) is not null;

  /// <summary>Whether a name is bound by this function itself, as a parameter or a local.</summary>
  public bool BindsOwn(string name) => !this.Nonlocals.Contains(name) && !this.Globals.Contains(name) && this.Own(name) is not null;

  /// <inheritdoc/>
  public override string ToString() => this.Qualified;

}
