using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>
/// An operation of the IR at one scalar type, written <c>Op.type</c> as in a cost profile: <c>RotateLeft.u32</c>,
/// <c>Xor.u8</c>. <c>Op</c> is the name of a member of <see cref="IrBinaryOp"/>, <see cref="IrUnaryOp"/> or
/// <see cref="IrIntrinsicOp"/>; <c>type</c> the lower-case name of a scalar <see cref="IrTypeKind"/>,
/// <c>bool</c> and <c>i8</c> to <c>f64</c>.
/// </summary>
/// <param name="Operation">The operation's name.</param>
/// <param name="Type">The type's name.</param>
public readonly record struct OperationKey(string Operation, string Type) {

  private static readonly FrozenSet<string> _operations = Enum.GetNames<IrBinaryOp>()
    .Concat(Enum.GetNames<IrUnaryOp>())
    .Concat(Enum.GetNames<IrIntrinsicOp>())
    .ToFrozenSet(StringComparer.Ordinal);

  private static readonly FrozenDictionary<string, int> _bits = new Dictionary<string, int>(StringComparer.Ordinal) {
    [Name(IrTypeKind.Bool)] = 8,
    [Name(IrTypeKind.I8)] = 8,
    [Name(IrTypeKind.U8)] = 8,
    [Name(IrTypeKind.I16)] = 16,
    [Name(IrTypeKind.U16)] = 16,
    [Name(IrTypeKind.I32)] = 32,
    [Name(IrTypeKind.U32)] = 32,
    [Name(IrTypeKind.F32)] = 32,
    [Name(IrTypeKind.I64)] = 64,
    [Name(IrTypeKind.U64)] = 64,
    [Name(IrTypeKind.F64)] = 64
  }.ToFrozenDictionary(StringComparer.Ordinal);

  private static string Name(IrTypeKind kind) => kind.ToString().ToLowerInvariant();

  /// <summary>Whether the IR knows the operation and the type is one of the scalar types.</summary>
  public bool IsKnown => _operations.Contains(this.Operation) && _bits.ContainsKey(this.Type);

  /// <summary>The bits one element of the type takes in a register: 8 for <c>bool</c>, <c>i8</c> and <c>u8</c>, up to 64; 0 for an unknown type.</summary>
  public int ElementBits => _bits.GetValueOrDefault(this.Type);

  /// <summary>Reads <c>Op.type</c>; the type is what follows the last dot.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The key, known to the IR or not.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
  /// <exception cref="FormatException">The text has no dot, or nothing before or after the last one.</exception>
  public static OperationKey Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    return TryParse(text, out var key) ? key : throw new FormatException($"'{text}' is not of the form Op.type");
  }

  /// <summary>Reads <c>Op.type</c> where the text has that form; the type is what follows the last dot.</summary>
  /// <param name="text">The text, or <see langword="null"/>.</param>
  /// <param name="key">The key, known to the IR or not; <see langword="default"/> when the text is not of the form.</param>
  /// <returns><see langword="true"/> when the text has a dot with something before and after it.</returns>
  public static bool TryParse(string? text, out OperationKey key) {
    var dot = text?.LastIndexOf('.') ?? -1;
    if (dot <= 0 || dot == text!.Length - 1) {
      key = default;
      return false;
    }
    key = new(text[..dot], text[(dot + 1)..]);
    return true;
  }

  /// <summary>The key as <c>Op.type</c>.</summary>
  public override string ToString() => $"{this.Operation}.{this.Type}";

}
