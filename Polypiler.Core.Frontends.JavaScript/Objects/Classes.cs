using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>Makes the classes of the IR from the classes of the file, around the lowering of their members.</summary>
internal static class Classes {

  /// <summary>
  /// Gives every class its fields, each of the type every value stored into it has, and its methods,
  /// once the types are known and before any function is lowered; refuses a class the subset does not
  /// have and a field values of two kinds reach.
  /// </summary>
  public static void Declare(IReadOnlyList<ClassModel> classes, TypeInference inference, List<Diagnostic> diagnostics) {
    // A class follows the class it derives from, whose methods its overrides name.
    foreach (var owner in classes.OrderBy(owner => owner.Lineage.Count())) {
      Diagnostic Unsupported(Acornima.Ast.Node node, string what) => inference.Diagnose(node, JavaScriptFrontend.UnsupportedDescriptor, $"'{what}' is not supported yet");
      if (owner.Problem is { } problem)
        diagnostics.Add(Unsupported(owner.Declaration, problem));

      if (owner.Constructor is null && owner.Fields.Any(field => field.Initializer is not null))
        diagnostics.Add(Unsupported(owner.Declaration, "a class field with a value in a class without a constructor"));

      if (owner.Constructor is null && owner.Base?.Constructor is { } inherited && inference.ModelOf(inherited).Parameters.Count > 0)
        diagnostics.Add(Unsupported(owner.Declaration, "a derived class without a constructor whose base class constructor takes parameters"));

      owner.Ir.Inherit(owner.Base?.Ir, []);

      var fields = new List<IrField>();
      foreach (var field in owner.Fields) {
        var type = field.Variable.Type;
        if (type.Kind == JsKind.Conflict)
          diagnostics.Add(inference.Diagnose(field.Variable.Evidence ?? field.Variable.Declaration, JavaScriptFrontend.MixedKindsDescriptor, $"'{field.Key}' holds both {type.Kinds.First} and {type.Kinds.Second}"));

        var ir = type.Kind == JsKind.Conflict ? IrType.F64 : type.Ir ?? IrType.F64;
        if (ir.IsString || ir.IsView || ir.Element is { IsNumeric: false })
          diagnostics.Add(Unsupported(field.Variable.Declaration, $"a field of the type {type.Describe()}"));

        field.Ir = new IrField(ClassModel.IrName(field.Key), ir, false) { Visibility = field.IsPrivate ? IrVisibility.Private : IrVisibility.Public };
        fields.Add(field.Ir);
      }

      owner.Ir.Define(fields);
      // Every method of JavaScript is virtual; in the IR one is where a derived class overrides it.
      foreach (var member in owner.Members)
        owner.Methods[member] = new IrMethod(ClassModel.IrName(member.Key), member.Name, member.IsStatic) {
          Kind = member.Member switch { MemberKind.Getter => IrMethodKind.Getter, MemberKind.Setter => IrMethodKind.Setter, _ => IrMethodKind.Method },
          Visibility = member.Key.StartsWith('#') ? IrVisibility.Private : IrVisibility.Public,
          Dispatch = member.IsStatic ? IrDispatch.Static : member.Overrides is not null ? IrDispatch.Override : member.IsOverridden ? IrDispatch.Virtual : IrDispatch.Instance,
          Overrides = member.Overrides is { } replaced ? ClassModel.MethodOf(replaced) : null
        };

      owner.Creator = new IrConstructor(owner.Constructor?.Name ?? $"{owner.Name}.ctor");
    }
  }

  /// <summary>
  /// Gives every class its members once they are lowered: the constructor - one that does nothing for
  /// a class without one - the methods, the properties of its accessors and its static fields.
  /// </summary>
  /// <returns>The functions of the module that only a class has: the constructors made here.</returns>
  public static List<IrFunction> Define(IReadOnlyList<ClassModel> classes, TypeInference inference) {
    var made = new List<IrFunction>();
    foreach (var owner in classes) {
      if (owner.Constructor is null) {
        var receiver = new IrLocal("this", IrType.Object(owner.Ir));
        List<IrStmt> inherited = owner.Base is { } @base ? [new IrExprStmt(new IrCall(@base.Creator!.Function, IrType.Void, [new IrLocalRef(receiver)]))] : [];
        made.Add(new IrFunction(owner.Creator!.Function, false, IrType.Void, [receiver], [], new IrBlock(inherited)));
      }

      var properties = new List<IrProperty>();
      foreach (var key in owner.Members.Where(member => member.Member is MemberKind.Getter or MemberKind.Setter).Select(member => member.Key).Distinct()) {
        var getter = owner.OwnMember(MemberKind.Getter, key);
        var setter = owner.OwnMember(MemberKind.Setter, key);
        var type = getter is not null ? inference.ModelOf(getter).ReturnType : inference.ModelOf(setter!).Parameters[0].Parameter!.Type;
        properties.Add(new IrProperty(ClassModel.IrName(key), type) {
          Getter = getter is null ? null : owner.Methods[getter], Setter = setter is null ? null : owner.Methods[setter],
          Visibility = key.StartsWith('#') ? IrVisibility.Private : IrVisibility.Public
        });
      }

      owner.Ir.DefineMembers([owner.Creator!], [.. owner.Members.Select(member => owner.Methods[member])], properties, [.. owner.Statics.Values.Select(field => field.Global!)]);
    }

    return made;
  }

}
