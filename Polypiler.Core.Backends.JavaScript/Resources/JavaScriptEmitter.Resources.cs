using System.Collections.Generic;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// Resource scopes in JavaScript, at every level: no profile has ECMAScript's <c>using</c>, so a scope
/// is what it means (<see cref="IrUsing.Lowered"/>) -
/// <c>const r = e; try { … } finally { if (r != null) r.Dispose(); }</c>, <c>var</c> at ECMAScript 5 -
/// and several resources are nested, the first acquired outermost. A <c>try</c> with a
/// <c>finally</c> block is ECMAScript 3; the method is named as every method of a class is.
/// </summary>
public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>A resource scope: the resource declared, then the body in a try whose finally block disposes it unless it is <c>null</c>.</summary>
    private void Using(IrUsing scope, List<JsStatement> statements) {
      var initializer = this.Value(scope.Initializer);
      var keyword = this.Native("const") ? "const" : "var";
      statements.Add(new JsVariableDeclaration(keyword, [new JsDeclarator(this.Declare(scope.Resource), initializer)]));
      var body = new JsBlock(this.Statements(scope.Body));
      var held = new JsBinary(this.Variable(scope.Resource), "!=", new JsNull()) { Type = IrType.Bool };
      var dispose = new JsIf(held, new JsExpressionStatement(this.Text(scope.DisposeCall)), null);
      statements.Add(new JsTry(body, null, null, new JsBlock([dispose])));
    }

  }

}
