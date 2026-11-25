using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection.Metadata;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>
/// Decodes raw ECMA-335 CIL bytecode streams into <see cref="IlInstruction"/> sequences, supporting
/// all 1-byte and <c>0xFE</c> 2-byte opcodes and their exact operand encodings.
/// </summary>
internal static class IlDecoder {

  private enum Encoding : byte {
    Invalid,
    None,
    ShortVar,
    Var,
    ShortI,
    Int32,
    Int64,
    Float32,
    Float64,
    ShortBranch,
    Branch,
    Token,
    Switch
  }

  private static readonly Encoding[] _oneByte = BuildOneByteTable();
  private static readonly Encoding[] _twoByte = BuildTwoByteTable();

  /// <summary>Decodes an IL byte stream into instructions.</summary>
  public static IReadOnlyList<IlInstruction> Decode(ReadOnlySpan<byte> il, string methodName) {
    var result = new List<IlInstruction>();
    var pos = 0;

    while (pos < il.Length) {
      var start = pos;
      var first = il[pos++];
      ILOpCode opCode;
      Encoding encoding;

      if (first == 0xFE) {
        if (pos >= il.Length)
          throw new MsilImportException(MsilFrontend.CorruptMetadata, $"truncated 2-byte opcode at IL_{start:X4} in '{methodName}'");

        var second = il[pos++];
        opCode = (ILOpCode)(0xFE00 | second);
        encoding = second < _twoByte.Length ? _twoByte[second] : Encoding.Invalid;
      } else {
        opCode = (ILOpCode)first;
        encoding = _oneByte[first];
      }

      if (encoding == Encoding.Invalid)
        throw new MsilImportException(MsilFrontend.UnsupportedOpcode, $"unknown IL opcode 0x{(int)opCode:X} at IL_{start:X4} in '{methodName}'");

      switch (encoding) {
        case Encoding.None:
          result.Add(new(start, pos, opCode, IlOperandKind.None));
          break;
        case Encoding.ShortVar:
          RequireBytes(il, pos, 1, start, methodName);
          var u8 = il[pos++];
          result.Add(new(start, pos, opCode, IlOperandKind.Int32, u8));
          break;
        case Encoding.Var:
          RequireBytes(il, pos, 2, start, methodName);
          var u16 = BinaryPrimitives.ReadUInt16LittleEndian(il.Slice(pos, 2));
          pos += 2;
          result.Add(new(start, pos, opCode, IlOperandKind.Int32, u16));
          break;
        case Encoding.ShortI:
          RequireBytes(il, pos, 1, start, methodName);
          var i8 = (sbyte)il[pos++];
          result.Add(new(start, pos, opCode, IlOperandKind.Int32, i8));
          break;
        case Encoding.Int32:
          RequireBytes(il, pos, 4, start, methodName);
          var i32 = BinaryPrimitives.ReadInt32LittleEndian(il.Slice(pos, 4));
          pos += 4;
          result.Add(new(start, pos, opCode, IlOperandKind.Int32, i32));
          break;
        case Encoding.Int64:
          RequireBytes(il, pos, 8, start, methodName);
          var i64 = BinaryPrimitives.ReadInt64LittleEndian(il.Slice(pos, 8));
          pos += 8;
          result.Add(new(start, pos, opCode, IlOperandKind.Int64, i64));
          break;
        case Encoding.Float32:
          RequireBytes(il, pos, 4, start, methodName);
          var f32 = BinaryPrimitives.ReadSingleLittleEndian(il.Slice(pos, 4));
          pos += 4;
          result.Add(new(start, pos, opCode, IlOperandKind.Float32, FloatValue: f32));
          break;
        case Encoding.Float64:
          RequireBytes(il, pos, 8, start, methodName);
          var f64 = BinaryPrimitives.ReadDoubleLittleEndian(il.Slice(pos, 8));
          pos += 8;
          result.Add(new(start, pos, opCode, IlOperandKind.Float64, FloatValue: f64));
          break;
        case Encoding.ShortBranch:
          RequireBytes(il, pos, 1, start, methodName);
          var shortDelta = (sbyte)il[pos++];
          result.Add(new(start, pos, opCode, IlOperandKind.BranchTarget, pos + shortDelta));
          break;
        case Encoding.Branch:
          RequireBytes(il, pos, 4, start, methodName);
          var longDelta = BinaryPrimitives.ReadInt32LittleEndian(il.Slice(pos, 4));
          pos += 4;
          result.Add(new(start, pos, opCode, IlOperandKind.BranchTarget, pos + longDelta));
          break;
        case Encoding.Token:
          RequireBytes(il, pos, 4, start, methodName);
          var token = BinaryPrimitives.ReadInt32LittleEndian(il.Slice(pos, 4));
          pos += 4;
          result.Add(new(start, pos, opCode, IlOperandKind.Token, Token: token));
          break;
        default: {
          RequireBytes(il, pos, 4, start, methodName);
          var count = BinaryPrimitives.ReadUInt32LittleEndian(il.Slice(pos, 4));
          pos += 4;
          if (count > (uint)((il.Length - pos) / 4))
            throw new MsilImportException(MsilFrontend.CorruptMetadata, $"truncated switch table at IL_{start:X4} in '{methodName}'");

          var next = pos + (int)(count * 4);
          var targets = new int[count];
          for (var i = 0; i < targets.Length; ++i) {
            var delta = BinaryPrimitives.ReadInt32LittleEndian(il.Slice(pos, 4));
            pos += 4;
            targets[i] = next + delta;
          }

          result.Add(new(start, pos, opCode, IlOperandKind.Switch, SwitchTargets: targets));
          break;
        }
      }
    }

    return result;
  }

  private static void RequireBytes(ReadOnlySpan<byte> il, int pos, int needed, int instructionOffset, string methodName) {
    if (pos + needed > il.Length)
      throw new MsilImportException(MsilFrontend.CorruptMetadata, $"truncated operand at IL_{instructionOffset:X4} in '{methodName}'");
  }

  private static Encoding[] BuildOneByteTable() {
    var t = new Encoding[256];
    Fill(t, 0x00, 0x0D, Encoding.None);
    Fill(t, 0x0E, 0x13, Encoding.ShortVar);
    Fill(t, 0x14, 0x1E, Encoding.None);
    t[0x1F] = Encoding.ShortI;
    t[0x20] = Encoding.Int32;
    t[0x21] = Encoding.Int64;
    t[0x22] = Encoding.Float32;
    t[0x23] = Encoding.Float64;
    t[0x25] = t[0x26] = t[0x2A] = Encoding.None;
    Fill(t, 0x27, 0x29, Encoding.Token);
    Fill(t, 0x2B, 0x37, Encoding.ShortBranch);
    Fill(t, 0x38, 0x44, Encoding.Branch);
    t[0x45] = Encoding.Switch;
    Fill(t, 0x46, 0x6E, Encoding.None);
    Fill(t, 0x6F, 0x75, Encoding.Token);
    t[0x76] = t[0x7A] = Encoding.None;
    t[0x79] = Encoding.Token;
    Fill(t, 0x7B, 0x81, Encoding.Token);
    Fill(t, 0x82, 0x8B, Encoding.None);
    t[0x8C] = t[0x8D] = t[0x8F] = Encoding.Token;
    t[0x8E] = Encoding.None;
    Fill(t, 0x90, 0xA2, Encoding.None);
    Fill(t, 0xA3, 0xA5, Encoding.Token);
    Fill(t, 0xB3, 0xBA, Encoding.None);
    t[0xC2] = t[0xC6] = t[0xD0] = Encoding.Token;
    t[0xC3] = Encoding.None;
    Fill(t, 0xD1, 0xDC, Encoding.None);
    t[0xDD] = Encoding.Branch;
    t[0xDE] = Encoding.ShortBranch;
    t[0xDF] = t[0xE0] = Encoding.None;
    return t;
  }

  private static Encoding[] BuildTwoByteTable() {
    var t = new Encoding[0x1F];
    Fill(t, 0x00, 0x05, Encoding.None);
    t[0x06] = t[0x07] = Encoding.Token;
    Fill(t, 0x09, 0x0E, Encoding.Var);
    t[0x0F] = t[0x11] = Encoding.None;
    t[0x12] = Encoding.ShortVar;
    t[0x13] = t[0x14] = Encoding.None;
    t[0x15] = t[0x16] = t[0x1C] = Encoding.Token;
    t[0x17] = t[0x18] = t[0x1A] = t[0x1D] = t[0x1E] = Encoding.None;
    return t;
  }

  private static void Fill(Encoding[] table, int first, int last, Encoding value) {
    for (var i = first; i <= last; ++i)
      table[i] = value;
  }

}
