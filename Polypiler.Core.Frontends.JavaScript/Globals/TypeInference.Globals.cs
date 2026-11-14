using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  private IReadOnlyDictionary<string, Variable> _globals = new Dictionary<string, Variable>();

  /// <summary>
  /// The initializers of the variables of the module, as the body of a function of their own: what
  /// its flows let reach a variable is joined with what the functions let reach it, so that a variable
  /// of the module is typed like a local, from every value that reaches it anywhere.
  /// </summary>
  public FunctionModel Initializers { get; private set; } = null!;

  /// <summary>Takes the variables of the module and binds their initializers; call it before <see cref="Solve"/>.</summary>
  /// <param name="globals">The variables, in source order.</param>
  /// <param name="root">The program, which a diagnostic about the module as a whole points at.</param>
  public void DeclareGlobals(IReadOnlyList<Variable> globals, Node root) {
    this._globals = globals.ToDictionary(global => global.Name);
    var model = new FunctionModel(new Candidate("", root, null!));
    var binder = new Binder(model, candidates, constants, source);
    this._binders[model] = binder;
    binder.BindInitializers(globals);
    this.Initializers = model;
  }

  /// <summary>Whether a name is that of a variable of the module.</summary>
  public bool IsGlobal(string name) => this._globals.ContainsKey(name);

  /// <summary>The binder of a function, which sees the variables of the module wherever no local hides them.</summary>
  private Binder BinderOf(FunctionModel model) => new(model, candidates, constants, source) { Globals = this._globals };

  /// <summary>The functions whose flows the fixed point runs over: those imported and the initializers.</summary>
  private IEnumerable<FunctionModel> Flowing(List<FunctionModel> models) => models.Append(this.Initializers);

}
