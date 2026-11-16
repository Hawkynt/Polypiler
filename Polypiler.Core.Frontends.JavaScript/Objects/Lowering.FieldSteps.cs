using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// <c>++</c> and <c>--</c> of a field used as a value (#1407), as a stream cipher reads its keystream
/// with <c>this.buffer[this.position++]</c>: the receiver is evaluated once, the field read, stepped in
/// double arithmetic as every <c>++</c> of JavaScript - the step makes the field a double, as
/// <c>+= 1</c> does - and stored, and the old value (postfix) or the new one (prefix) yielded
/// (ECMA-262 §13.4.2.1, §13.4.4.1). The IR has no store that yields a value, so the step is a function of
/// the module per field and form that takes the object. A static field is a variable of the module,
/// which the step of the IR takes as it is.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The functions that step a field and yield a value, by the field, the direction and the form.</summary>
  private readonly Dictionary<(IrField Field, bool Increment, bool Postfix), string> _fieldSteps = [];

  /// <summary>A step of a field of an object or of a static field as a value; <see langword="null"/> for an element or a member of no object.</summary>
  private IrExpr? FieldStepValue(UpdateExpression step, MemberExpression member) {
    var increment = step.Operator == Operator.Increment;
    if (this._model.Bindings.GetValueOrDefault(member) is Variable global)
      return new IrStep(Read(global), increment, !step.Prefix, false);

    // A computed key of an object is no field the subset names; the step of an element refuses it.
    if (types.Of(member.Object).Class is not { } owner || Binder.KeyOf(member) is not { } key)
      return null;

    var text = increment ? "++" : "--";
    if (owner.Field(key) is not { } field)
      throw this.Unsupported(step, owner.Member(MemberKind.Getter, key) is not null || owner.Member(MemberKind.Setter, key) is not null
        ? $"a step of the accessor {key}"
        : $"{text} of {types.Text(member)}, which is no field");

    var ir = field.Ir!;
    if (field.IsPrivate)
      throw this.Unsupported(step, $"{text} of the private field {key} as a value");

    // A field that is stepped holds a double (TypeInference.Stepped), or an optional one where null reaches it as well.
    if (ir.Type != IrType.F64)
      throw this.Unsupported(step, $"{text} of the field {key} of the type {ir.Type}");

    // The function takes an object of the class that declares the field, which every class derived from it is.
    var declarer = owner.Lineage.First(type => type.OwnField(key) is not null);
    return new IrCall(this.FieldStepper(IrType.Object(declarer.Ir), ir, increment, !step.Prefix), IrType.F64, [this.Expression(member.Object)]);
  }

  /// <summary>
  /// The function of the module that steps a field of the object it is given and yields the value
  /// from before the step or after it: <c>v = o.f; o.f = v ± 1; return v</c> or <c>v ± 1</c>.
  /// </summary>
  private string FieldStepper(IrType receiver, IrField field, bool increment, bool postfix) {
    if (this._fieldSteps.TryGetValue((field, increment, postfix), out var known))
      return known;

    var name = $"$step{this._fieldSteps.Count}";
    this._fieldSteps[(field, increment, postfix)] = name;
    var (target, before, after) = (new IrLocal("o", receiver), new IrLocal("v", IrType.F64), new IrLocal("w", IrType.F64));
    var one = new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1));
    this.Callbacks.Add(new IrFunction(name, false, IrType.F64, [target], [before, after], new IrBlock([
      new IrDeclare(before, new IrFieldLoad(new IrLocalRef(target), field)),
      new IrDeclare(after, new IrBinary(increment ? IrBinaryOp.Add : IrBinaryOp.Subtract, new IrLocalRef(before), one)),
      new IrFieldStore(new IrLocalRef(target), field, new IrLocalRef(after)),
      new IrReturn(new IrLocalRef(postfix ? before : after))
    ])));
    return name;
  }

}
