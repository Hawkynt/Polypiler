using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Polypiler.Core.Backends.Jvm.ClassFile;

/// <summary>
/// Deduplicating JVMS Chapter 4 constant pool builder supporting UTF-8 (modified UTF-8),
/// primitive numeric constants, class/string/fieldref/methodref/name-and-type entries.
/// </summary>
internal sealed class ConstantPool {

  private const byte TagUtf8 = 1;
  private const byte TagInteger = 3;
  private const byte TagFloat = 4;
  private const byte TagLong = 5;
  private const byte TagDouble = 6;
  private const byte TagClass = 7;
  private const byte TagString = 8;
  private const byte TagFieldref = 9;
  private const byte TagMethodref = 10;
  private const byte TagInterfaceMethodref = 11;
  private const byte TagNameAndType = 12;

  private readonly List<byte[]> _entries = [];
  private readonly Dictionary<string, ushort> _utf8 = new(StringComparer.Ordinal);
  private readonly Dictionary<int, ushort> _ints = [];
  private readonly Dictionary<int, ushort> _floats = [];
  private readonly Dictionary<long, ushort> _longs = [];
  private readonly Dictionary<long, ushort> _doubles = [];
  private readonly Dictionary<ushort, ushort> _classes = [];
  private readonly Dictionary<ushort, ushort> _strings = [];
  private readonly Dictionary<(ushort, ushort), ushort> _nameAndTypes = [];
  private readonly Dictionary<(ushort, ushort), ushort> _fieldrefs = [];
  private readonly Dictionary<(ushort, ushort), ushort> _methodrefs = [];
  private readonly Dictionary<(ushort, ushort), ushort> _interfaceMethodrefs = [];
  private ushort _nextIndex = 1;

  /// <summary>Adds or retrieves a <c>CONSTANT_Utf8_info</c> entry encoded in JVMS modified UTF-8.</summary>
  public ushort AddUtf8(string text) {
    if (this._utf8.TryGetValue(text, out var existing))
      return existing;

    var bytes = EncodeModifiedUtf8(text);
    var entry = new byte[3 + bytes.Length];
    entry[0] = TagUtf8;
    BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(1, 2), (ushort)bytes.Length);
    bytes.CopyTo(entry, 3);
    var index = this.Append(entry, 1);
    this._utf8[text] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_Integer_info</c> entry.</summary>
  public ushort AddInteger(int value) {
    if (this._ints.TryGetValue(value, out var existing))
      return existing;

    var entry = new byte[5];
    entry[0] = TagInteger;
    BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(1, 4), value);
    var index = this.Append(entry, 1);
    this._ints[value] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_Float_info</c> entry from its IEEE 754 binary32 bits.</summary>
  public ushort AddFloatBits(int bits) {
    if (this._floats.TryGetValue(bits, out var existing))
      return existing;

    var entry = new byte[5];
    entry[0] = TagFloat;
    BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(1, 4), bits);
    var index = this.Append(entry, 1);
    this._floats[bits] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_Long_info</c> entry (consumes two constant-pool indices).</summary>
  public ushort AddLong(long value) {
    if (this._longs.TryGetValue(value, out var existing))
      return existing;

    var entry = new byte[9];
    entry[0] = TagLong;
    BinaryPrimitives.WriteInt64BigEndian(entry.AsSpan(1, 8), value);
    var index = this.Append(entry, 2);
    this._longs[value] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_Double_info</c> entry from its IEEE 754 binary64 bits (consumes two indices).</summary>
  public ushort AddDoubleBits(long bits) {
    if (this._doubles.TryGetValue(bits, out var existing))
      return existing;

    var entry = new byte[9];
    entry[0] = TagDouble;
    BinaryPrimitives.WriteInt64BigEndian(entry.AsSpan(1, 8), bits);
    var index = this.Append(entry, 2);
    this._doubles[bits] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_Class_info</c> entry for an internal class name or array descriptor.</summary>
  public ushort AddClass(string internalName) {
    var nameIndex = this.AddUtf8(internalName);
    if (this._classes.TryGetValue(nameIndex, out var existing))
      return existing;

    var entry = new byte[3];
    entry[0] = TagClass;
    BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(1, 2), nameIndex);
    var index = this.Append(entry, 1);
    this._classes[nameIndex] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_String_info</c> entry.</summary>
  public ushort AddString(string value) {
    var utf8Index = this.AddUtf8(value);
    if (this._strings.TryGetValue(utf8Index, out var existing))
      return existing;

    var entry = new byte[3];
    entry[0] = TagString;
    BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(1, 2), utf8Index);
    var index = this.Append(entry, 1);
    this._strings[utf8Index] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_NameAndType_info</c> entry.</summary>
  public ushort AddNameAndType(string name, string descriptor) {
    var key = (this.AddUtf8(name), this.AddUtf8(descriptor));
    if (this._nameAndTypes.TryGetValue(key, out var existing))
      return existing;

    var index = this.AppendPair(TagNameAndType, key.Item1, key.Item2);
    this._nameAndTypes[key] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_Fieldref_info</c> entry.</summary>
  public ushort AddFieldref(string ownerInternalName, string fieldName, string descriptor) {
    var key = (this.AddClass(ownerInternalName), this.AddNameAndType(fieldName, descriptor));
    if (this._fieldrefs.TryGetValue(key, out var existing))
      return existing;

    var index = this.AppendPair(TagFieldref, key.Item1, key.Item2);
    this._fieldrefs[key] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_Methodref_info</c> entry.</summary>
  public ushort AddMethodref(string ownerInternalName, string methodName, string descriptor) {
    var key = (this.AddClass(ownerInternalName), this.AddNameAndType(methodName, descriptor));
    if (this._methodrefs.TryGetValue(key, out var existing))
      return existing;

    var index = this.AppendPair(TagMethodref, key.Item1, key.Item2);
    this._methodrefs[key] = index;
    return index;
  }

  /// <summary>Adds or retrieves a <c>CONSTANT_InterfaceMethodref_info</c> entry.</summary>
  public ushort AddInterfaceMethodref(string ownerInternalName, string methodName, string descriptor) {
    var key = (this.AddClass(ownerInternalName), this.AddNameAndType(methodName, descriptor));
    if (this._interfaceMethodrefs.TryGetValue(key, out var existing))
      return existing;

    var index = this.AppendPair(TagInterfaceMethodref, key.Item1, key.Item2);
    this._interfaceMethodrefs[key] = index;
    return index;
  }

  /// <summary>Writes <c>constant_pool_count</c> followed by every constant pool entry.</summary>
  public void WriteTo(Stream stream) {
    ClassFileWriter.WriteU2(stream, this._nextIndex);
    foreach (var entry in this._entries)
      stream.Write(entry, 0, entry.Length);
  }

  private ushort AppendPair(byte tag, ushort first, ushort second) {
    var entry = new byte[5];
    entry[0] = tag;
    BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(1, 2), first);
    BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(3, 2), second);
    return this.Append(entry, 1);
  }

  private ushort Append(byte[] entry, ushort slots) {
    var index = this._nextIndex;
    this._entries.Add(entry);
    this._nextIndex = (ushort)(this._nextIndex + slots);
    return index;
  }

  /// <summary>Encodes a UTF-16 string in JVMS §4.4.7 modified UTF-8.</summary>
  internal static byte[] EncodeModifiedUtf8(string text) {
    var length = 0;
    foreach (var ch in text)
      length += ch is >= '\u0001' and <= '\u007F' ? 1 : ch <= '\u07FF' ? 2 : 3;

    var buffer = new byte[length];
    var pos = 0;
    foreach (var ch in text) {
      if (ch is >= '\u0001' and <= '\u007F') {
        buffer[pos++] = (byte)ch;
      } else if (ch <= '\u07FF') {
        buffer[pos++] = (byte)(0xC0 | ((ch >> 6) & 0x1F));
        buffer[pos++] = (byte)(0x80 | (ch & 0x3F));
      } else {
        buffer[pos++] = (byte)(0xE0 | ((ch >> 12) & 0x0F));
        buffer[pos++] = (byte)(0x80 | ((ch >> 6) & 0x3F));
        buffer[pos++] = (byte)(0x80 | (ch & 0x3F));
      }
    }

    return buffer;
  }

}
