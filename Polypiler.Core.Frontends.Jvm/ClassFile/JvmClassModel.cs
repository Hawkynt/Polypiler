using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Jvm.ClassFile;

internal static class JvmAccessFlags {

  public const ushort Public = 0x0001;
  public const ushort Static = 0x0008;
  public const ushort Native = 0x0100;
  public const ushort Abstract = 0x0400;
  public const ushort Bridge = 0x0040;
  public const ushort Synthetic = 0x1000;

}

internal enum JvmConstantTag : byte {

  Utf8 = 1,
  Integer = 3,
  Float = 4,
  Long = 5,
  Double = 6,
  Class = 7,
  String = 8,
  Fieldref = 9,
  Methodref = 10,
  InterfaceMethodref = 11,
  NameAndType = 12,
  MethodHandle = 15,
  MethodType = 16,
  Dynamic = 17,
  InvokeDynamic = 18,
  Module = 19,
  Package = 20

}

internal sealed record JvmCpEntry(
  JvmConstantTag Tag,
  string? Text = null,
  long IntValue = 0,
  double FloatValue = 0,
  ushort Index1 = 0,
  ushort Index2 = 0);

internal sealed class JvmConstantPool(JvmCpEntry?[] entries) {

  public JvmCpEntry? GetEntry(int index)
    => index >= 1 && index < entries.Length ? entries[index] : null;

  public string? TryGetUtf8(int index)
    => this.GetEntry(index) is { Tag: JvmConstantTag.Utf8 } entry ? entry.Text : null;

  public string? TryGetClassName(int index)
    => this.GetEntry(index) is { Tag: JvmConstantTag.Class } entry ? this.TryGetUtf8(entry.Index1) : null;

  public (string Owner, string Name, string Descriptor)? TryGetMemberRef(int index) {
    if (this.GetEntry(index) is not { Tag: JvmConstantTag.Fieldref or JvmConstantTag.Methodref or JvmConstantTag.InterfaceMethodref } entry)
      return null;

    var owner = this.TryGetClassName(entry.Index1);
    if (owner is null || this.GetEntry(entry.Index2) is not { Tag: JvmConstantTag.NameAndType } nat)
      return null;

    var name = this.TryGetUtf8(nat.Index1);
    var descriptor = this.TryGetUtf8(nat.Index2);
    return name is not null && descriptor is not null ? (owner, name, descriptor) : null;
  }

}

internal sealed record JvmFieldInfo(ushort AccessFlags, string Name, string Descriptor, JvmCpEntry? ConstantValue);

internal sealed record JvmExceptionEntry(int StartPc, int EndPc, int HandlerPc, string? CatchType);

internal sealed record JvmCodeAttribute(byte[] Code, IReadOnlyList<JvmExceptionEntry> ExceptionTable);

internal sealed record JvmMethodInfo(ushort AccessFlags, string Name, string Descriptor, JvmCodeAttribute? Code);

internal sealed record JvmClassFile(
  string SourceName,
  string ThisClass,
  JvmConstantPool ConstantPool,
  IReadOnlyList<JvmFieldInfo> Fields,
  IReadOnlyList<JvmMethodInfo> Methods);
