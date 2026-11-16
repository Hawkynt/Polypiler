using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// The macros of the subset: <c>vec!</c>, the assertions and the panicking macros. An assertion
/// computes its operands, left first, then panics when it does not hold; a <c>debug_assert*!</c> is
/// the same with debug assertions on and nothing at all - not even its operands - with them off.
/// </summary>
internal sealed partial class FunctionLowering {

  private IrExpr? Macro(MacroExpr macro) {
    switch (macro.Name) {
      case "vec":
        return this.VecMacro(macro);
      case "format":
        return this.Format(macro);
      case "assert" or "debug_assert": {
        if (macro.Name.StartsWith("debug_", System.StringComparison.Ordinal) && !this._typed.DebugAssertions)
          return null;

        var condition = this.Value(macro.Arguments[0]);
        this.Emit(new IrIf(Not(condition), new IrBlock([RustTraps.PanicStatement(Message(macro, 1, "assertion failed"))]), null));
        return null;
      }
      case "assert_eq" or "assert_ne" or "debug_assert_eq" or "debug_assert_ne": {
        if (macro.Name.StartsWith("debug_", System.StringComparison.Ordinal) && !this._typed.DebugAssertions)
          return null;

        var type = this._typed.TypeOf(macro.Arguments[0]).Dereferenced;
        var values = this.Operands(macro.Arguments[0], macro.Arguments[1]);
        var equal = type.IsScalar ? Equal(values[0], values[1]) : this.Equality(true, values[0], values[1], type);
        var fails = macro.Name.EndsWith("_eq", System.StringComparison.Ordinal) ? Not(equal) : equal;
        this.Emit(new IrIf(fails, new IrBlock([RustTraps.PanicStatement(Message(macro, 2, "assertion `left " + (macro.Name.EndsWith("_eq", System.StringComparison.Ordinal) ? "==" : "!=") + " right` failed"))]), null));
        return null;
      }
      default: {
        var message = macro.Name switch {
          "unreachable" => "internal error: entered unreachable code",
          "todo" => "not yet implemented",
          "unimplemented" => "not implemented",
          _ => "explicit panic"
        };
        this.Emit(RustTraps.PanicStatement(Message(macro, 0, message)));
        this._diverged = true;
        return null;
      }
    }
  }

  /// <summary>The message of a panicking macro: its literal after the operands, or Rust's own text.</summary>
  private static string Message(MacroExpr macro, int first, string fallback)
    => macro.Arguments.Skip(first).FirstOrDefault() is StringLiteral literal ? literal.Value : fallback;

}
