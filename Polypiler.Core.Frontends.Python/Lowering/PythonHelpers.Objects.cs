using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>The operations of enums that no single IR operation computes: the member of a value and the name of a member.</summary>
internal sealed partial class PythonHelpers {

  /// <summary><c>E(v)</c>: the member whose value is <c>v</c>; <c>ValueError</c> where none is.</summary>
  public IrCall EnumOf(IrExpr value, IrEnumType type, IReadOnlyList<long> values, string name)
    => this.Call($"PyEnumOf_{type.Name}", type.Type, [value], made => {
      var v = Local("value", IrType.I64);
      var found = values.Aggregate((IrExpr)Bool(false), (any, member) => Or(any, Equal(Ref(v), Int(member))));
      return Function(made, type.Type, [v], [],
        If(Not(found), Raise(IrExceptionClass.ArgumentException, $"the value is not a valid {name}")),
        Return(Convert(type.Type, Ref(v))));
    });

  /// <summary><c>e.name</c>: the name of a member.</summary>
  public IrCall EnumName(IrExpr member, IrEnumType type, IReadOnlyList<(string Name, long Value)> members)
    => this.Call($"PyEnumName_{type.Name}", IrType.String, [member], made => {
      var e = Local("member", type.Type);
      IrExpr name = new IrStrConst(members[^1].Name);
      for (var index = members.Count - 2; index >= 0; --index)
        name = Choose(Equal(Convert(IrType.I64, Ref(e)), Int(members[index].Value)), new IrStrConst(members[index].Name), name);

      return Function(made, IrType.String, [e], [], Return(name));
    });

}
