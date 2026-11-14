using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>One use of a member of the JavaScript integer table: a module that exports <c>f</c>, and the types of its parameters.</summary>
/// <param name="Member">The member of the table it uses.</param>
/// <param name="Source">The module.</param>
/// <param name="Parameters">The type of each parameter of <c>f</c>, which its JSDoc states.</param>
internal sealed record JavaScriptIntegerUse(string Member, string Source, IrType[] Parameters) {

  /// <summary>The use as a test names it.</summary>
  public override string ToString() => this.Source;

}

/// <summary>
/// The uses of the members of <c>JavaScriptIntegerApi</c> that the unit tests import and the
/// acceptance tests run in node, and the calls that run them: every combination of the edges of
/// the parameters.
/// </summary>
internal static class JavaScriptIntegerUses {

  private const string View = "DataView.prototype.";

  private static readonly byte[] _bytes = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0x80, 0x7F];

  /// <summary>Every use.</summary>
  public static IReadOnlyList<JavaScriptIntegerUse> All { get; } = Build();

  private static JavaScriptIntegerUse Use(string member, string body, params (string Name, IrType Type)[] parameters) {
    var doc = string.Join("\n", parameters.Select(parameter => $" * @param {{{Spell(parameter.Type)}}} {parameter.Name}"));
    return new(member, $"/**\n{doc}\n */\nexport function f({string.Join(", ", parameters.Select(parameter => parameter.Name))}) {{\n  {body}\n}}\n", [.. parameters.Select(parameter => parameter.Type)]);
  }

  private static string Spell(IrType type) => type.Kind switch {
    IrTypeKind.Array => "Uint8Array",
    IrTypeKind.F64 => "number",
    IrTypeKind.I8 => "int8",
    IrTypeKind.U8 => "uint8",
    IrTypeKind.I16 => "int16",
    IrTypeKind.U16 => "uint16",
    IrTypeKind.I32 => "int32",
    IrTypeKind.U32 => "uint32",
    IrTypeKind.I64 => "int64",
    _ => "uint64"
  };

  private static List<JavaScriptIntegerUse> Build() {
    var bytes = IrType.Array(IrType.U8);
    var uses = new List<JavaScriptIntegerUse> {
      Use("Math.clz32", "return Math.clz32(a);", ("a", IrType.U32)),
      Use("Math.clz32", "return Math.clz32(a);", ("a", IrType.I16)),
      Use("Math.imul", "return Math.imul(a, b);", ("a", IrType.I32), ("b", IrType.U32)),
      Use("Math.abs", "return Math.abs(a);", ("a", IrType.F64)),
      Use("Math.min", "return Math.min(a, b);", ("a", IrType.F64), ("b", IrType.I32)),
      Use("Math.max", "return Math.max(a, b);", ("a", IrType.I32), ("b", IrType.U32)),
      Use("Math.sign", "return Math.sign(a);", ("a", IrType.F64))
    };
    foreach (var type in new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32 }) {
      uses.Add(Use("Math.abs", "return Math.abs(a);", ("a", type)));
      uses.Add(Use("Math.sign", "return Math.sign(a);", ("a", type)));
      uses.Add(Use("Math.min", "return Math.min(a, b);", ("a", type), ("b", IrType.I8)));
      uses.Add(Use("Math.max", "return Math.max(a, b, 3);", ("a", type), ("b", type)));
    }

    // The width 64 is the cut to the other 64-bit type: a value of that type is converted.
    foreach (var width in new[] { 1, 8, 16, 31, 32, 63, 64 }) {
      uses.Add(Use("BigInt.asUintN", $"return BigInt.asUintN({width}, a);", ("a", width == 64 ? IrType.I64 : IrType.U64)));
      uses.Add(Use("BigInt.asIntN", $"return BigInt.asIntN({width}, a);", ("a", width == 64 ? IrType.U64 : IrType.I64)));
    }

    uses.Add(Use("BigInt.asUintN", "return BigInt.asUintN(16, a * a);", ("a", IrType.U64)));
    uses.Add(Use("BigInt.asIntN", "return BigInt.asIntN(32, a + 1n);", ("a", IrType.I64)));
    foreach (var (name, type) in new[] { ("Int8", IrType.I8), ("Uint8", IrType.U8), ("Int16", IrType.I16), ("Uint16", IrType.U16),
               ("Int32", IrType.I32), ("Uint32", IrType.U32), ("BigInt64", IrType.I64), ("BigUint64", IrType.U64) }) {
      foreach (var order in type.Bits == 8 ? new[] { "" } : ["", ", true", ", false"]) {
        uses.Add(Use($"{View}get{name}", $"const v = new DataView(b.buffer);\n  return v.get{name}(i{order});", ("b", bytes), ("i", IrType.I32)));
        uses.Add(Use($"{View}set{name}", $"const v = new DataView(b.buffer, b.byteOffset, b.byteLength);\n  v.set{name}(i, x{order});\n  return b;", ("b", bytes), ("i", IrType.I32), ("x", type)));
      }
    }

    return uses;
  }

  /// <summary>Every combination of the edges of the parameters of a use.</summary>
  public static IReadOnlyList<PrototypeCall> Calls(JavaScriptIntegerUse use) {
    IEnumerable<object[]> tuples = [[]];
    for (var index = 0; index < use.Parameters.Length; ++index) {
      var type = use.Parameters[index];
      object[] values = type.Kind switch {
        IrTypeKind.Array => [_bytes, new byte[2], new byte[8]],
        IrTypeKind.F64 => [0.0, -2.5, 7.0, -2147483648.0, 1e20, double.NaN],
        IrTypeKind.I32 when use.Parameters[0].IsArray && index == 1 => [-1, 0, 1, 3, 4, 6, 7, 9, 10, 11],
        _ => IntegerCases.Edges(type)
      };
      tuples = tuples.SelectMany(tuple => values.Select(value => (object[])[.. tuple, value])).ToList();
    }

    return [.. tuples.Select(tuple => new PrototypeCall("f", tuple))];
  }

}
