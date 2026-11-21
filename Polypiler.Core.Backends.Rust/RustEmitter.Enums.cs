using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The tables of values and names of the enums the crate needs them of, by enum.</summary>
    private readonly Dictionary<IrEnumType, (string Values, string Names)> _enumTables = new(ReferenceEqualityComparer.Instance);

    /// <summary>The table items of the enums, in the order they were asked for.</summary>
    private readonly List<RsItem> _enumTableItems = [];

    /// <summary>
    /// An enum as a newtype over its underlying integer, as a C# enum is open - any value of the
    /// underlying type is one of it: <c>pub struct Color(pub u8);</c> with the derives of a value, an
    /// associated constant per member, and <c>From</c> both ways.
    /// </summary>
    private IEnumerable<RsItem> EnumItems(IrEnumType type) {
      var name = this._types[type];
      var underlying = TypeName(type.Underlying)!;
      yield return new RsStruct(name, "pub", [new RsFieldDeclaration("", "pub", underlying)]) {
        Attributes = ["derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)"]
      };

      var constants = this.MemberNames(type).Select(pair => (RsItem)new RsConst(pair.Rust, "pub", name,
        new RsCall(new RsPath(name), [Literal(new IrConst(type.Underlying, pair.Member.Value))]))).ToList();
      if (constants.Count > 0)
        yield return new RsImpl(null, name, constants);

      yield return new RsImpl($"From<{underlying}>", name, [new RsFunction("from", "", [new RsParameter("value", underlying)], "Self", new RsBlock([], new RsCall(new RsPath("Self"), [new RsPath("value")])))]);
      yield return new RsImpl($"From<{name}>", underlying, [new RsFunction("from", "", [new RsParameter("value", name)], "Self", new RsBlock([], new RsField(new RsPath("value"), "0")))]);
    }

    /// <summary>The members of an enum with their Rust names, in declaration order.</summary>
    private IReadOnlyList<(IrEnumMember Member, string Rust)> MemberNames(IrEnumType type) {
      var names = type.Members.Select(member => member.Name).ToList();
      return [.. type.Members.Zip(RustNames.Unique(names, member => RustNames.Screaming(member, this.Edition), new HashSet<string>(StringComparer.Ordinal)))];
    }

    /// <summary>A constant of an enum: its member's constant where one has the value, the first in declaration order, else the value wrapped.</summary>
    private RsExpression EnumConstant(IrConst constant) {
      var type = constant.Type.EnumType!;
      if (this._closedEnums.Contains(type))
        return this.Variant(type, constant.Value);

      var name = this._types[type];
      var member = this.MemberNames(type).FirstOrDefault(pair => pair.Member.Value == constant.Value);
      return member.Rust is { } rust
        ? new RsPath($"{name}::{rust}") { Type = constant.Type }
        : new RsCall(new RsPath(name), [Literal(new IrConst(type.Underlying, constant.Value))]) { Type = constant.Type };
    }

    /// <summary>The integer an enum value holds: <c>v.0</c>.</summary>
    private RsExpression Inner(IrExpr value) {
      if (value is IrConvert { Type.IsEnum: true } wrapped)
        return this.Expression(wrapped.Operand);

      // A value the emitter has just wrapped, Color(x), holds x; a variant of a closed enum is cast, Op::Sub as u8.
      var written = this.Expression(value);
      if (this.IsClosedEnum(value.Type))
        return this.Cast(written, value.Type.EnumType!.Underlying);

      return written is RsCall { Callee: RsPath callee, Arguments: [var inner] } && callee.Name == this._types[value.Type.EnumType!]
        ? inner
        : new RsField(this.Operand(written, 15), "0") { Type = value.Type.EnumType!.Underlying };
    }

    /// <summary>An integer as a value of an enum: <c>Color(x)</c>.</summary>
    private RsExpression Wrapped(IrType type, RsExpression value) => new RsCall(new RsPath(this._types[type.EnumType!]), [value]) { Type = type };

    /// <summary>The bits of an enum value as the unsigned number .NET orders and formats by: <c>u64::from(v.0)</c>, <c>v.0 as u8 as u64</c> for a signed type.</summary>
    private RsExpression Bits(IrExpr value) {
      var underlying = value.Type.EnumType!.Underlying;
      var inner = this.Inner(value);
      if (!underlying.IsSigned)
        return underlying == IrType.U64 ? inner : new RsCall(new RsPath("u64::from"), [inner]) { Type = IrType.U64 };

      var unsigned = underlying.Kind switch { IrTypeKind.I8 => IrType.U8, IrTypeKind.I16 => IrType.U16, IrTypeKind.I32 => IrType.U32, _ => IrType.U64 };
      var cast = this.Cast(inner, unsigned);
      return unsigned == IrType.U64 ? cast : this.Cast(cast, IrType.U64);
    }

    /// <summary>The tables of an enum: the values in .NET's order as unsigned bits and the names beside them, statics of the crate.</summary>
    private (string Values, string Names) EnumTables(IrEnumType type) {
      if (this._enumTables.TryGetValue(type, out var known))
        return known;

      var stem = RustNames.Screaming(this._types[type], this.Edition);
      var ordered = type.Ordered;
      var values = stem + "_VALUES";
      var names = stem + "_NAMES";
      this._enumTableItems.Add(new RsConst(values, "", $"[u64; {ordered.Count}]",
        new RsArray([.. ordered.Select(member => (RsExpression)new RsLiteral(type.Unsigned(member.Value).ToString(CultureInfo.InvariantCulture)))])) { IsStatic = true });
      this._enumTableItems.Add(new RsConst(names, "", $"[&str; {ordered.Count}]", new RsArray([.. ordered.Select(member => (RsExpression)new RsString(member.Name))])) { IsStatic = true });
      return this._enumTables[type] = (values, names);
    }

    /// <summary>
    /// An operation of the IR on an enum: its text and its parse by the helpers over its tables, whether
    /// a value is defined, whether it has a flag, its names and its values as vectors in .NET's order.
    /// </summary>
    private RsExpression EnumOperation(IrIntrinsic operation) {
      var arguments = operation.Arguments;
      var type = EnumOperations.Of(operation);
      var name = this._types[type];
      switch (operation.Op) {
        case IrIntrinsicOp.EnumToString: {
          var (values, names) = this.EnumTables(type);
          return new RsCall(new RsPath(this.StringHelper("ply_enum_text")), [
            this.Bits(arguments[0]), new RsMethodCall(this.Receiver(this.Inner(arguments[0])), "to_string", []), new RsReference(false, new RsPath(values)),
            new RsReference(false, new RsPath(names)), new RsLiteral(type.IsFlags ? "true" : "false")
          ]) { Type = IrType.String };
        }
        case IrIntrinsicOp.EnumIsDefined: {
          var (values, _) = this.EnumTables(type);
          return new RsMethodCall(new RsPath(values), "contains", [new RsReference(false, this.Bits(arguments[0]))]) { Type = IrType.Bool };
        }
        case IrIntrinsicOp.EnumParse: {
          var (values, names) = this.EnumTables(type);
          var underlying = type.Underlying;
          var parsed = this.HelperCall("ply_enum_parse", IrType.U64, [
            this.StringArgument(arguments[0]), new RsReference(false, new RsPath(values)), new RsReference(false, new RsPath(names)), this.Expression(arguments[1]),
            new RsLiteral(IrOperators.Minimum(underlying).ToString(CultureInfo.InvariantCulture)), new RsLiteral(IrOperators.Maximum(underlying).ToString(CultureInfo.InvariantCulture)),
            new RsReference(false, new RsPath(this.WhitespaceTable(WhitespaceSet.DotNet)))
          ]);
          return this.Wrapped(operation.Type, underlying == IrType.U64 ? parsed : this.Cast(parsed, underlying));
        }
        case IrIntrinsicOp.EnumHasFlag: {
          var flag = this.Inner(arguments[1]);
          return this.Binary(new RsParenthesized(this.Binary(this.Inner(arguments[0]), "&", flag, type.Underlying)), "==", flag, IrType.Bool);
        }
        case IrIntrinsicOp.EnumNames:
          return type.Members.Count == 0 ? new RsCall(new RsPath("Vec::new"), []) { Type = operation.Type }
            : new RsMacro("vec", [new RsArray([.. type.Ordered.Select(member => this.Utf16(member.Name))])]) { Bracketed = true, Type = operation.Type };
        default:
          return type.Members.Count == 0 ? new RsCall(new RsPath("Vec::new"), []) { Type = operation.Type }
            : new RsMacro("vec", [new RsArray([.. type.Ordered.Select(member => this.EnumConstant(new IrConst(type.Type, member.Value)))])]) { Bracketed = true, Type = operation.Type };
      }
    }

  }

}
