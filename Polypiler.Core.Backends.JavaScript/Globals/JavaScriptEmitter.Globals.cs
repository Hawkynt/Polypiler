using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The name each global of the module is written with.</summary>
    private readonly Dictionary<IrGlobal, string> _globals = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Gives every global of the module its name: the source name, unless a parameter or a local of a
    /// function has it, which would hide the global inside that function. Such a global gets
    /// <c>$g</c> appended, which no name of a source and no other name the emitter makes has.
    /// </summary>
    private void NameGlobals(IrModule module) {
      var variables = module.Functions.SelectMany(function => function.Parameters.Concat(function.Locals)).Select(variable => this.Name(variable.Name)).ToHashSet();
      foreach (var global in module.Globals) {
        var name = this.Name(global.Name);
        this._globals[global] = variables.Contains(name) ? name + "$g" : name;
      }
    }

    /// <summary>
    /// The globals of the module, declared after its constants and before its functions, in the
    /// module itself, in the body of a CommonJS module or inside the function of a script, and so
    /// private to the module in every format: <c>let</c> where the level has it, <c>var</c> where it
    /// has not, and <c>const</c> for an array, which is never replaced. A global without an
    /// initializer starts as the zero of its type, as it does in the IR. A 64-bit integer is a
    /// <c>BigInt</c>, or the object of its halves where the level has no <c>BigInt</c>, as everywhere.
    /// </summary>
    private List<JsStatement> Globals(IrModule module) {
      var declarations = new List<JsStatement>();
      foreach (var global in module.Globals) {
        var name = this._globals[global];
        this._construct = $"the global '{global.Name}'";
        this.Describe(global.Type);
        var keyword = global.Type.IsArray && this.Native("const") ? "const" : this.Let;
        var value = global.Initializer is { } initializer ? this.Value(initializer) : this.Constant(global.Type, 0).Node;
        declarations.Add(new JsVariableDeclaration(keyword, [new JsDeclarator(name, value)]) {
          Leading = declarations.Count == 0 ? JsTrivia.Blank : JsTrivia.None
        });
      }

      return declarations;
    }

    /// <summary>A global of the module as an expression of its type.</summary>
    private JsIdentifier GlobalName(IrGlobal global) => new(this._globals[global]) { Type = global.Type };

    /// <summary>An assignment to a global of the module.</summary>
    private JsAssignment GlobalAssignment(IrGlobalAssign assign) {
      var target = this.GlobalName(assign.Global);
      return new JsAssignment(target, "=", this.Value(assign.Value));
    }

  }

}
