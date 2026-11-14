using System.Linq;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>
  /// Lets a value reach a member of an object, which the class of the object decides: a field takes
  /// what is stored or stepped into it, a setter's parameter what is assigned to the property, a
  /// method's parameters what is passed. Says whether a type changed.
  /// </summary>
  private bool ApplyMember(Flow flow) {
    switch (flow.Kind) {
      case FlowKind.FieldStore:
        var store = (AssignmentExpression)flow.Node;
        var target = (MemberExpression)store.Left;
        if (this.Of(target.Object).Class is not { } owner || Binder.KeyOf(target) is not { } key)
          return false;

        var value = Binder.Compound(store.Operator) is null ? this.Of(store.Right) : this.Combined(store);
        if (owner.Field(key) is { } field)
          return this.Reach(field.Variable, value, store.Right);

        return owner.Member(MemberKind.Setter, key) is { } setter && this.ModelOf(setter).Parameters.ElementAtOrDefault(0) is { Declared: null } parameter
          && this.Pass(parameter, value, store.Right);
      case FlowKind.FieldStep:
        var step = (UpdateExpression)flow.Node;
        var stepped = (MemberExpression)step.Argument;
        return this.Of(stepped.Object).Class?.Field(Binder.KeyOf(stepped)!) is { } counter && this.Reach(counter.Variable, Stepped(counter.Variable.Type), step);
      default:
        var call = (CallExpression)flow.Source!;
        var callee = (MemberExpression)call.Callee;
        return this.Of(callee.Object).Class is { } type && Binder.KeyOf(callee) is { } name && type.Member(MemberKind.Method, name) is { } method
          && this.ModelOf(method).Parameters.ElementAtOrDefault(flow.Index) is { Declared: null } argument && this.Pass(argument, this.Of(flow.Node), flow.Node);
    }
  }

  /// <summary>The type of a property read of an object: a field, or the result of a getter; nothing for anything else.</summary>
  private JsType MemberType(ClassModel owner, MemberExpression member) {
    if (Binder.KeyOf(member) is not { } key)
      return JsType.Unknown;

    if (owner.Field(key) is { } field)
      return field.Variable.Type;

    return owner.Member(MemberKind.Getter, key) is { } getter ? this.ResultOf(this.ModelOf(getter)) : JsType.Unknown;
  }

  /// <summary>The type of what a call of a function yields, as its result type in the IR says once it has one.</summary>
  private JsType ResultOf(FunctionModel model)
    => model.Result.Kind is JsKind.Unknown or JsKind.Array or JsKind.Record or JsKind.Conflict or JsKind.Object || model.Result.Ir is null ? model.Result : JsType.Of(model.ReturnType).AbsentAs(model.Result);

}
