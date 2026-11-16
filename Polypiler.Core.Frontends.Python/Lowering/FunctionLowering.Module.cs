using System.Collections.Generic;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>What the module's own statements and declarations ask of the lowering: their values, and the equality of fields.</summary>
internal sealed partial class FunctionLowering {

  /// <summary>A module-level expression: the statements its temporaries need, its value, its type and the locals they use.</summary>
  public (List<IrStmt> Prefix, IrExpr Value, PyType Type, List<IrLocal> Locals) ModuleValue(PyNode expression, PyType? expected) {
    this._prefix = [];
    this._locals = [];
    var value = this.Value(expression, expected);
    return (this._prefix, value.Value, value.Type, this._locals);
  }

  /// <summary>A module-level <c>x op= v</c>: the value of <c>x op v</c>.</summary>
  public (List<IrStmt> Prefix, IrExpr Value, PyType Type, List<IrLocal> Locals) ModuleAugmented(PyNode statement) {
    this._prefix = [];
    this._locals = [];
    var current = this.Value(statement.Node("target")!);
    var value = this.Operate(statement, statement.Operator()!, current, this.Value(statement.Node("value")!));
    return (this._prefix, value.Value, value.Type, this._locals);
  }

  /// <summary>Python's <c>==</c> of two fields of one type, as a dataclass's <c>__eq__</c> compares them.</summary>
  public IrExpr FieldEquality(IrExpr left, IrExpr right, PyType type) => this.ElementEquality(this._module.Node, left, right, type);

}
