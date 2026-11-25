using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Polypiler.Core.Backends.Jvm.Bytecode;

namespace Polypiler.Core.Backends.Jvm.ClassFile;

/// <summary>A field declared in a JVMS classfile.</summary>
internal sealed record JvmFieldDef(ushort AccessFlags, string Name, string Descriptor);

/// <summary>An exception handler entry in a method's <c>Code</c> attribute (JVMS §4.7.3).</summary>
internal readonly record struct JvmExceptionEntry(ushort StartPc, ushort EndPc, ushort HandlerPc, ushort CatchType);

/// <summary>A single <c>full_frame</c> entry in a method's <c>StackMapTable</c> attribute (JVMS §4.7.4).</summary>
internal sealed record JvmStackMapFrame(int Offset, VType[] Locals, VType[] Stack);

/// <summary>Compiled bytecode body and metadata for a JVM method's <c>Code</c> attribute.</summary>
internal sealed record JvmMethodCode(
  ushort MaxStack,
  ushort MaxLocals,
  byte[] Bytecode,
  IReadOnlyList<JvmExceptionEntry> ExceptionTable,
  IReadOnlyList<JvmStackMapFrame> StackMapFrames);

/// <summary>A method declared in a JVMS classfile.</summary>
internal sealed record JvmMethodDef(ushort AccessFlags, string Name, string Descriptor, JvmMethodCode Code);

/// <summary>
/// Writes a verifier-clean JVMS Chapter 4 <c>.class</c> file with constant pool, class metadata,
/// static fields, methods, <c>Code</c> attributes, exception tables, and <c>StackMapTable</c> attributes.
/// </summary>
internal sealed class ClassFileWriter {

  public const ushort AccPublic = 0x0001;
  public const ushort AccPrivate = 0x0002;
  public const ushort AccStatic = 0x0008;
  public const ushort AccFinal = 0x0010;
  public const ushort AccSuper = 0x0020;

  private readonly ConstantPool _pool;
  private readonly ushort _majorVersion;
  private readonly ushort _thisClassIndex;
  private readonly ushort _superClassIndex;
  private readonly ushort _codeAttrNameIndex;
  private readonly ushort _stackMapTableAttrNameIndex;
  private readonly List<JvmFieldDef> _fields = [];
  private readonly List<JvmMethodDef> _methods = [];

  /// <summary>Initializes a classfile writer for a public class extending <c>java/lang/Object</c>.</summary>
  public ClassFileWriter(ConstantPool pool, ushort majorVersion, string internalClassName) {
    this._pool = pool;
    this._majorVersion = majorVersion;
    this._thisClassIndex = pool.AddClass(internalClassName);
    this._superClassIndex = pool.AddClass("java/lang/Object");
    this._codeAttrNameIndex = pool.AddUtf8("Code");
    this._stackMapTableAttrNameIndex = pool.AddUtf8("StackMapTable");
  }

  /// <summary>Adds a field definition.</summary>
  public void AddField(JvmFieldDef field) => this._fields.Add(field);

  /// <summary>Adds a method definition.</summary>
  public void AddMethod(JvmMethodDef method) => this._methods.Add(method);

  /// <summary>Serializes the entire classfile to a byte array.</summary>
  public byte[] ToBytes() {
    // Pre-populate all field/method UTF-8 constant pool entries before writing the pool.
    var fieldIndices = new List<(ushort Flags, ushort NameIdx, ushort DescIdx)>(this._fields.Count);
    foreach (var field in this._fields)
      fieldIndices.Add((field.AccessFlags, this._pool.AddUtf8(field.Name), this._pool.AddUtf8(field.Descriptor)));

    var methodIndices = new List<(ushort Flags, ushort NameIdx, ushort DescIdx, JvmMethodCode Code)>(this._methods.Count);
    foreach (var method in this._methods)
      methodIndices.Add((method.AccessFlags, this._pool.AddUtf8(method.Name), this._pool.AddUtf8(method.Descriptor), method.Code));

    using var stream = new MemoryStream();
    WriteU4(stream, 0xCAFEBABE);
    WriteU2(stream, 0);
    WriteU2(stream, this._majorVersion);
    this._pool.WriteTo(stream);
    WriteU2(stream, AccPublic | AccSuper);
    WriteU2(stream, this._thisClassIndex);
    WriteU2(stream, this._superClassIndex);
    WriteU2(stream, 0); // interfaces_count

    WriteU2(stream, (ushort)fieldIndices.Count);
    foreach (var (flags, nameIdx, descIdx) in fieldIndices) {
      WriteU2(stream, flags);
      WriteU2(stream, nameIdx);
      WriteU2(stream, descIdx);
      WriteU2(stream, 0); // attributes_count
    }

    WriteU2(stream, (ushort)methodIndices.Count);
    foreach (var (flags, nameIdx, descIdx, code) in methodIndices) {
      WriteU2(stream, flags);
      WriteU2(stream, nameIdx);
      WriteU2(stream, descIdx);
      WriteU2(stream, 1); // Code attribute
      this.WriteCodeAttribute(stream, code);
    }

    WriteU2(stream, 0); // class attributes_count
    return stream.ToArray();
  }

  private void WriteCodeAttribute(Stream stream, JvmMethodCode code) {
    var stackMapBytes = code.StackMapFrames.Count > 0 ? SerializeStackMapTable(code.StackMapFrames) : null;
    var subAttrCount = stackMapBytes is not null ? (ushort)1 : (ushort)0;
    var subAttrLength = stackMapBytes is not null ? 6 + stackMapBytes.Length : 0;
    var codeAttrLength = 2 + 2 + 4 + code.Bytecode.Length + 2 + (code.ExceptionTable.Count * 8) + 2 + subAttrLength;

    WriteU2(stream, this._codeAttrNameIndex);
    WriteU4(stream, (uint)codeAttrLength);
    WriteU2(stream, code.MaxStack);
    WriteU2(stream, code.MaxLocals);
    WriteU4(stream, (uint)code.Bytecode.Length);
    stream.Write(code.Bytecode, 0, code.Bytecode.Length);

    WriteU2(stream, (ushort)code.ExceptionTable.Count);
    foreach (var entry in code.ExceptionTable) {
      WriteU2(stream, entry.StartPc);
      WriteU2(stream, entry.EndPc);
      WriteU2(stream, entry.HandlerPc);
      WriteU2(stream, entry.CatchType);
    }

    WriteU2(stream, subAttrCount);
    if (stackMapBytes is not null) {
      WriteU2(stream, this._stackMapTableAttrNameIndex);
      WriteU4(stream, (uint)stackMapBytes.Length);
      stream.Write(stackMapBytes, 0, stackMapBytes.Length);
    }
  }

  private static byte[] SerializeStackMapTable(IReadOnlyList<JvmStackMapFrame> frames) {
    using var ms = new MemoryStream();
    WriteU2(ms, (ushort)frames.Count);
    var previousOffset = -1;
    foreach (var frame in frames) {
      var delta = previousOffset < 0 ? frame.Offset : frame.Offset - previousOffset - 1;
      previousOffset = frame.Offset;
      ms.WriteByte(255); // full_frame
      WriteU2(ms, (ushort)delta);
      WriteU2(ms, (ushort)frame.Locals.Length);
      foreach (var local in frame.Locals)
        WriteVerificationType(ms, local);

      WriteU2(ms, (ushort)frame.Stack.Length);
      foreach (var item in frame.Stack)
        WriteVerificationType(ms, item);
    }

    return ms.ToArray();
  }

  private static void WriteVerificationType(Stream stream, VType type) {
    stream.WriteByte(type.Tag);
    if (type.Tag == VType.ObjectTag)
      WriteU2(stream, type.ClassIndex);
  }

  internal static void WriteU2(Stream stream, ushort value) {
    Span<byte> buf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(buf, value);
    stream.Write(buf);
  }

  internal static void WriteU4(Stream stream, uint value) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buf, value);
    stream.Write(buf);
  }

}
