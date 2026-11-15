using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for the operations on enums (#1318). Every profile has an enum
/// declaration and the members of <c>System.Enum</c> that take a <c>Type</c>; the generic members
/// follow the "Applies to" tables of the API reference - <c>Enum.Parse&lt;TEnum&gt;</c> from .NET Core
/// 2.0 and .NET Standard 2.1, <c>Enum.IsDefined&lt;TEnum&gt;</c>, <c>GetNames&lt;TEnum&gt;</c> and
/// <c>GetValues&lt;TEnum&gt;</c> from .NET 5. A template's <c>{t}</c> is the enum's name. The emitter never
/// writes <c>HasFlag</c>, which .NET Framework 4.0 introduced and which boxes before .NET Core 2.1.
/// </summary>
internal static class CSharpEnumRows {

  private const string Enum = "global::System.Enum.";

  private static readonly IrTypeKind[] _any = [];

  /// <summary>Every row of the enums.</summary>
  public static IEnumerable<CSharpCapability> All => [
    CSharpCapability.OfApi("enum declaration with [Flags] (FlagsAttribute)", CSharpProfile.Net20),
    CSharpCapability.OfApi("Enum.HasFlag(Enum)", CSharpProfile.Net46),
    Written("Enum.ToString()", IrIntrinsicOp.EnumToString, "{0}.ToString()"),
    Generic("Enum.IsDefined<TEnum>(TEnum)", CSharpProfile.Net80, IrIntrinsicOp.EnumIsDefined, Enum + "IsDefined<{t}>({0})", Enum + "IsDefined(typeof({t}), {0})"),
    Generic("Enum.Parse<TEnum>(String, Boolean)", CSharpProfile.NetStandard21, IrIntrinsicOp.EnumParse, Enum + "Parse<{t}>({0}, {1})", "(({t})" + Enum + "Parse(typeof({t}), {0}, {1}))"),
    Written("(x & f) == f, or (~x & f) == 0 where f is computed", IrIntrinsicOp.EnumHasFlag, "((~{0} & {1}) == 0)")
      .WithTotal(new CSharpForm(null, Template: "(({0} & {1}) == {1})"), arguments => arguments[1] is IrConst or IrLocalRef or IrConstRef),
    Generic("Enum.GetNames<TEnum>()", CSharpProfile.Net80, IrIntrinsicOp.EnumNames, Enum + "GetNames<{t}>()", Enum + "GetNames(typeof({t}))"),
    Generic("Enum.GetValues<TEnum>()", CSharpProfile.Net80, IrIntrinsicOp.EnumValues, Enum + "GetValues<{t}>()", "(({t}[])" + Enum + "GetValues(typeof({t})))")
  ];

  /// <summary>A row every profile writes alike.</summary>
  private static CSharpCapability Written(string name, IrIntrinsicOp op, string template)
    => CSharpCapability.OfOperation(name, CSharpProfile.Net20, op, _any, new CSharpForm(null, Template: template));

  /// <summary>A generic member from its first profile on, the member that takes a <c>Type</c> before.</summary>
  private static CSharpCapability Generic(string name, CSharpProfile since, IrIntrinsicOp op, string native, string replacement)
    => CSharpCapability.OfOperation(name, since, op, _any, new CSharpForm(null, Template: native), new CSharpForm(null, Template: replacement));

}
