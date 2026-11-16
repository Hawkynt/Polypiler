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

    /// <summary>The enums of the module whose values are always one of their members (#462).</summary>
    private HashSet<IrEnumType> _closedEnums = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Finds the closed enums of a module: an enum of the IR is open - any value of its underlying type is
    /// one of it - and is closed here where the module can make no value but a member's. It is no flags
    /// enum, its members have distinct values in ascending order (the order a derived <c>Ord</c> compares
    /// by), and no value of it comes from anywhere else: no integer converted to it (a bitwise operator on it
    /// is one on its integer, converted back), no parse of a text, no constant that is no member, no public function that takes or gives it
    /// (a caller outside passes any integer), and - where no member is zero - no value it holds by
    /// default, in a new array, a default struct or a field of a class.
    /// </summary>
    private void ClosedEnums(IrModule module) {
      var closed = new HashSet<IrEnumType>(module.Types.OfType<IrEnumType>().Where(Closable), ReferenceEqualityComparer.Instance);
      if (closed.Count == 0) {
        this._closedEnums = closed;
        return;
      }

      void Open(IrType? type) {
        foreach (var named in Enums(type))
          closed.Remove(named);
      }

      void OpenDefaulted(IrType? type) {
        foreach (var named in Enums(type).Where(named => named.MemberOf(0) is null))
          closed.Remove(named);
      }

      foreach (var function in module.Functions.Where(function => function.IsPublic)) {
        Open(function.ReturnType);
        foreach (var parameter in function.Parameters)
          Open(parameter.Type);
      }

      foreach (var field in module.Types.OfType<IrClassType>().SelectMany(type => type.AllFields))
        OpenDefaulted(field.Type);

      var expressions = module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).Concat(module.Globals.Select(global => global.Initializer).OfType<IrExpr>().SelectMany(IrTree.Nodes));
      foreach (var node in expressions) {
        switch (node) {
          case IrConvert { Type.EnumType: { } } convert:
            Open(convert.Type);
            break;
          case IrIntrinsic { Op: IrIntrinsicOp.EnumParse } parse:
            Open(parse.Type);
            break;
          case IrConst { Type.EnumType: { } named } constant when named.MemberOf(constant.Value) is null:
            Open(constant.Type);
            break;
          case IrNewArray or IrStructDefault:
            OpenDefaulted(node.Type);
            break;
        }
      }

      this._closedEnums = closed;
    }

    /// <summary>Whether an enum can be closed at all: no flags, one to 256 members of distinct values in ascending order.</summary>
    private static bool Closable(IrEnumType type) {
      if (type.IsFlags || type.Members.Count is 0 or > 256)
        return false;

      var values = type.Members.Select(member => IrOperators.Number(type.Underlying, member.Value)).ToList();
      return values.Zip(values.Skip(1)).All(pair => pair.First < pair.Second);
    }

    /// <summary>The enums a type holds values of: itself, the elements of an array, the fields of a struct, through structs of structs.</summary>
    private static IEnumerable<IrEnumType> Enums(IrType? type, HashSet<IrStructType>? seen = null) {
      if (type is null)
        yield break;
      if (type.EnumType is { } named)
        yield return named;
      if (type.Element is { } element)
        foreach (var inner in Enums(element, seen))
          yield return inner;
      if (type.Underlying is { } underlying && !type.IsEnum)
        foreach (var inner in Enums(underlying, seen))
          yield return inner;
      if (type.StructType is { } declared && (seen ??= new HashSet<IrStructType>(ReferenceEqualityComparer.Instance)).Add(declared))
        foreach (var inner in declared.Fields.SelectMany(field => Enums(field.Type, seen)))
          yield return inner;
    }

    /// <summary>Whether values of a type are those of a closed enum, a Rust <c>enum</c>.</summary>
    private bool IsClosedEnum(IrType type) => type.EnumType is { } named && this._closedEnums.Contains(named);

    /// <summary>The members of a closed enum with the names of their variants, in declaration order.</summary>
    private IReadOnlyList<(IrEnumMember Member, string Rust)> VariantNames(IrEnumType type) {
      var names = type.Members.Select(member => member.Name).ToList();
      return [.. type.Members.Zip(RustNames.Unique(names, member => RustNames.UpperCamel(member, this.Edition), new HashSet<string>(StringComparer.Ordinal)))];
    }

    /// <summary>The variant of a closed enum that has a value, <c>Op::Sub</c>.</summary>
    private RsPath Variant(IrEnumType type, long value)
      => new($"{this._types[type]}::{this.VariantNames(type).First(pair => pair.Member.Value == value).Rust}") { Type = type.Type };

    /// <summary>
    /// A closed enum as a Rust enum with the values of the IR as its discriminants: <c>#[repr(u8)] pub enum Op { Add = 1, Sub = 5 }</c>,
    /// with a derived <c>Default</c> - <c>#[default]</c> on the member of value zero, or on the first, which no
    /// value of the module is by default - the checked conversion from its integer, <c>impl TryFrom&lt;u8&gt; for Op</c>, a
    /// <c>match</c> over the declared values, and the conversion back, <c>From&lt;Op&gt; for u8</c>.
    /// </summary>
    private IEnumerable<RsItem> ClosedEnumItems(IrEnumType type) {
      var name = this._types[type];
      var underlying = TypeName(type.Underlying)!;
      var variants = this.VariantNames(type);
      var fallback = variants.FirstOrDefault(pair => pair.Member.Value == 0) is { Rust: { } zero } ? zero : variants[0].Rust;
      yield return new RsEnum(name, "pub", [.. variants.Select(pair => new RsVariant(pair.Rust, Discriminant(type, pair.Member.Value)) { IsDefault = pair.Rust == fallback })]) {
        Attributes = [$"repr({underlying})", "derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)"]
      };

      var arms = variants.Select(pair => new RsMatchArm([Discriminant(type, pair.Member.Value)], new RsCall(new RsPath("Ok"), [new RsPath("Self::" + pair.Rust)]))).ToList();
      var exhaustive = type.Underlying.Bits == 8 && variants.Count == 256;
      if (!exhaustive)
        arms.Add(new RsMatchArm(["_"], new RsCall(new RsPath("Err"), [new RsPath("value")])));
      yield return new RsImpl($"TryFrom<{underlying}>", name, [
        new RsTypeAlias("Error", underlying),
        new RsFunction("try_from", "", [new RsParameter("value", underlying)], $"Result<Self, {underlying}>", new RsBlock([], new RsMatch(new RsPath("value"), arms)))
      ]);
      yield return new RsImpl($"From<{name}>", underlying, [new RsFunction("from", "", [new RsParameter("value", name)], "Self", new RsBlock([], new RsCast(new RsPath("value"), underlying)))]);
    }

    /// <summary>The value of a member as a discriminant and a pattern: the integer without a suffix, negative with its sign.</summary>
    private static string Discriminant(IrEnumType type, long value) => IrOperators.Number(type.Underlying, value).ToString(CultureInfo.InvariantCulture);

    /// <summary>The patterns of a switch over a closed enum: the variants of its labels.</summary>
    private IReadOnlyList<string> VariantPatterns(IrEnumType type, IReadOnlyList<long> labels) => [.. labels.Select(label => this.Variant(type, label).Name)];

  }

}
