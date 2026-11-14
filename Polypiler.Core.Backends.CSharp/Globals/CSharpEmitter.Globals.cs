using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// A global of the module as a private static field of the class, in the same form in every
    /// profile: initialised in its declaration, so that the class has no static constructor and the
    /// runtime initialises it as it initialises a constant table (<c>beforefieldinit</c>). The
    /// initializer is static, so the order cannot be observed. An array global is <c>readonly</c>:
    /// its elements change, the array does not. A global without an initializer is given its zero
    /// all the same: the compiler warns about a field nothing assigns (CS0649).
    /// </summary>
    private CsField Global(IrGlobal global) {
      var name = this._members[global.Name];
      IReadOnlyList<string> modifiers = global.Type.IsArray ? ["private", "static", "readonly"] : ["private", "static"];
      var initializer = global.Initializer ?? new IrConst(global.Type, 0);
      var value = this.Stored(initializer);
      // As in an expression-bodied method, whatever wraps around stands in an unchecked context of its own.
      if (Expressions(initializer).Any(this.Wraps) && !Traps(Promoted(initializer)))
        value = new CsCheckedExpression(false, value) { Type = value.Type };

      return new CsField(modifiers, new CsType(this.TypeName(global.Type)), name, value);
    }

    /// <summary>A global of the module as an expression of its type.</summary>
    private CsName GlobalName(IrGlobal global) => new(this._members[global.Name]) { Type = global.Type };

  }

}
