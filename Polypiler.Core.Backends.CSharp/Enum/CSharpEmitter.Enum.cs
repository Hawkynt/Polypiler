using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>
  /// The enums a public member of the module names - a public function by its parameters or its
  /// result, a struct by its fields, which are public - and which C# therefore needs to be public as
  /// well; any other enum is public only when the module exports it.
  /// </summary>
  private static HashSet<IrEnumType> Exposed(IrModule module) {
    static IEnumerable<IrEnumType> Named(IrType type) => (type.Element ?? type).EnumType is { } named ? [named] : [];
    var signatures = module.Functions.Where(function => function.IsPublic)
      .SelectMany(function => function.Parameters.Select(parameter => parameter.Type).Append(function.ReturnType));
    var fields = module.Types.OfType<IrStructType>().SelectMany(type => type.Fields.Select(field => field.Type));
    return new HashSet<IrEnumType>(signatures.Concat(fields).SelectMany(Named), ReferenceEqualityComparer.Instance);
  }

  private sealed partial class Writer {

    private readonly Dictionary<IrEnumType, Dictionary<string, string>> _memberNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>The C# name of every member of an enum: its own where C# can write it, with an <c>@</c> for a keyword.</summary>
    private Dictionary<string, string> MemberNames(IrEnumType type) {
      if (!this._memberNames.TryGetValue(type, out var names))
        this._memberNames[type] = names = Names(type.Members.Select(member => member.Name), []);

      return names;
    }

    /// <summary>
    /// The enums of the module, each with its underlying type and its members in declaration order,
    /// <c>[Flags]</c> where it has the attribute, public where the module exports it or a public member names it.
    /// </summary>
    private List<CsMember> Enums() {
      var exposed = Exposed(this._module);
      return [.. this._module.Types.OfType<IrEnumType>().Select(type => (CsMember)new CsEnumDeclaration(
        type.IsFlags ? [Base + "Flags"] : [],
        [type.IsPublic || exposed.Contains(type) ? "public" : "internal"],
        this._typeNames[type.Name],
        new CsType(Keyword(type.Underlying)),
        [.. type.Members.Select(member => new CsEnumMember(this.MemberNames(type)[member.Name], Bare(type.Underlying, member.Value)))]))];
    }

    /// <summary>
    /// A constant of an enum: the first member, in declaration order, with its value, or else the value
    /// cast to the enum - the value C# keeps whether it names a member or not.
    /// </summary>
    private (CsExpression Node, int Binding) EnumLiteral(IrType type, long value) {
      var declared = type.EnumType!;
      var name = this.TypeName(type);
      if (declared.MemberOf(value) is { } member)
        return (new CsMemberAccess(new CsName(name), this.MemberNames(declared)[member.Name]) { Type = type }, Primary);

      // A negative number in parentheses: (E)-1 would be a subtraction from E.
      var number = Bare(declared.Underlying, value);
      return (new CsCast(new CsType(name), Number(declared.Underlying, value).Sign < 0 ? new CsParenthesized(number) : number) { Type = type }, Cast);
    }

    /// <summary>A constant of the module of an enum type, or a table of them: <c>const</c> or a read-only array of the enum's constants.</summary>
    private CsField EnumConstant(IrModuleConst constant, string name) {
      var type = new CsType(this.TypeName(constant.Type));
      if (constant.Type.Element is not { } element)
        return new CsField(["private", "const"], type, name, this.EnumLiteral(constant.Type, constant.Values[0]).Node);

      return new CsField(["private", "static", "readonly"], type, name,
        new CsArrayInitializer([.. constant.Values.Select(value => this.EnumLiteral(element, value).Node)]) { Tabulated = true });
    }

  }

}
