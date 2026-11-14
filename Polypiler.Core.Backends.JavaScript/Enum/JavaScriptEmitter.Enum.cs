using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The name each enum of the module is written with as an object.</summary>
    private readonly Dictionary<IrEnumType, string> _enums = new(ReferenceEqualityComparer.Instance);

    /// <summary>The enums whose private table of names a helper reads, in the order they were first used.</summary>
    private readonly List<IrEnumType> _tables = [];

    /// <summary>
    /// Gives every enum of the module its name: the source name, unless a function, a constant, a
    /// global, a parameter or a local has it, which would hide the object or be hidden by it. Such an
    /// enum gets <c>$e</c> appended, which no name of a source and no other name the emitter makes has.
    /// </summary>
    private void NameEnums(IrModule module) {
      var taken = module.Functions.SelectMany(function => function.Parameters.Concat(function.Locals)).Select(variable => variable.Name)
        .Concat(module.Functions.Select(function => function.Name))
        .Concat(module.Constants.Select(constant => constant.Name))
        .Concat(module.Globals.Select(global => global.Name))
        .Select(this.Name).ToHashSet();
      foreach (var type in module.Types.OfType<IrEnumType>()) {
        var name = this.Name(type.Name);
        this._enums[type] = taken.Contains(name) ? name + "$e" : name;
      }

      this._members.UnionWith(this._enums.Values);
    }

    /// <summary>
    /// Whether an enum is an object of the module: one of up to 32 bits always, one of 64 bits where
    /// the level has <c>BigInt</c>; below that its members are pairs of halves, written where they are used.
    /// </summary>
    private bool IsObject(IrEnumType type) => !type.Underlying.IsLong || !this.Halves;

    /// <summary>
    /// The enums of the module as frozen objects of their members in declaration order, <c>const</c>
    /// where the level has it, exported where the module exports them. A public enum of 64 bits below
    /// ECMAScript 2020 has no object a caller could read its members from, and cannot be expressed.
    /// </summary>
    private List<JsStatement> Enums(IrModule module) {
      var declarations = new List<JsStatement>();
      foreach (var type in module.Types.OfType<IrEnumType>()) {
        this._construct = $"the enum '{type.Name}'";
        if (!this.IsObject(type)) {
          if (type.IsPublic && this._refused.Add(type.Name))
            this._problems.Add(InexpressibleDescriptor.At($"'the public 64-bit enum {type.Name}' cannot be expressed in JavaScript profile '{this._profile.LevelName}'"));

          continue;
        }

        this.Native("Object.freeze");
        var name = this._enums[type];
        var members = new JsObject([.. type.Members.Select(member => new JsProperty(member.Name, this.Literal(type.Underlying, IrOperators.Number(type.Underlying, member.Value))))]);
        JsStatement declaration = new JsVariableDeclaration(this.Native("const") ? "const" : "var", [new JsDeclarator(name, Invoke("Object.freeze", members))]);
        if (type.IsPublic && this._profile.Module == JavaScriptModuleFormat.Esm)
          declaration = new JsExport(declaration);
        else if (type.IsPublic)
          this._exported.Add(name);

        declarations.Add(declaration with { Leading = declarations.Count == 0 ? JsTrivia.Blank : JsTrivia.None });
      }

      return declarations;
    }

    /// <summary>
    /// The private tables of the enums a helper reads: the members in .NET's order, each as its name and
    /// the unsigned bits of its value, the lower half and, of 64 bits, the upper half.
    /// </summary>
    private List<JsStatement> Tables() => [.. this._tables.Select(type => AsHelper(new JsVariableDeclaration(this.Native("const") ? "const" : "var", [
      new JsDeclarator(TableName(this._enums[type]), new JsArray([.. type.Ordered.Select(member => {
        var bits = type.Unsigned(member.Value);
        List<JsExpression> entry = [new JsString(member.Name), Number((bits & 0xFFFFFFFF).ToString(CultureInfo.InvariantCulture))];
        if (type.Underlying.IsLong)
          entry.Add(Number((bits >> 32).ToString(CultureInfo.InvariantCulture)));

        return (JsExpression)new JsArray(entry);
      })]) { Tabulated = true })
    ])))];

    /// <summary>The name of the private table of an enum: no name of a source has a <c>$</c>, and no helper's name a second one.</summary>
    private static string TableName(string name) => "$names$" + name;

    /// <summary>The private table of an enum, which the module declares because this reads it.</summary>
    private JsIdentifier Table(IrEnumType type) {
      if (!this._tables.Contains(type))
        this._tables.Add(type);

      return Id(TableName(this._enums[type]));
    }

    /// <summary>A constant of an enum as the member of its object that has the value, or <see langword="null"/> where there is none.</summary>
    private JsExpression? Member(IrType type, long value)
      => type.EnumType is { } declared && this.IsObject(declared) && declared.MemberOf(value) is { } member
        ? new JsMember(Id(this._enums[declared]), member.Name) { Type = type }
        : null;

    /// <summary>
    /// An operation on enums: the text, whether a value is defined and the parse by the helpers over
    /// the enum's private table; the names and the values as arrays of their constants, in .NET's order;
    /// a flag as <c>(x &amp; f) == f</c> on the values of the underlying type, or <c>(~x &amp; f) == 0</c>
    /// where the flag is computed, so that it is computed once.
    /// </summary>
    private (JsExpression Node, bool Atomic) EnumOperation(IrIntrinsic intrinsic) {
      var type = EnumOperations.Of(intrinsic);
      var bits = Number(type.Underlying.Bits.ToString(CultureInfo.InvariantCulture));
      var signed = Flag(type.Underlying.IsSigned);
      var arguments = intrinsic.Arguments;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.EnumToString when type.Underlying.IsLong && this.Halves:
          return (this.Helper("$enumName64", [this.Table(type), Flag(type.IsFlags), this.Text(arguments[0]), signed]), true);
        case IrIntrinsicOp.EnumToString:
          return (this.Helper("$enumName", [this.Table(type), Flag(type.IsFlags), this.Text(arguments[0]), bits]), true);
        case IrIntrinsicOp.EnumIsDefined:
          return (this.Helper("$enumDefined", [this.Table(type), this.Text(arguments[0]), bits]), true);
        case IrIntrinsicOp.EnumParse when type.Underlying.IsLong:
          return (this.Helper("$enumParse64", [this.Table(type), this.Text(arguments[0]), this.Text(arguments[1]), signed]), true);
        case IrIntrinsicOp.EnumParse:
          return (this.Helper("$enumParse", [this.Table(type), this.Text(arguments[0]), this.Text(arguments[1]), bits, signed]), true);
        case IrIntrinsicOp.EnumNames:
          return this.Write(new IrIntrinsic(IrIntrinsicOp.ArrayOf, intrinsic.Type, [.. type.Ordered.Select(member => new IrStrConst(member.Name))]));
        case IrIntrinsicOp.EnumValues:
          return this.Write(new IrIntrinsic(IrIntrinsicOp.ArrayOf, intrinsic.Type, [.. type.Ordered.Select(member => new IrConst(type.Type, member.Value))]));
        default:
          return this.Expression(HasFlag(arguments[0], arguments[1]));
      }
    }

    /// <summary>
    /// <c>HasFlag</c> on the values of the underlying type, an enum narrower than 32 bits widened to
    /// <c>i32</c>: <c>(x &amp; f) == f</c> where the flag is a constant or a variable, which reading
    /// twice is reading once; <c>(~x &amp; f) == 0</c>, which reads each operand once, everywhere else.
    /// </summary>
    private static IrBinary HasFlag(IrExpr value, IrExpr flag) {
      var underlying = value.Type.Underlying;
      var work = underlying.IsWideInteger ? underlying : IrType.I32;
      IrExpr Plain(IrExpr operand) => underlying.IsWideInteger ? new IrConvert(work, operand) : new IrConvert(work, new IrConvert(underlying, operand));
      return flag is IrConst or IrLocalRef or IrConstRef
        ? new IrBinary(IrBinaryOp.Equal, new IrBinary(IrBinaryOp.And, Plain(value), Plain(flag)), Plain(flag))
        : new IrBinary(IrBinaryOp.Equal, new IrBinary(IrBinaryOp.And, new IrUnary(IrUnaryOp.BitNot, Plain(value)), Plain(flag)), new IrConst(work, 0));
    }

  }

}
