using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Frontends.Jvm.ClassFile;

internal static class JvmClassReader {

  private const uint ClassMagic = 0xCAFEBABEu;
  private const uint ZipLocalMagic = 0x504B0304u;
  private const uint ZipEmptyMagic = 0x504B0506u;
  private const ushort MinimumMajorVersion = 50;

  public static IReadOnlyList<JvmClassFile> LoadArchiveOrClass(string fileName, byte[] bytes, List<Diagnostic> diagnostics) {
    if (bytes.Length < 4) {
      diagnostics.Add(JvmFrontend.NotClassOrJar.At($"'{fileName}' is neither a JVM .class file nor a .jar archive", fileName));
      return [];
    }

    var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes);
    if (magic == ClassMagic) {
      var single = ReadClass(fileName, bytes, diagnostics);
      return single is null ? [] : [single];
    }

    if (magic is ZipLocalMagic or ZipEmptyMagic)
      return ReadJar(fileName, bytes, diagnostics);

    diagnostics.Add(JvmFrontend.NotClassOrJar.At($"'{fileName}' is neither a JVM .class file nor a .jar archive (magic 0x{magic:X8})", fileName));
    return [];
  }

  private static IReadOnlyList<JvmClassFile> ReadJar(string fileName, byte[] bytes, List<Diagnostic> diagnostics) {
    try {
      using var stream = new MemoryStream(bytes, writable: false);
      using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
      var classEntries = archive.Entries
        .Where(entry => entry.FullName.EndsWith(".class", StringComparison.Ordinal)
          && !entry.FullName.StartsWith("META-INF/", StringComparison.Ordinal)
          && !string.Equals(entry.FullName, "module-info.class", StringComparison.Ordinal)
          && !entry.FullName.EndsWith("/module-info.class", StringComparison.Ordinal))
        .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
        .ToList();
      if (classEntries.Count == 0) {
        diagnostics.Add(JvmFrontend.CorruptClassFile.At($"archive '{fileName}' contains no .class entries", fileName));
        return [];
      }

      var classes = new List<JvmClassFile>(classEntries.Count);
      foreach (var entry in classEntries) {
        using var entryStream = entry.Open();
        using var copy = new MemoryStream();
        entryStream.CopyTo(copy);
        var parsed = ReadClass($"{fileName}!/{entry.FullName}", copy.ToArray(), diagnostics);
        if (parsed is not null)
          classes.Add(parsed);
      }

      return diagnostics.Count > 0 ? [] : classes;
    } catch (InvalidDataException ex) {
      diagnostics.Add(JvmFrontend.CorruptClassFile.At($"corrupt .jar archive '{fileName}': {ex.Message}", fileName));
      return [];
    }
  }

  private static JvmClassFile? ReadClass(string sourceName, byte[] bytes, List<Diagnostic> diagnostics) {
    var reader = new BinaryReaderCursor(bytes);
    try {
      var magic = reader.ReadU4();
      if (magic != ClassMagic)
        throw new ClassFormatException(JvmFrontend.NotClassOrJar, $"'{sourceName}' does not begin with 0xCAFEBABE");

      _ = reader.ReadU2();
      var major = reader.ReadU2();
      if (major < MinimumMajorVersion)
        throw new ClassFormatException(JvmFrontend.UnsupportedClassVersion,
          $"class file '{sourceName}' has major version {major}; Java 6+ (major >= {MinimumMajorVersion}) is required");

      var pool = ReadConstantPool(ref reader, sourceName);
      _ = reader.ReadU2();
      var thisClassIndex = reader.ReadU2();
      var superClassIndex = reader.ReadU2();
      var thisClass = pool.TryGetClassName(thisClassIndex)
        ?? throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"invalid this_class index {thisClassIndex} in '{sourceName}'");

      if (superClassIndex != 0 && pool.TryGetClassName(superClassIndex) is null)
        throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"invalid super_class index {superClassIndex} in '{sourceName}'");

      var interfacesCount = reader.ReadU2();
      for (var i = 0; i < interfacesCount; ++i)
        _ = reader.ReadU2();

      var fieldsCount = reader.ReadU2();
      var fields = new List<JvmFieldInfo>(fieldsCount);
      for (var i = 0; i < fieldsCount; ++i)
        fields.Add(ReadField(ref reader, pool, sourceName));

      var methodsCount = reader.ReadU2();
      var methods = new List<JvmMethodInfo>(methodsCount);
      for (var i = 0; i < methodsCount; ++i)
        methods.Add(ReadMethod(ref reader, pool, sourceName));

      var classAttributesCount = reader.ReadU2();
      for (var i = 0; i < classAttributesCount; ++i) {
        _ = reader.ReadU2();
        reader.Skip(reader.ReadU4());
      }

      return new(sourceName, thisClass, pool, fields, methods);
    } catch (ClassFormatException ex) {
      diagnostics.Add(ex.Descriptor.At(ex.Message, sourceName));
      return null;
    }
  }

  private static JvmConstantPool ReadConstantPool(ref BinaryReaderCursor reader, string sourceName) {
    var count = reader.ReadU2();
    if (count < 1)
      throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"invalid constant_pool_count {count} in '{sourceName}'");

    var entries = new JvmCpEntry?[count];
    for (var i = 1; i < count; ++i) {
      var tag = reader.ReadU1();
      switch ((JvmConstantTag)tag) {
        case JvmConstantTag.Utf8:
          entries[i] = new(JvmConstantTag.Utf8, Text: DecodeModifiedUtf8(reader.ReadSpan(reader.ReadU2())));
          break;
        case JvmConstantTag.Integer:
          entries[i] = new(JvmConstantTag.Integer, IntValue: unchecked((int)reader.ReadU4()));
          break;
        case JvmConstantTag.Float:
          entries[i] = new(JvmConstantTag.Float, FloatValue: BitConverter.Int32BitsToSingle(unchecked((int)reader.ReadU4())));
          break;
        case JvmConstantTag.Long:
          entries[i] = new(JvmConstantTag.Long, IntValue: unchecked((long)reader.ReadU8()));
          if (++i >= count)
            throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"CONSTANT_Long overflows constant pool in '{sourceName}'");

          break;
        case JvmConstantTag.Double:
          entries[i] = new(JvmConstantTag.Double, FloatValue: BitConverter.Int64BitsToDouble(unchecked((long)reader.ReadU8())));
          if (++i >= count)
            throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"CONSTANT_Double overflows constant pool in '{sourceName}'");

          break;
        case JvmConstantTag.Class:
        case JvmConstantTag.String:
        case JvmConstantTag.MethodType:
        case JvmConstantTag.Module:
        case JvmConstantTag.Package:
          entries[i] = new((JvmConstantTag)tag, Index1: reader.ReadU2());
          break;
        case JvmConstantTag.Fieldref:
        case JvmConstantTag.Methodref:
        case JvmConstantTag.InterfaceMethodref:
        case JvmConstantTag.NameAndType:
        case JvmConstantTag.Dynamic:
        case JvmConstantTag.InvokeDynamic:
          entries[i] = new((JvmConstantTag)tag, Index1: reader.ReadU2(), Index2: reader.ReadU2());
          break;
        case JvmConstantTag.MethodHandle:
          entries[i] = new(JvmConstantTag.MethodHandle, Index1: reader.ReadU1(), Index2: reader.ReadU2());
          break;
        default:
          throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"unknown constant pool tag {tag} at index {i} in '{sourceName}'");
      }
    }

    return new(entries);
  }

  private static JvmFieldInfo ReadField(ref BinaryReaderCursor reader, JvmConstantPool pool, string sourceName) {
    var accessFlags = reader.ReadU2();
    var name = pool.TryGetUtf8(reader.ReadU2());
    var descriptor = pool.TryGetUtf8(reader.ReadU2());
    if (name is null || descriptor is null)
      throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"invalid field constant pool reference in '{sourceName}'");

    JvmCpEntry? constantValue = null;
    var attrCount = reader.ReadU2();
    for (var i = 0; i < attrCount; ++i) {
      var attrName = pool.TryGetUtf8(reader.ReadU2());
      var attrSpan = reader.ReadSpan(reader.ReadU4());
      if (attrName == "ConstantValue" && attrSpan.Length == 2)
        constantValue = pool.GetEntry(BinaryPrimitives.ReadUInt16BigEndian(attrSpan));
    }

    return new(accessFlags, name, descriptor, constantValue);
  }

  private static JvmMethodInfo ReadMethod(ref BinaryReaderCursor reader, JvmConstantPool pool, string sourceName) {
    var accessFlags = reader.ReadU2();
    var name = pool.TryGetUtf8(reader.ReadU2());
    var descriptor = pool.TryGetUtf8(reader.ReadU2());
    if (name is null || descriptor is null)
      throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"invalid method constant pool reference in '{sourceName}'");

    JvmCodeAttribute? code = null;
    var attrCount = reader.ReadU2();
    for (var i = 0; i < attrCount; ++i) {
      var attrName = pool.TryGetUtf8(reader.ReadU2());
      var attrBytes = reader.ReadSpan(reader.ReadU4());
      if (attrName == "Code")
        code = ReadCodeAttribute(attrBytes, pool, sourceName);
    }

    return new(accessFlags, name, descriptor, code);
  }

  private static JvmCodeAttribute ReadCodeAttribute(ReadOnlySpan<byte> data, JvmConstantPool pool, string sourceName) {
    var cursor = new BinaryReaderCursor(data);
    _ = cursor.ReadU2();
    _ = cursor.ReadU2();
    var code = cursor.ReadSpan(cursor.ReadU4()).ToArray();
    var exceptionCount = cursor.ReadU2();
    var exceptions = new List<JvmExceptionEntry>(exceptionCount);
    for (var i = 0; i < exceptionCount; ++i) {
      var startPc = cursor.ReadU2();
      var endPc = cursor.ReadU2();
      var handlerPc = cursor.ReadU2();
      var catchTypeIndex = cursor.ReadU2();
      if (startPc >= endPc || endPc > code.Length || handlerPc >= code.Length)
        throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"exception table entry out of bytecode bounds in '{sourceName}'");

      string? catchType = null;
      if (catchTypeIndex != 0)
        catchType = pool.TryGetClassName(catchTypeIndex)
          ?? throw new ClassFormatException(JvmFrontend.CorruptClassFile, $"invalid catch_type index {catchTypeIndex} in '{sourceName}'");

      exceptions.Add(new(startPc, endPc, handlerPc, catchType));
    }

    var subAttrCount = cursor.ReadU2();
    for (var i = 0; i < subAttrCount; ++i) {
      var attrName = pool.TryGetUtf8(cursor.ReadU2());
      var attrSpan = cursor.ReadSpan(cursor.ReadU4());
      if (attrName == "LineNumberTable") {
        var sub = new BinaryReaderCursor(attrSpan);
        var count = sub.ReadU2();
        sub.Skip((uint)count * 4u);
      } else if (attrName == "StackMapTable") {
        ValidateStackMapTable(attrSpan, code.Length, pool, sourceName);
      }
    }

    return new(code, exceptions);
  }

  private static void ValidateStackMapTable(ReadOnlySpan<byte> data, int codeLength, JvmConstantPool pool, string sourceName) {
    var cursor = new BinaryReaderCursor(data, JvmFrontend.InvalidStackMap);
    var count = cursor.ReadU2();
    var pc = -1;
    for (var i = 0; i < count; ++i) {
      var frameType = cursor.ReadU1();
      int offsetDelta;
      if (frameType <= 63) {
        offsetDelta = frameType;
      } else if (frameType <= 127) {
        offsetDelta = frameType - 64;
        ValidateVerificationType(ref cursor, codeLength, pool, sourceName);
      } else if (frameType == 247) {
        offsetDelta = cursor.ReadU2();
        ValidateVerificationType(ref cursor, codeLength, pool, sourceName);
      } else if (frameType is >= 248 and <= 251) {
        offsetDelta = cursor.ReadU2();
      } else if (frameType is >= 252 and <= 254) {
        offsetDelta = cursor.ReadU2();
        var appendCount = frameType - 251;
        for (var j = 0; j < appendCount; ++j)
          ValidateVerificationType(ref cursor, codeLength, pool, sourceName);
      } else if (frameType == 255) {
        offsetDelta = cursor.ReadU2();
        var localCount = cursor.ReadU2();
        for (var j = 0; j < localCount; ++j)
          ValidateVerificationType(ref cursor, codeLength, pool, sourceName);

        var stackCount = cursor.ReadU2();
        for (var j = 0; j < stackCount; ++j)
          ValidateVerificationType(ref cursor, codeLength, pool, sourceName);
      } else {
        throw new ClassFormatException(JvmFrontend.InvalidStackMap, $"reserved StackMapTable frame tag {frameType} in '{sourceName}'");
      }

      pc = pc < 0 ? offsetDelta : pc + offsetDelta + 1;
      if (pc >= codeLength)
        throw new ClassFormatException(JvmFrontend.InvalidStackMap, $"StackMapTable frame offset {pc} exceeds bytecode length {codeLength} in '{sourceName}'");
    }
  }

  private static void ValidateVerificationType(ref BinaryReaderCursor cursor, int codeLength, JvmConstantPool pool, string sourceName) {
    var tag = cursor.ReadU1();
    if (tag <= 6)
      return;

    if (tag == 7) {
      var cpoolIndex = cursor.ReadU2();
      if (pool.TryGetClassName(cpoolIndex) is null)
        throw new ClassFormatException(JvmFrontend.InvalidStackMap, $"StackMapTable Object verification type references invalid class index {cpoolIndex} in '{sourceName}'");

      return;
    }

    if (tag == 8) {
      var offset = cursor.ReadU2();
      if (offset >= codeLength)
        throw new ClassFormatException(JvmFrontend.InvalidStackMap, $"StackMapTable Uninitialized offset {offset} exceeds bytecode length {codeLength} in '{sourceName}'");

      return;
    }

    throw new ClassFormatException(JvmFrontend.InvalidStackMap, $"invalid StackMapTable verification_type_info tag {tag} in '{sourceName}'");
  }

  private static string DecodeModifiedUtf8(ReadOnlySpan<byte> bytes) {
    var builder = new StringBuilder(bytes.Length);
    var i = 0;
    while (i < bytes.Length) {
      var b1 = bytes[i++];
      if ((b1 & 0x80) == 0) {
        builder.Append((char)b1);
      } else if ((b1 & 0xE0) == 0xC0 && i < bytes.Length) {
        var b2 = bytes[i++];
        builder.Append((char)(((b1 & 0x1F) << 6) | (b2 & 0x3F)));
      } else if ((b1 & 0xF0) == 0xE0 && i + 1 < bytes.Length) {
        var b2 = bytes[i++];
        var b3 = bytes[i++];
        builder.Append((char)(((b1 & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F)));
      } else {
        throw new ClassFormatException(JvmFrontend.CorruptClassFile, "invalid Modified UTF-8 sequence in constant pool");
      }
    }

    return builder.ToString();
  }

  private sealed class ClassFormatException(DiagnosticDescriptor descriptor, string message) : Exception(message) {

    public DiagnosticDescriptor Descriptor { get; } = descriptor;

  }

  private ref struct BinaryReaderCursor(ReadOnlySpan<byte> data, DiagnosticDescriptor? overflowDescriptor = null) {

    private readonly ReadOnlySpan<byte> _data = data;
    private readonly DiagnosticDescriptor _overflow = overflowDescriptor ?? JvmFrontend.CorruptClassFile;
    private int _offset;

    public byte ReadU1() {
      this.Ensure(1);
      return this._data[this._offset++];
    }

    public ushort ReadU2() {
      this.Ensure(2);
      var value = BinaryPrimitives.ReadUInt16BigEndian(this._data[this._offset..]);
      this._offset += 2;
      return value;
    }

    public uint ReadU4() {
      this.Ensure(4);
      var value = BinaryPrimitives.ReadUInt32BigEndian(this._data[this._offset..]);
      this._offset += 4;
      return value;
    }

    public ulong ReadU8() {
      this.Ensure(8);
      var value = BinaryPrimitives.ReadUInt64BigEndian(this._data[this._offset..]);
      this._offset += 8;
      return value;
    }

    public ReadOnlySpan<byte> ReadSpan(uint length) {
      if (length > (uint)(this._data.Length - this._offset))
        throw new ClassFormatException(this._overflow, "unexpected end of class file");

      var len = (int)length;
      var slice = this._data.Slice(this._offset, len);
      this._offset += len;
      return slice;
    }

    public void Skip(uint length) => _ = this.ReadSpan(length);

    private readonly void Ensure(int count) {
      if (this._offset + count > this._data.Length)
        throw new ClassFormatException(this._overflow, "unexpected end of class file");
    }

  }

}
