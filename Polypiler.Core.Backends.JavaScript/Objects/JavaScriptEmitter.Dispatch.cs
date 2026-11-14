using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The functions of the module that are the signatures of the methods of its interfaces, which nothing writes.</summary>
    private readonly HashSet<string> _signatures = [];

    /// <summary>The class being written, whose constructor calls those of its base class with <c>super</c>.</summary>
    private IrClassType? _class;

    /// <summary>The classes of the module that derive from a class or that a class derives from.</summary>
    private HashSet<IrClassType>? _hierarchy;

    /// <summary>Whether a class has a base class or a class derived from it in the module.</summary>
    private bool InHierarchy(IrClassType type)
      => (this._hierarchy ??= [.. this._owners.Values.Where(owner => owner.Base is not null).SelectMany(owner => new[] { owner, owner.Base! })]).Contains(type);

    /// <summary>The property that marks the objects of the classes that implement an interface.</summary>
    private static string Marker(IrInterfaceType type) => "$is_" + type.Name;

    /// <summary>
    /// The interfaces of a class, each a property of its prototype that is no enumerable property of
    /// any object and that no name of the source has (<c>$</c> is none of the frontends' names):
    /// <c>Object.defineProperty(C.prototype, "$is_I", { value: true })</c>.
    /// </summary>
    private IEnumerable<JsStatement> Markers(IrClassType type, JsExpression prototype) {
      foreach (var contract in type.AllInterfaces) {
        this.Native("Object.defineProperty");
        yield return new JsExpressionStatement(Invoke("Object.defineProperty", prototype, new JsString(Marker(contract)),
          new JsObject([new JsProperty("value", new JsBoolean(true))])));
      }
    }

    /// <summary>
    /// A call of the method of a base class, whatever overrides it: <c>super.m(…)</c>, or
    /// <c>Base.prototype.m.call(this, …)</c> where the level has no classes. Accessors are never
    /// virtual, and their base calls are written as calls on <c>this</c>.
    /// </summary>
    private JsExpression BaseCall(IrMethodCall call, IrClassType owner) {
      var name = this.MemberName(call.Method, owner);
      var arguments = call.Arguments.Select(this.Value).ToList();
      if (this.Native("super"))
        return new JsCall(new JsMember(Id("super"), name), arguments);

      this.Native(".call");
      return new JsCall(new JsMember(new JsMember(new JsMember(Id(this.Name(owner.Name)), "prototype"), name), "call"), [new JsThis(), .. arguments]);
    }

    /// <summary>Whether a reference is an object of a class or an interface: <c>x instanceof C</c>, or the marker of the interface on it.</summary>
    private JsExpression IsOf(JsExpression value, IrType type) {
      if (type.ClassType is { } owner) {
        this.Native("instanceof");
        return Infix(value, "instanceof", Id(this.Name(owner.Name)));
      }

      return Infix(Infix(value, "!=", new JsNull()), "&&", Infix(new JsMember(value, Marker(type.InterfaceType!)), "===", new JsBoolean(true)));
    }

    /// <summary>
    /// A type test, inline where its operand is read once - a test of a class, or of a variable - and
    /// through the helper <c>$is_&lt;I&gt;</c> otherwise; a cast through <c>$cast_&lt;T&gt;</c>, which
    /// throws a <c>TypeError</c> where the IR traps; an <c>as</c> as <c>x instanceof C ? x : null</c>
    /// for a variable, through <c>$as_&lt;T&gt;</c> otherwise.
    /// </summary>
    private (JsExpression Node, bool Atomic) Conversion(IrExpr expression) {
      switch (expression) {
        case IrTypeTest test:
          if (test.Target.ClassType is not null || test.Value is IrLocalRef)
            return (this.IsOf(this.Operand(test.Value), test.Target), false);

          return (Invoke(this.StructHelper("is", test.Target.Declaration!), this.Value(test.Value)), true);
        case IrCast cast:
          return (Invoke(this.StructHelper("cast", cast.Type.Declaration!), this.Value(cast.Value)), true);
        default:
          var converted = (IrAsCast)expression;
          if (converted.Value is not IrLocalRef)
            return (Invoke(this.StructHelper("as", converted.Type.Declaration!), this.Value(converted.Value)), true);

          var value = this.Operand(converted.Value);
          return (new JsConditional(this.IsOf(value, converted.Type), value, new JsNull()), false);
      }
    }

    /// <summary>The helper of a type test, a cast or an <c>as</c> of a class or an interface.</summary>
    private JsFunctionDeclaration ConversionHelper(string name, string kind, IrTypeDeclaration declared) {
      var type = declared is IrClassType owner ? IrType.Object(owner) : IrType.Object((IrInterfaceType)declared);
      var value = Id("value");
      JsStatement body = kind switch {
        "is" => new JsReturn(this.IsOf(value, type)),
        "as" => new JsReturn(new JsConditional(this.IsOf(value, type), value, new JsNull())),
        _ => new JsIf(Infix(Infix(value, "===", new JsNull()), "||", this.IsOf(value, type)), new JsReturn(value), null)
      };
      if (kind == "cast")
        this.Native("TypeError");

      List<JsStatement> statements = kind == "cast"
        ? [body, new JsThrow(new JsNew(Id("TypeError"), [new JsString(IrTrapException.Describe(IrTrap.InvalidCast))]))]
        : [body];
      return new JsFunctionDeclaration(name, ["value"], new JsBlock(statements));
    }

  }

}
