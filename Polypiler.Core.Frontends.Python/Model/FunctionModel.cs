using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>A parameter or a local of a function, with the type the inference gave it.</summary>
/// <param name="Name">The name.</param>
/// <param name="Node">Where it is first bound: the parameter, or the first assignment.</param>
internal sealed record Variable(string Name, PyNode Node) {

  /// <summary>The type: of the hint for a parameter that has one, otherwise of the values that reach it.</summary>
  public PyType Type { get; set; }

  /// <summary>The type hint of a parameter, or <see langword="null"/>.</summary>
  public PyNode? Hint { get; init; }

  /// <summary>Whether values of a narrower number type reach it and are converted when they are stored.</summary>
  public bool Widened { get; set; }

}

/// <summary>A module-level function: its parameters and locals and what they are.</summary>
internal sealed class FunctionModel {

  /// <summary>Describes a <c>def</c>.</summary>
  /// <param name="node">The <c>FunctionDef</c>.</param>
  public FunctionModel(PyNode node) {
    this.Node = node;
    this.Name = node.Text("name")!;
    this.Parameters = [.. node.Node("args")!.Nodes("args").Select(argument => new Variable(argument.Text("arg")!, argument) { Hint = argument.Node("annotation") })];
  }

  /// <summary>The <c>FunctionDef</c>.</summary>
  public PyNode Node { get; }

  /// <summary>The name.</summary>
  public string Name { get; }

  /// <summary>Whether the function belongs to the public surface of the module: its name does not start with an underscore.</summary>
  public bool IsPublic => !this.Name.StartsWith('_');

  /// <summary>The parameters, in order.</summary>
  public IReadOnlyList<Variable> Parameters { get; }

  /// <summary>The locals beside the parameters, in the order they are first bound.</summary>
  public Dictionary<string, Variable> Locals { get; } = [];

  /// <summary>The statements of the body.</summary>
  public IReadOnlyList<PyNode> Body => this.Node.Nodes("body");

  /// <summary>The return type hint, or <see langword="null"/>.</summary>
  public PyNode? ReturnHint => this.Node.Node("returns");

  /// <summary>What the function returns: the join of the values its <c>return</c> statements give.</summary>
  public PyType Returns { get; set; }

  /// <summary>Whether a value of a narrower number type is returned and converted.</summary>
  public bool ReturnWidened { get; set; }

  /// <summary>Whether control can reach the end of the body, where the function returns <c>None</c>.</summary>
  public bool EndReachable { get; set; }

  /// <summary>A parameter or local by its name, or <see langword="null"/>.</summary>
  public Variable? Find(string name)
    => this.Parameters.FirstOrDefault(parameter => parameter.Name == name) ?? this.Locals.GetValueOrDefault(name);

  /// <summary>Whether a name is bound in the function: a parameter or a local.</summary>
  public bool Binds(string name) => this.Find(name) is not null;

}
