using System.Collections.Generic;
using System.Linq;
using Acornima;
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

  /// <summary>
  /// The type of a call of a constructor or a method of a base class - nothing, or the method's
  /// result - of a getter of a base class read through <c>super</c>, and of <c>instanceof</c>; <see langword="null"/> for any other node.
  /// </summary>
  private JsType? Inherited(Node node) {
    var current = this._scope!.Candidate.Class;
    if (Inheritance.Of(node, current, this.Names.ClassNamed) is { } reference)
      return reference.IsConstructor ? JsType.Undefined : reference.Member is { } method ? this.ResultOf(this.ModelOf(method)) : JsType.Unknown;

    return node switch {
      MemberExpression { Object: Super } member when current?.Base is { } @base => this.MemberType(@base, member),
      BinaryExpression { Operator: Operator.InstanceOf } test when this.Names.ClassNamed(test.Right) is not null => JsType.Boolean,
      _ => null
    };
  }

  private List<List<Candidate>>? _slots;

  /// <summary>
  /// Gives the methods of each slot of a hierarchy - a method and its overrides - one signature:
  /// the join of their results and of their parameters, since a call through the base class reaches
  /// any of them. Says whether a type changed.
  /// </summary>
  private bool Unify() {
    var changed = false;
    foreach (var slot in this._slots ??= Inheritance.Slots(this.Classes.Values)) {
      var models = slot.Select(this.ModelOf).ToList();
      var result = models.Select(model => model.Result).Aggregate(JsType.Join);
      foreach (var model in models.Where(model => model.Result != result)) {
        model.ResultEvidence ??= models.Select(other => other.ResultEvidence).FirstOrDefault(evidence => evidence is not null);
        model.Result = result;
        this._known.Clear();
        changed = true;
      }

      for (var index = 0; index < models.Min(model => model.Parameters.Count); ++index) {
        var parameters = models.Select(model => model.Parameters[index]).ToList();
        var entry = parameters.Select(parameter => parameter.Entry).Aggregate(JsType.Join);
        foreach (var parameter in parameters.Where(parameter => parameter.Declared is null && parameter.Entry != entry))
          changed |= this.Pass(parameter, entry, parameter.Declaration);
      }
    }

    return changed;
  }

  /// <summary>The type of what a call of a function yields, as its result type in the IR says once it has one.</summary>
  private JsType ResultOf(FunctionModel model)
    => model.Result.Kind is JsKind.Unknown or JsKind.Array or JsKind.Record or JsKind.Conflict or JsKind.Object || model.Result.Ir is null ? model.Result : JsType.Of(model.ReturnType);

}
