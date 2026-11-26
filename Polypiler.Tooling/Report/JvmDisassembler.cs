using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Polypiler.Tooling.Report;

/// <summary>
/// Disassembles JVM <c>.jar</c> archives and <c>.class</c> files emitted by the JVM backend into
/// deterministic, <c>javap -c</c>-style readable bytecode mnemonics.
/// </summary>
public static class JvmDisassembler {

  private enum OperandKind : byte {
    None,
    SignedByte,
    UnsignedByte,
    SignedShort,
    CpByte,
    CpShort,
    Branch2,
    Branch4,
    NewArray,
    Iinc,
    TableSwitch,
    LookupSwitch,
    InvokeInterface
  }

  private readonly record struct OpSpec(string Name, OperandKind Kind);
  private readonly record struct CpRef(byte Tag, int First, int Second);

  private static readonly OpSpec?[] _opcodes = BuildOpcodes();

  /// <summary>Disassembles every <c>.class</c> entry in a JAR archive in deterministic path order.</summary>
  /// <param name="bytes">The JAR archive bytes.</param>
  /// <returns>Readable JVM bytecode disassembly with LF line endings.</returns>
  public static string DisassembleJar(byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    using var stream = new MemoryStream(bytes, writable: false);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    var classes = archive.Entries
      .Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal))
      .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
      .ToList();

    var parts = new List<string>(classes.Count);
    foreach (var entry in classes) {
      using var entryStream = entry.Open();
      using var copy = new MemoryStream();
      entryStream.CopyTo(copy);
      parts.Add(DisassembleClass(copy.ToArray()).TrimEnd());
    }

    return string.Join("\n\n", parts) + "\n";
  }

  /// <summary>Disassembles a single JVMS Chapter 4 <c>.class</c> file into readable mnemonics.</summary>
  /// <param name="bytes">The classfile bytes.</param>
  /// <returns>Readable JVM bytecode disassembly with LF line endings.</returns>
  public static string DisassembleClass(byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    var span = bytes.AsSpan();
    var pos = 0;
    var magic = ReadU4(span, ref pos);
    if (magic != 0xCAFEBABEu)
      throw new InvalidDataException($"invalid classfile magic 0x{magic:X8}");

    var minor = ReadU2(span, ref pos);
    var major = ReadU2(span, ref pos);
    var pool = ReadConstantPool(span, ref pos);
    var accessFlags = ReadU2(span, ref pos);
    var thisClass = ResolveClass(pool, ReadU2(span, ref pos));
    var superClass = ResolveClass(pool, ReadU2(span, ref pos));

    var interfaceCount = ReadU2(span, ref pos);
    var interfaces = new string[interfaceCount];
    for (var i = 0; i < interfaceCount; ++i)
      interfaces[i] = ResolveClass(pool, ReadU2(span, ref pos));

    var output = new StringBuilder();
    output.Append("// classfile version ").Append(major).Append('.').AppendLine(minor.ToString(CultureInfo.InvariantCulture));
    output.Append(FormatClassFlags(accessFlags)).Append("class ").Append(thisClass);
    if (superClass != "java/lang/Object" && superClass.Length > 0)
      output.Append(" extends ").Append(superClass);
    if (interfaces.Length > 0)
      output.Append(" implements ").Append(string.Join(", ", interfaces));
    output.AppendLine(" {");

    var fieldCount = ReadU2(span, ref pos);
    for (var i = 0; i < fieldCount; ++i) {
      var flags = ReadU2(span, ref pos);
      var name = ResolveUtf8(pool, ReadU2(span, ref pos));
      var desc = ResolveUtf8(pool, ReadU2(span, ref pos));
      SkipAttributes(span, ref pos);
      output.Append("  ").Append(FormatMemberFlags(flags)).Append(desc).Append(' ').Append(name).AppendLine(";");
    }

    var methodCount = ReadU2(span, ref pos);
    for (var i = 0; i < methodCount; ++i) {
      var flags = ReadU2(span, ref pos);
      var name = ResolveUtf8(pool, ReadU2(span, ref pos));
      var desc = ResolveUtf8(pool, ReadU2(span, ref pos));
      output.Append("  ").Append(FormatMemberFlags(flags)).Append(name).Append(desc).AppendLine(";");

      var attrCount = ReadU2(span, ref pos);
      for (var a = 0; a < attrCount; ++a) {
        var attrName = ResolveUtf8(pool, ReadU2(span, ref pos));
        var attrLen = (int)ReadU4(span, ref pos);
        var attrStart = pos;
        if (attrName == "Code")
          WriteCodeAttribute(span.Slice(attrStart, attrLen), pool, output);
        pos = attrStart + attrLen;
      }
    }

    output.AppendLine("}");
    return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
  }

  private static void WriteCodeAttribute(ReadOnlySpan<byte> attr, object?[] pool, StringBuilder output) {
    var pos = 0;
    var maxStack = ReadU2(attr, ref pos);
    var maxLocals = ReadU2(attr, ref pos);
    var codeLen = (int)ReadU4(attr, ref pos);
    var code = attr.Slice(pos, codeLen);
    output.Append("    Code: stack=").Append(maxStack).Append(", locals=").AppendLine(maxLocals.ToString(CultureInfo.InvariantCulture));

    var pc = 0;
    while (pc < code.Length) {
      var start = pc;
      var op = code[pc++];
      if (_opcodes[op] is not { } spec) {
        output.Append("      ").Append(start).Append(": .byte 0x").AppendLine(op.ToString("X2", CultureInfo.InvariantCulture));
        continue;
      }

      var operand = ReadOperand(code, ref pc, start, spec.Kind, pool);
      output.Append("      ").Append(start).Append(": ").Append(spec.Name);
      if (operand.Length > 0)
        output.Append(' ').Append(operand);
      output.AppendLine();
    }
  }

  private static string ReadOperand(ReadOnlySpan<byte> code, ref int pc, int start, OperandKind kind, object?[] pool) {
    switch (kind) {
      case OperandKind.SignedByte:
        return ((sbyte)code[pc++]).ToString(CultureInfo.InvariantCulture);
      case OperandKind.UnsignedByte:
        return code[pc++].ToString(CultureInfo.InvariantCulture);
      case OperandKind.SignedShort:
        return ((short)ReadU2(code, ref pc)).ToString(CultureInfo.InvariantCulture);
      case OperandKind.CpByte:
        return FormatPoolEntry(pool, code[pc++]);
      case OperandKind.CpShort:
        return FormatPoolEntry(pool, ReadU2(code, ref pc));
      case OperandKind.Branch2:
        return (start + (short)ReadU2(code, ref pc)).ToString(CultureInfo.InvariantCulture);
      case OperandKind.Branch4:
        return (start + (int)ReadU4(code, ref pc)).ToString(CultureInfo.InvariantCulture);
      case OperandKind.NewArray:
        return code[pc++] switch {
          4 => "boolean",
          5 => "char",
          6 => "float",
          7 => "double",
          8 => "byte",
          9 => "short",
          10 => "int",
          11 => "long",
          var t => t.ToString(CultureInfo.InvariantCulture)
        };
      case OperandKind.Iinc: {
        var index = code[pc++];
        var delta = (sbyte)code[pc++];
        return $"{index}, {delta}";
      }
      case OperandKind.InvokeInterface: {
        var cpIndex = ReadU2(code, ref pc);
        pc += 2;
        return FormatPoolEntry(pool, cpIndex);
      }
      case OperandKind.TableSwitch: {
        pc = (pc + 3) & ~3;
        var def = start + (int)ReadU4(code, ref pc);
        var low = (int)ReadU4(code, ref pc);
        var high = (int)ReadU4(code, ref pc);
        var arms = new List<string>();
        for (var v = low; v <= high; ++v)
          arms.Add($"{v}->{start + (int)ReadU4(code, ref pc)}");
        arms.Add($"default->{def}");
        return $"{{ {string.Join(", ", arms)} }}";
      }
      case OperandKind.LookupSwitch: {
        pc = (pc + 3) & ~3;
        var def = start + (int)ReadU4(code, ref pc);
        var count = (int)ReadU4(code, ref pc);
        var arms = new List<string>(count + 1);
        for (var i = 0; i < count; ++i) {
          var key = (int)ReadU4(code, ref pc);
          var target = start + (int)ReadU4(code, ref pc);
          arms.Add($"{key}->{target}");
        }
        arms.Add($"default->{def}");
        return $"{{ {string.Join(", ", arms)} }}";
      }
      case OperandKind.None:
      default:
        return "";
    }
  }

  private static object?[] ReadConstantPool(ReadOnlySpan<byte> span, ref int pos) {
    var count = ReadU2(span, ref pos);
    var pool = new object?[count];
    for (var i = 1; i < count; ++i) {
      var tag = span[pos++];
      switch (tag) {
        case 1: {
          var len = ReadU2(span, ref pos);
          pool[i] = Encoding.UTF8.GetString(span.Slice(pos, len));
          pos += len;
          break;
        }
        case 3:
          pool[i] = (int)ReadU4(span, ref pos);
          break;
        case 4:
          pool[i] = BitConverter.Int32BitsToSingle((int)ReadU4(span, ref pos));
          break;
        case 5:
          pool[i] = (long)ReadU8(span, ref pos);
          ++i;
          break;
        case 6:
          pool[i] = BitConverter.Int64BitsToDouble((long)ReadU8(span, ref pos));
          ++i;
          break;
        case 7:
        case 8:
        case 16:
          pool[i] = new CpRef(tag, ReadU2(span, ref pos), 0);
          break;
        case 9:
        case 10:
        case 11:
        case 12:
        case 18:
          pool[i] = new CpRef(tag, ReadU2(span, ref pos), ReadU2(span, ref pos));
          break;
        case 15:
          pool[i] = new CpRef(tag, span[pos++], ReadU2(span, ref pos));
          break;
        default:
          throw new InvalidDataException($"unsupported constant pool tag {tag}");
      }
    }
    return pool;
  }

  private static string ResolveUtf8(object?[] pool, int index)
    => index > 0 && index < pool.Length && pool[index] is string s ? s : $"#{index}";

  private static string ResolveClass(object?[] pool, int index)
    => index > 0 && index < pool.Length && pool[index] is CpRef(7, var nameIndex, _) ? ResolveUtf8(pool, nameIndex) : $"#{index}";

  private static string FormatPoolEntry(object?[] pool, int index) {
    if (index <= 0 || index >= pool.Length)
      return $"#{index}";

    return pool[index] switch {
      int i => i.ToString(CultureInfo.InvariantCulture),
      long l => l.ToString(CultureInfo.InvariantCulture) + "L",
      float f => f.ToString("G9", CultureInfo.InvariantCulture) + "f",
      double d => d.ToString("G17", CultureInfo.InvariantCulture) + "d",
      CpRef(7, var nameIndex, _) => $"// class {ResolveUtf8(pool, nameIndex)}",
      CpRef(8, var utf8Index, _) => $"\"{ResolveUtf8(pool, utf8Index)}\"",
      CpRef(9, var clsIndex, var ntIndex) => $"// Field {ResolveClass(pool, clsIndex)}.{ResolveNameAndType(pool, ntIndex)}",
      CpRef(10 or 11, var clsIndex, var ntIndex) => $"// Method {ResolveClass(pool, clsIndex)}.{ResolveNameAndType(pool, ntIndex)}",
      _ => $"#{index}"
    };
  }

  private static string ResolveNameAndType(object?[] pool, int index)
    => index > 0 && index < pool.Length && pool[index] is CpRef(12, var nameIndex, var descIndex)
      ? $"{ResolveUtf8(pool, nameIndex)}:{ResolveUtf8(pool, descIndex)}"
      : $"#{index}";

  private static void SkipAttributes(ReadOnlySpan<byte> span, ref int pos) {
    var count = ReadU2(span, ref pos);
    for (var i = 0; i < count; ++i) {
      pos += 2;
      var len = (int)ReadU4(span, ref pos);
      pos += len;
    }
  }

  private static string FormatClassFlags(ushort flags) {
    var sb = new StringBuilder();
    if ((flags & 0x0001) != 0)
      sb.Append("public ");
    if ((flags & 0x0010) != 0)
      sb.Append("final ");
    return sb.ToString();
  }

  private static string FormatMemberFlags(ushort flags) {
    var sb = new StringBuilder();
    if ((flags & 0x0001) != 0)
      sb.Append("public ");
    else if ((flags & 0x0002) != 0)
      sb.Append("private ");
    if ((flags & 0x0008) != 0)
      sb.Append("static ");
    if ((flags & 0x0010) != 0)
      sb.Append("final ");
    return sb.ToString();
  }

  private static ushort ReadU2(ReadOnlySpan<byte> span, ref int pos) {
    var v = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pos, 2));
    pos += 2;
    return v;
  }

  private static uint ReadU4(ReadOnlySpan<byte> span, ref int pos) {
    var v = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos, 4));
    pos += 4;
    return v;
  }

  private static ulong ReadU8(ReadOnlySpan<byte> span, ref int pos) {
    var v = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(pos, 8));
    pos += 8;
    return v;
  }

  private static OpSpec?[] BuildOpcodes() {
    var ops = new OpSpec?[256];
    void Add(byte code, string name, OperandKind kind = OperandKind.None) => ops[code] = new(name, kind);

    Add(0x00, "nop");
    Add(0x01, "aconst_null");
    Add(0x02, "iconst_m1");
    Add(0x03, "iconst_0");
    Add(0x04, "iconst_1");
    Add(0x05, "iconst_2");
    Add(0x06, "iconst_3");
    Add(0x07, "iconst_4");
    Add(0x08, "iconst_5");
    Add(0x09, "lconst_0");
    Add(0x0A, "lconst_1");
    Add(0x0B, "fconst_0");
    Add(0x0C, "fconst_1");
    Add(0x0D, "fconst_2");
    Add(0x0E, "dconst_0");
    Add(0x0F, "dconst_1");
    Add(0x10, "bipush", OperandKind.SignedByte);
    Add(0x11, "sipush", OperandKind.SignedShort);
    Add(0x12, "ldc", OperandKind.CpByte);
    Add(0x13, "ldc_w", OperandKind.CpShort);
    Add(0x14, "ldc2_w", OperandKind.CpShort);
    Add(0x15, "iload", OperandKind.UnsignedByte);
    Add(0x16, "lload", OperandKind.UnsignedByte);
    Add(0x17, "fload", OperandKind.UnsignedByte);
    Add(0x18, "dload", OperandKind.UnsignedByte);
    Add(0x19, "aload", OperandKind.UnsignedByte);
    Add(0x1A, "iload_0");
    Add(0x1B, "iload_1");
    Add(0x1C, "iload_2");
    Add(0x1D, "iload_3");
    Add(0x1E, "lload_0");
    Add(0x1F, "lload_1");
    Add(0x20, "lload_2");
    Add(0x21, "lload_3");
    Add(0x22, "fload_0");
    Add(0x23, "fload_1");
    Add(0x24, "fload_2");
    Add(0x25, "fload_3");
    Add(0x26, "dload_0");
    Add(0x27, "dload_1");
    Add(0x28, "dload_2");
    Add(0x29, "dload_3");
    Add(0x2A, "aload_0");
    Add(0x2B, "aload_1");
    Add(0x2C, "aload_2");
    Add(0x2D, "aload_3");
    Add(0x2E, "iaload");
    Add(0x2F, "laload");
    Add(0x30, "faload");
    Add(0x31, "daload");
    Add(0x32, "aaload");
    Add(0x33, "baload");
    Add(0x34, "caload");
    Add(0x35, "saload");
    Add(0x36, "istore", OperandKind.UnsignedByte);
    Add(0x37, "lstore", OperandKind.UnsignedByte);
    Add(0x38, "fstore", OperandKind.UnsignedByte);
    Add(0x39, "dstore", OperandKind.UnsignedByte);
    Add(0x3A, "astore", OperandKind.UnsignedByte);
    Add(0x3B, "istore_0");
    Add(0x3C, "istore_1");
    Add(0x3D, "istore_2");
    Add(0x3E, "istore_3");
    Add(0x3F, "lstore_0");
    Add(0x40, "lstore_1");
    Add(0x41, "lstore_2");
    Add(0x42, "lstore_3");
    Add(0x43, "fstore_0");
    Add(0x44, "fstore_1");
    Add(0x45, "fstore_2");
    Add(0x46, "fstore_3");
    Add(0x47, "dstore_0");
    Add(0x48, "dstore_1");
    Add(0x49, "dstore_2");
    Add(0x4A, "dstore_3");
    Add(0x4B, "astore_0");
    Add(0x4C, "astore_1");
    Add(0x4D, "astore_2");
    Add(0x4E, "astore_3");
    Add(0x4F, "iastore");
    Add(0x50, "lastore");
    Add(0x51, "fastore");
    Add(0x52, "dastore");
    Add(0x53, "aastore");
    Add(0x54, "bastore");
    Add(0x55, "castore");
    Add(0x56, "sastore");
    Add(0x57, "pop");
    Add(0x58, "pop2");
    Add(0x59, "dup");
    Add(0x5A, "dup_x1");
    Add(0x5B, "dup_x2");
    Add(0x5C, "dup2");
    Add(0x5D, "dup2_x1");
    Add(0x5E, "dup2_x2");
    Add(0x5F, "swap");
    Add(0x60, "iadd");
    Add(0x61, "ladd");
    Add(0x62, "fadd");
    Add(0x63, "dadd");
    Add(0x64, "isub");
    Add(0x65, "lsub");
    Add(0x66, "fsub");
    Add(0x67, "dsub");
    Add(0x68, "imul");
    Add(0x69, "lmul");
    Add(0x6A, "fmul");
    Add(0x6B, "dmul");
    Add(0x6C, "idiv");
    Add(0x6D, "ldiv");
    Add(0x6E, "fdiv");
    Add(0x6F, "ddiv");
    Add(0x70, "irem");
    Add(0x71, "lrem");
    Add(0x72, "frem");
    Add(0x73, "drem");
    Add(0x74, "ineg");
    Add(0x75, "lneg");
    Add(0x76, "fneg");
    Add(0x77, "dneg");
    Add(0x78, "ishl");
    Add(0x79, "lshl");
    Add(0x7A, "ishr");
    Add(0x7B, "lshr");
    Add(0x7C, "iushr");
    Add(0x7D, "lushr");
    Add(0x7E, "iand");
    Add(0x7F, "land");
    Add(0x80, "ior");
    Add(0x81, "lor");
    Add(0x82, "ixor");
    Add(0x83, "lxor");
    Add(0x84, "iinc", OperandKind.Iinc);
    Add(0x85, "i2l");
    Add(0x86, "i2f");
    Add(0x87, "i2d");
    Add(0x88, "l2i");
    Add(0x89, "l2f");
    Add(0x8A, "l2d");
    Add(0x8B, "f2i");
    Add(0x8C, "f2l");
    Add(0x8D, "f2d");
    Add(0x8E, "d2i");
    Add(0x8F, "d2l");
    Add(0x90, "d2f");
    Add(0x91, "i2b");
    Add(0x92, "i2c");
    Add(0x93, "i2s");
    Add(0x94, "lcmp");
    Add(0x95, "fcmpl");
    Add(0x96, "fcmpg");
    Add(0x97, "dcmpl");
    Add(0x98, "dcmpg");
    Add(0x99, "ifeq", OperandKind.Branch2);
    Add(0x9A, "ifne", OperandKind.Branch2);
    Add(0x9B, "iflt", OperandKind.Branch2);
    Add(0x9C, "ifge", OperandKind.Branch2);
    Add(0x9D, "ifgt", OperandKind.Branch2);
    Add(0x9E, "ifle", OperandKind.Branch2);
    Add(0x9F, "if_icmpeq", OperandKind.Branch2);
    Add(0xA0, "if_icmpne", OperandKind.Branch2);
    Add(0xA1, "if_icmplt", OperandKind.Branch2);
    Add(0xA2, "if_icmpge", OperandKind.Branch2);
    Add(0xA3, "if_icmpgt", OperandKind.Branch2);
    Add(0xA4, "if_icmple", OperandKind.Branch2);
    Add(0xA5, "if_acmpeq", OperandKind.Branch2);
    Add(0xA6, "if_acmpne", OperandKind.Branch2);
    Add(0xA7, "goto", OperandKind.Branch2);
    Add(0xAA, "tableswitch", OperandKind.TableSwitch);
    Add(0xAB, "lookupswitch", OperandKind.LookupSwitch);
    Add(0xAC, "ireturn");
    Add(0xAD, "lreturn");
    Add(0xAE, "freturn");
    Add(0xAF, "dreturn");
    Add(0xB0, "areturn");
    Add(0xB1, "return");
    Add(0xB2, "getstatic", OperandKind.CpShort);
    Add(0xB3, "putstatic", OperandKind.CpShort);
    Add(0xB4, "getfield", OperandKind.CpShort);
    Add(0xB5, "putfield", OperandKind.CpShort);
    Add(0xB6, "invokevirtual", OperandKind.CpShort);
    Add(0xB7, "invokespecial", OperandKind.CpShort);
    Add(0xB8, "invokestatic", OperandKind.CpShort);
    Add(0xB9, "invokeinterface", OperandKind.InvokeInterface);
    Add(0xBB, "new", OperandKind.CpShort);
    Add(0xBC, "newarray", OperandKind.NewArray);
    Add(0xBD, "anewarray", OperandKind.CpShort);
    Add(0xBE, "arraylength");
    Add(0xBF, "athrow");
    Add(0xC0, "checkcast", OperandKind.CpShort);
    Add(0xC1, "instanceof", OperandKind.CpShort);
    Add(0xC6, "ifnull", OperandKind.Branch2);
    Add(0xC7, "ifnonnull", OperandKind.Branch2);
    Add(0xC8, "goto_w", OperandKind.Branch4);
    return ops;
  }

}
