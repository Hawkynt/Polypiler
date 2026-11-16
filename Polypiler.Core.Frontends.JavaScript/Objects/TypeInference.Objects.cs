using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>
  /// Lets a value reach a member of an object, which the class of the object decides: a field takes
  /// what is stored or stepped into it, a setter's parameter what is assigned to the property, a
  /// method's parameters what is passed. A view stored into a field escapes, and so does the view in the
  /// variable it is read or cut from: both are memories. Says whether a type changed.
  /// </summary>
  private bool ApplyMember(FunctionModel model, Flow flow) {
    switch (flow.Kind) {
      case FlowKind.FieldStore:
        var store = (AssignmentExpression)flow.Node;
        var target = (MemberExpression)store.Left;
        if (this.Of(target.Object).Class is not { } owner || Binder.KeyOf(target) is not { } key)
          return false;

        var value = Binder.Compound(store.Operator) is null ? this.Of(store.Right) : this.Combined(store);
        if (owner.Field(key) is { } field) {
          value = Fresh(field.Variable.Type, store.Right, value);

          var origin = value.IsView && Origin(store.Right) is Identifier read && model.Bindings.GetValueOrDefault(read) is Variable { Type.IsView: true } source
            && this.Reach(source, value.Stored(), store.Right);
          return this.Reach(field.Variable, value.Stored(), store.Right) | origin | this.Fit(model, store.Right, field.Variable.Type);
        }

        return owner.Member(MemberKind.Setter, key) is { } setter && this.ModelOf(setter).Parameters.ElementAtOrDefault(0) is { } parameter
          && (parameter.Declared is null ? this.Pass(parameter, value, store.Right) : parameter.Declared.IsStated && this.Shape(parameter, value, store.Right));
      case FlowKind.FieldStep:
        var step = (UpdateExpression)flow.Node;
        var stepped = (MemberExpression)step.Argument;
        return this.Of(stepped.Object).Class?.Field(Binder.KeyOf(stepped)!) is { } counter && this.Reach(counter.Variable, Stepped(counter.Variable.Type), step);
      default:
        var call = (CallExpression)flow.Source!;
        var callee = (MemberExpression)call.Callee;
        if (this.CallbackCall(call) is not null)
          return this.Callback(call, flow.Index);

        return this.Of(callee.Object).Class is { } type && Binder.KeyOf(callee) is { } name && type.Member(MemberKind.Method, name) is { } method
          && this.ModelOf(method).Parameters.ElementAtOrDefault(flow.Index) is { } argument
          && (argument.Declared is null ? this.Pass(argument, this.Of(flow.Node), flow.Node) : argument.Declared.IsStated && this.Shape(argument, this.Of(flow.Node), flow.Node));
    }
  }

  /// <summary>
  /// A new Array a store makes, which nothing else holds, as one of the integers the place states where
  /// its own fit them (#1413, #1416); one the program grows joins the place as a list all the same. Any
  /// other value as it is.
  /// </summary>
  private static JsType Fresh(JsType? place, Node made, JsType value)
    => IsFresh(made) && value is { Kind: JsKind.Array, IsPlain: true, IsView: false, Element: { Kind: JsKind.Number } given } && (given.IsInteger || given.IsWhole)
      && place is { Kind: JsKind.Array, IsView: false, Element: { AdmitsWhole: true } element } && JsType.Join(element, given) == element
      ? JsType.ArrayOf(element, true) : value;

  /// <summary>Whether an expression makes a new Array: a literal, <c>new Array(…)</c>, or the <c>fill</c> of one.</summary>
  private static bool IsFresh(Node made) => made switch {
    ArrayExpression => true,
    NewExpression { Callee: Identifier { Name: "Array" } } => true,
    CallExpression { Callee: MemberExpression { Computed: false, Object: var filled, Property: Identifier { Name: "fill" } } } => IsFresh(filled),
    _ => false
  };

  /// <summary>What a view is made of: the receiver of <c>subarray</c>, as often as it is called, and the expression itself otherwise.</summary>
  private static Expression Origin(Expression view)
    => view is CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: "subarray" } } part } ? Origin(part.Object) : view;

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
    var changed = this.UnifyShapes();
    foreach (var slot in this._slots ??= Inheritance.Slots(this.Classes.Values)) {
      var models = slot.Select(this.ModelOf).ToList();
      // A method that states no result returns what the one it overrides, or one that overrides it, states (#1413).
      if (models.Select(model => model.StatedResult).FirstOrDefault(stated => stated is not null) is { } statedResult)
        foreach (var model in models.Where(model => model.StatedResult is null)) {
          model.StatedResult = statedResult;
          changed = true;
        }

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
        foreach (var parameter in parameters.Where(parameter => parameter.Entry != entry))
          // A stated T[] takes the kind of array the overrides are passed, its elements staying the stated ones (#1374).
          changed |= parameter.Declared is null ? this.Pass(parameter, entry, parameter.Declaration)
            : parameter.Declared.IsStated && this.Shape(parameter, entry, parameter.Declaration);
      }
    }

    return changed | this.UnifyFields();
  }

  /// <summary>
  /// Gives a field that derived classes declare accessors over one type with them (#1402): the field
  /// is the base implementation of a property whose getters return and whose setters take what any
  /// read or store of the key reaches. Says whether a type changed.
  /// </summary>
  private bool UnifyFields() {
    var changed = false;
    foreach (var field in this.Classes.Values.Distinct().SelectMany(owner => owner.Fields).Where(field => field.IsVirtual)) {
      var getters = field.Accessors.Where(member => member.Member == MemberKind.Getter).Select(this.ModelOf).ToList();
      var setters = field.Accessors.Where(member => member.Member == MemberKind.Setter).Select(member => this.ModelOf(member).Parameters.ElementAtOrDefault(0)).OfType<Variable>().ToList();
      var type = getters.Select(model => model.Result).Concat(setters.Select(parameter => parameter.Entry)).Aggregate(field.Variable.Type, JsType.Join);
      changed |= this.Reach(field.Variable, type, field.Variable.Declaration);
      foreach (var model in getters.Where(model => model.Result != type)) {
        model.Result = type;
        this._known.Clear();
        changed = true;
      }

      foreach (var parameter in setters.Where(parameter => parameter.Entry != type))
        changed |= parameter.Declared is null ? this.Pass(parameter, type, parameter.Declaration) : parameter.Declared.IsStated && this.Shape(parameter, type, parameter.Declaration);
    }

    return changed;
  }

  /// <summary>The type of what a call of a function yields, as its result type in the IR says once it has one.</summary>
  private JsType ResultOf(FunctionModel model)
    // A whole double stays one: its range is what the IR type of a double does not say (#1413).
    => model.Result.IsWhole && model.ReturnType == Polypiler.Core.IR.IrType.F64 ? model.Result
    : model.Result.Kind is JsKind.Unknown or JsKind.Array or JsKind.Record or JsKind.Conflict or JsKind.Object or JsKind.Function or JsKind.Sequence || model.Result.Ir is null ? model.Result : JsType.Of(model.ReturnType).AbsentAs(model.Result);

}
