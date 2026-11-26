using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Jvm.Bytecode;

internal static class JvmOpcodes {

  public const byte Nop = 0x00;
  public const byte AconstNull = 0x01;
  public const byte IconstM1 = 0x02;
  public const byte Iconst0 = 0x03;
  public const byte Iconst5 = 0x08;
  public const byte Lconst0 = 0x09;
  public const byte Lconst1 = 0x0A;
  public const byte Fconst0 = 0x0B;
  public const byte Fconst2 = 0x0D;
  public const byte Dconst0 = 0x0E;
  public const byte Dconst1 = 0x0F;
  public const byte Bipush = 0x10;
  public const byte Sipush = 0x11;
  public const byte Ldc = 0x12;
  public const byte LdcW = 0x13;
  public const byte Ldc2W = 0x14;
  public const byte Iload = 0x15;
  public const byte Lload = 0x16;
  public const byte Fload = 0x17;
  public const byte Dload = 0x18;
  public const byte Aload = 0x19;
  public const byte Iload0 = 0x1A;
  public const byte Iload3 = 0x1D;
  public const byte Lload0 = 0x1E;
  public const byte Lload3 = 0x21;
  public const byte Fload0 = 0x22;
  public const byte Fload3 = 0x25;
  public const byte Dload0 = 0x26;
  public const byte Dload3 = 0x29;
  public const byte Aload0 = 0x2A;
  public const byte Aload3 = 0x2D;
  public const byte Iaload = 0x2E;
  public const byte Laload = 0x2F;
  public const byte Faload = 0x30;
  public const byte Daload = 0x31;
  public const byte Aaload = 0x32;
  public const byte Baload = 0x33;
  public const byte Caload = 0x34;
  public const byte Saload = 0x35;
  public const byte Istore = 0x36;
  public const byte Lstore = 0x37;
  public const byte Fstore = 0x38;
  public const byte Dstore = 0x39;
  public const byte Astore = 0x3A;
  public const byte Istore0 = 0x3B;
  public const byte Istore3 = 0x3E;
  public const byte Lstore0 = 0x3F;
  public const byte Lstore3 = 0x42;
  public const byte Fstore0 = 0x43;
  public const byte Fstore3 = 0x46;
  public const byte Dstore0 = 0x47;
  public const byte Dstore3 = 0x4A;
  public const byte Astore0 = 0x4B;
  public const byte Astore3 = 0x4E;
  public const byte Iastore = 0x4F;
  public const byte Bastore = 0x54;
  public const byte Sastore = 0x56;
  public const byte Pop = 0x57;
  public const byte Pop2 = 0x58;
  public const byte Dup = 0x59;
  public const byte DupX1 = 0x5A;
  public const byte DupX2 = 0x5B;
  public const byte Dup2 = 0x5C;
  public const byte Dup2X1 = 0x5D;
  public const byte Dup2X2 = 0x5E;
  public const byte Swap = 0x5F;
  public const byte Iadd = 0x60;
  public const byte Ladd = 0x61;
  public const byte Fadd = 0x62;
  public const byte Dadd = 0x63;
  public const byte Isub = 0x64;
  public const byte Lsub = 0x65;
  public const byte Fsub = 0x66;
  public const byte Dsub = 0x67;
  public const byte Imul = 0x68;
  public const byte Lmul = 0x69;
  public const byte Fmul = 0x6A;
  public const byte Dmul = 0x6B;
  public const byte Idiv = 0x6C;
  public const byte Ldiv = 0x6D;
  public const byte Fdiv = 0x6E;
  public const byte Ddiv = 0x6F;
  public const byte Irem = 0x70;
  public const byte Lrem = 0x71;
  public const byte Frem = 0x72;
  public const byte Drem = 0x73;
  public const byte Ineg = 0x74;
  public const byte Lneg = 0x75;
  public const byte Fneg = 0x76;
  public const byte Dneg = 0x77;
  public const byte Ishl = 0x78;
  public const byte Lshl = 0x79;
  public const byte Ishr = 0x7A;
  public const byte Lshr = 0x7B;
  public const byte Iushr = 0x7C;
  public const byte Lushr = 0x7D;
  public const byte Iand = 0x7E;
  public const byte Land = 0x7F;
  public const byte Ior = 0x80;
  public const byte Lor = 0x81;
  public const byte Ixor = 0x82;
  public const byte Lxor = 0x83;
  public const byte Iinc = 0x84;
  public const byte I2l = 0x85;
  public const byte I2f = 0x86;
  public const byte I2d = 0x87;
  public const byte L2i = 0x88;
  public const byte L2f = 0x89;
  public const byte L2d = 0x8A;
  public const byte F2i = 0x8B;
  public const byte F2l = 0x8C;
  public const byte F2d = 0x8D;
  public const byte D2i = 0x8E;
  public const byte D2l = 0x8F;
  public const byte D2f = 0x90;
  public const byte I2b = 0x91;
  public const byte I2c = 0x92;
  public const byte I2s = 0x93;
  public const byte Lcmp = 0x94;
  public const byte Fcmpl = 0x95;
  public const byte Fcmpg = 0x96;
  public const byte Dcmpl = 0x97;
  public const byte Dcmpg = 0x98;
  public const byte Ifeq = 0x99;
  public const byte Ifne = 0x9A;
  public const byte Iflt = 0x9B;
  public const byte Ifge = 0x9C;
  public const byte Ifgt = 0x9D;
  public const byte Ifle = 0x9E;
  public const byte IfIcmpeq = 0x9F;
  public const byte IfIcmpne = 0xA0;
  public const byte IfIcmplt = 0xA1;
  public const byte IfIcmpge = 0xA2;
  public const byte IfIcmpgt = 0xA3;
  public const byte IfIcmple = 0xA4;
  public const byte IfAcmpeq = 0xA5;
  public const byte IfAcmpne = 0xA6;
  public const byte Goto = 0xA7;
  public const byte Tableswitch = 0xAA;
  public const byte Lookupswitch = 0xAB;
  public const byte Ireturn = 0xAC;
  public const byte Areturn = 0xB0;
  public const byte Return = 0xB1;
  public const byte Getstatic = 0xB2;
  public const byte Putstatic = 0xB3;
  public const byte Getfield = 0xB4;
  public const byte Putfield = 0xB5;
  public const byte Invokevirtual = 0xB6;
  public const byte Invokespecial = 0xB7;
  public const byte Invokestatic = 0xB8;
  public const byte Invokeinterface = 0xB9;
  public const byte Invokedynamic = 0xBA;
  public const byte New = 0xBB;
  public const byte Newarray = 0xBC;
  public const byte Anewarray = 0xBD;
  public const byte Arraylength = 0xBE;
  public const byte Athrow = 0xBF;
  public const byte Checkcast = 0xC0;
  public const byte Instanceof = 0xC1;
  public const byte Wide = 0xC4;
  public const byte Multianewarray = 0xC5;
  public const byte Ifnull = 0xC6;
  public const byte Ifnonnull = 0xC7;
  public const byte GotoW = 0xC8;

}

internal sealed record JvmInstruction(
  int Offset,
  int NextOffset,
  byte Opcode,
  int Operand1 = 0,
  int Operand2 = 0,
  int[]? SwitchKeys = null,
  int[]? SwitchTargets = null);

internal static class JvmBytecodeDecoder {

  public static IReadOnlyList<JvmInstruction>? Decode(byte[] code, string sourceName, List<Diagnostic> diagnostics) {
    try {
      var instructions = new List<JvmInstruction>();
      var pc = 0;
      while (pc < code.Length) {
        var start = pc;
        var opcode = code[pc++];
        if (opcode == JvmOpcodes.Wide) {
          Ensure(code, pc, 3);
          var inner = code[pc++];
          var index = ReadU2(code, ref pc);
          if (inner == JvmOpcodes.Iinc) {
            Ensure(code, pc, 2);
            var delta = ReadI2(code, ref pc);
            instructions.Add(new(start, pc, inner, index, delta));
          } else if (inner is >= JvmOpcodes.Iload and <= JvmOpcodes.Aload or >= JvmOpcodes.Istore and <= JvmOpcodes.Astore) {
            instructions.Add(new(start, pc, inner, index));
          } else {
            throw new DecodeException(JvmFrontend.UnsupportedOpcode, $"unsupported wide opcode 0x{inner:X2} at offset {start} in '{sourceName}'");
          }

          continue;
        }

        switch (opcode) {
          case JvmOpcodes.Bipush:
            Ensure(code, pc, 1);
            instructions.Add(new(start, pc + 1, opcode, unchecked((sbyte)code[pc])));
            pc += 1;
            break;
          case JvmOpcodes.Ldc:
          case >= JvmOpcodes.Iload and <= JvmOpcodes.Aload:
          case >= JvmOpcodes.Istore and <= JvmOpcodes.Astore:
          case JvmOpcodes.Newarray:
            Ensure(code, pc, 1);
            instructions.Add(new(start, pc + 1, opcode, code[pc]));
            pc += 1;
            break;
          case JvmOpcodes.Sipush:
            Ensure(code, pc, 2);
            instructions.Add(new(start, pc + 2, opcode, ReadI2(code, ref pc)));
            break;
          case JvmOpcodes.Iinc:
            Ensure(code, pc, 2);
            instructions.Add(new(start, pc + 2, opcode, code[pc], unchecked((sbyte)code[pc + 1])));
            pc += 2;
            break;
          case JvmOpcodes.LdcW:
          case JvmOpcodes.Ldc2W:
          case >= JvmOpcodes.Getstatic and <= JvmOpcodes.Invokestatic:
          case JvmOpcodes.New:
          case JvmOpcodes.Anewarray:
          case JvmOpcodes.Checkcast:
          case JvmOpcodes.Instanceof:
            Ensure(code, pc, 2);
            instructions.Add(new(start, pc + 2, opcode, ReadU2(code, ref pc)));
            break;
          case >= JvmOpcodes.Ifeq and <= JvmOpcodes.Goto:
          case JvmOpcodes.Ifnull:
          case JvmOpcodes.Ifnonnull: {
            Ensure(code, pc, 2);
            var target = start + ReadI2(code, ref pc);
            instructions.Add(new(start, pc, opcode, target));
            break;
          }
          case JvmOpcodes.GotoW: {
            Ensure(code, pc, 4);
            var target = start + ReadI4(code, ref pc);
            instructions.Add(new(start, pc, opcode, target));
            break;
          }
          case JvmOpcodes.Invokeinterface:
          case JvmOpcodes.Invokedynamic: {
            Ensure(code, pc, 4);
            var index = ReadU2(code, ref pc);
            pc += 2;
            instructions.Add(new(start, pc, opcode, index));
            break;
          }
          case JvmOpcodes.Multianewarray: {
            Ensure(code, pc, 3);
            var index = ReadU2(code, ref pc);
            var dims = code[pc++];
            instructions.Add(new(start, pc, opcode, index, dims));
            break;
          }
          case JvmOpcodes.Tableswitch: {
            pc += (4 - (pc & 3)) & 3;
            Ensure(code, pc, 12);
            var defaultTarget = start + ReadI4(code, ref pc);
            var low = ReadI4(code, ref pc);
            var high = ReadI4(code, ref pc);
            if (high < low || (long)high - low + 1 > (code.Length - pc) / 4)
              throw new DecodeException(JvmFrontend.CorruptClassFile, $"invalid tableswitch bounds [{low}..{high}] at offset {start} in '{sourceName}'");

            var count = high - low + 1;
            var keys = new int[count];
            var targets = new int[count];
            for (var i = 0; i < count; ++i) {
              keys[i] = low + i;
              targets[i] = start + ReadI4(code, ref pc);
            }

            instructions.Add(new(start, pc, opcode, defaultTarget, 0, keys, targets));
            break;
          }
          case JvmOpcodes.Lookupswitch: {
            pc += (4 - (pc & 3)) & 3;
            Ensure(code, pc, 8);
            var defaultTarget = start + ReadI4(code, ref pc);
            var npairs = ReadI4(code, ref pc);
            if (npairs < 0 || (long)npairs * 8 > code.Length - pc)
              throw new DecodeException(JvmFrontend.CorruptClassFile, $"invalid lookupswitch pair count {npairs} at offset {start} in '{sourceName}'");

            var keys = new int[npairs];
            var targets = new int[npairs];
            for (var i = 0; i < npairs; ++i) {
              keys[i] = ReadI4(code, ref pc);
              targets[i] = start + ReadI4(code, ref pc);
            }

            instructions.Add(new(start, pc, opcode, defaultTarget, 0, keys, targets));
            break;
          }
          default:
            instructions.Add(new(start, pc, opcode));
            break;
        }
      }

      return instructions;
    } catch (DecodeException ex) {
      diagnostics.Add(ex.Descriptor.At(ex.Message, sourceName));
      return null;
    }
  }

  private static ushort ReadU2(byte[] code, ref int pc) {
    var value = BinaryPrimitives.ReadUInt16BigEndian(code.AsSpan(pc, 2));
    pc += 2;
    return value;
  }

  private static short ReadI2(byte[] code, ref int pc) {
    var value = BinaryPrimitives.ReadInt16BigEndian(code.AsSpan(pc, 2));
    pc += 2;
    return value;
  }

  private static int ReadI4(byte[] code, ref int pc) {
    var value = BinaryPrimitives.ReadInt32BigEndian(code.AsSpan(pc, 4));
    pc += 4;
    return value;
  }

  private static void Ensure(byte[] code, int offset, int count) {
    if (offset + count > code.Length)
      throw new DecodeException(JvmFrontend.CorruptClassFile, $"truncated bytecode instruction at offset {offset}");
  }

  private sealed class DecodeException(DiagnosticDescriptor descriptor, string message) : Exception(message) {

    public DiagnosticDescriptor Descriptor { get; } = descriptor;

  }

}
