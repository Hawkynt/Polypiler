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

      owner.Ir.Inherit(owner.Base?.Ir, []);

      var fields = new List<IrField>();
      foreach (var field in owner.Fields) {
        var type = field.Variable.Type;
        if (type.Kind == JsKind.Conflict)
          diagnostics.Add(inference.Diagnose(field.Variable.Evidence ?? field.Variable.Declaration, JavaScriptFrontend.MixedKindsDescriptor, $"'{field.Key}' holds both {type.Kinds.First} and {type.Kinds.Second}"));

        // A view stored into a field is a memory (Stored), so every type a field reaches is one the IR lets a field of a class have.
        var ir = type.Kind == JsKind.Conflict ? IrType.F64 : type.Ir ?? IrType.F64;

        // A field derived classes declare accessors over is a virtual property, backed by a private field of a name no key spells (#1402).
        field.Ir = new IrField(ClassModel.IrName(field.Key) + (field.IsVirtual ? "$" : ""), ir, owner.IsValue) { Visibility = field.IsPrivate || field.IsVirtual ? IrVisibility.Private : IrVisibility.Public };
        fields.Add(field.Ir);
        if (field.IsVirtual) {
          field.Getter = new IrMethod(ClassModel.IrName(field.Key), $"{owner.Name}.get_{ClassModel.IrName(field.Key)}", false) { Kind = IrMethodKind.Getter, Dispatch = IrDispatch.Virtual };
          field.Setter = new IrMethod(ClassModel.IrName(field.Key), $"{owner.Name}.set_{ClassModel.IrName(field.Key)}", false) { Kind = IrMethodKind.Setter, Dispatch = IrDispatch.Virtual };
        }
      }

      owner.Ir.Define(fields);
      // Every method of JavaScript is virtual; in the IR one is where a derived class overrides it.
      foreach (var member in owner.Members)
        owner.Methods[member] = new IrMethod(ClassModel.IrName(member.Key), member.Name, member.IsStatic) {
          Kind = member.Member switch { MemberKind.Getter => IrMethodKind.Getter, MemberKind.Setter => IrMethodKind.Setter, _ => IrMethodKind.Method },
          Visibility = member.Key.StartsWith('#') ? IrVisibility.Private : IrVisibility.Public,
          Dispatch = member.IsStatic ? IrDispatch.Static : member.Overrides is not null || member.OverridesField is not null ? IrDispatch.Override : member.IsOverridden ? IrDispatch.Virtual : IrDispatch.Instance,
          Overrides = member.Overrides is { } replaced ? ClassModel.MethodOf(replaced) : member.OverridesField is { } field ? Accessor(field, member.Member) : null
        };

      // An accessor over a field of a base class has the type of the property the field is (#1402).
      foreach (var member in owner.Members.Where(member => member.OverridesField is not null)) {
        var field = member.OverridesField!;
        var model = inference.ModelOf(member);
        var type = member.Member == MemberKind.Getter ? model.ReturnType : model.Parameters[0].Entry.Ir;
        if (type != field.Ir!.Type)
          diagnostics.Add(Unsupported(member.Anchor, $"the accessor {member.Key} of the type {(type is null ? "nothing" : JsType.Spell(type))} over a field of the type {JsType.Spell(field.Ir.Type)}"));
      }

      // The class of a record runs no constructor of its lineage, as a literal runs none (#1388).
      owner.Creator = new IrConstructor(owner.Constructor?.Name ?? $"{owner.Name}.ctor") { SkipsBase = owner.RecordKeys is not null };
    }
  }

  /// <summary>The accessor of the virtual property of a field that a getter or a setter of a derived class overrides.</summary>
  private static IrMethod Accessor(ClassField field, MemberKind kind) => kind == MemberKind.Getter ? field.Getter! : field.Setter!;

  /// <summary>
  /// The fields the constructor of the class of a record stores (#1388): the field of each key of the
  /// record, in its order, then every field of the lineage that is no private one and that it lacks.
  /// </summary>
  public static List<ClassField> RecordFields(ClassModel owner)
    => [.. owner.RecordKeys!.Select(key => owner.Field(key)!), .. owner.Base!.Lineage.Reverse().SelectMany(type => type.Fields).Where(field => !field.IsPrivate && !owner.RecordKeys!.Contains(field.Key))];

  /// <summary>
  /// Gives every class its members once they are lowered: the constructor - one that does nothing for
  /// a class without one - the methods, the properties of its accessors and its static fields.
  /// </summary>
  /// <returns>The functions of the module that only a class has: the constructors made here.</returns>
  public static List<IrFunction> Define(IReadOnlyList<ClassModel> classes, TypeInference inference) {
    var made = new List<IrFunction>();
    foreach (var owner in classes) {
      if (owner.Constructor is null) {
        // A class without a constructor takes the parameters of the nearest one it inherits and passes them on (#1369).
        var receiver = new IrLocal("this", IrType.Object(owner.Ir));
        List<IrLocal> parameters = owner.Initial is { } initial ? [.. inference.ModelOf(initial).Parameters.Select(parameter => new IrLocal(parameter.Name, parameter.Parameter!.Type))] : [];
        List<IrStmt> inherited = owner.Base is { } @base ? [new IrExprStmt(new IrCall(@base.Creator!.Function, IrType.Void, [new IrLocalRef(receiver), .. parameters.Select(parameter => new IrLocalRef(parameter))]))] : [];
        // The class of a record takes the value of each key in the record's order, then of each field it lacks, and stores them (#1388).
        if (owner.RecordKeys is not null) {
          var stored = RecordFields(owner);
          parameters = [.. stored.Select(field => new IrLocal(field.Ir!.Name, field.Ir.Type))];
          inherited = [.. stored.Select((field, index) => (IrStmt)new IrFieldStore(new IrLocalRef(receiver), field.Ir!, new IrLocalRef(parameters[index])))];
        }

        // The class of the members of a frozen object takes the value of every field (#1371), as does that of a typedef (#1409).
        if (owner.IsValue) {
          parameters = [.. owner.Fields.Select(field => new IrLocal(field.Ir!.Name, field.Ir.Type))];
          inherited = [.. owner.Fields.Select((field, index) => (IrStmt)new IrFieldStore(new IrLocalRef(receiver), field.Ir!, new IrLocalRef(parameters[index])))];
        }

        made.Add(new IrFunction(owner.Creator!.Function, false, IrType.Void, [receiver, .. parameters], [], new IrBlock(inherited)));
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

      // The virtual property of a field derived classes declare accessors over reads and writes the field (#1402).
      var virtuals = owner.Fields.Where(field => field.IsVirtual).ToList();
      foreach (var field in virtuals) {
        var self = new IrLocal("this", IrType.Object(owner.Ir));
        var value = new IrLocal("value", field.Ir!.Type);
        made.Add(new IrFunction(field.Getter!.Function, false, field.Ir.Type, [self], [], new IrBlock([new IrReturn(new IrFieldLoad(new IrLocalRef(self), field.Ir))])));
        made.Add(new IrFunction(field.Setter!.Function, false, IrType.Void, [self, value], [], new IrBlock([new IrFieldStore(new IrLocalRef(self), field.Ir, new IrLocalRef(value))])));
        properties.Add(new IrProperty(ClassModel.IrName(field.Key), field.Ir.Type) { Getter = field.Getter, Setter = field.Setter });
      }

      owner.Ir.DefineMembers([owner.Creator!], [.. owner.Members.Select(member => owner.Methods[member]), .. virtuals.SelectMany(field => (IrMethod[])[field.Getter!, field.Setter!])], properties,
        [.. owner.Statics.Values.Select(field => field.Global!)]);
    }

    return made;
  }

}
