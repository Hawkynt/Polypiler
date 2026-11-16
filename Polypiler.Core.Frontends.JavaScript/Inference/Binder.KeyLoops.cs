using System.Collections.Generic;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class Binder {

  /// <summary>
  /// A loop over the keys of an object: the object is read, and the body is bound where the loop
  /// stands, with the key a constant string of its own scope - not as a function of its own.
  /// </summary>
  private bool KeyLoop(Statement statement) {
    if (Polypiler.Core.Frontends.JavaScript.KeyLoop.Of(statement) is not { } loop)
      return false;

    this.Expression(loop.Object);
    var outer = this._declared;
    this._declared = [];
    if (this.Declare(loop.Key, true, false) is { } key) {
      key.KeysOf = loop.Object;
      key.Type = JsType.String;
    }

    if (loop.Body is Statement body)
      this.Body(body);
    else
      this.Expression(loop.Body);

    this.Leave(outer);
    return true;
  }

  /// <summary>Whether a node is a variable of the keys of an object, as a key loop declares one.</summary>
  private bool IsKey(Node node) => node is Identifier { Name: var name } && this._scope.TryGetValue(name, out var variable) && variable.KeysOf is not null;

}
