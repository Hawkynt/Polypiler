using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Polypiler.Tooling.Report;

/// <summary>
/// Disassembles ECMA-335 PE/CLI assemblies (<c>.dll</c>, <c>.exe</c>) emitted by the MSIL backend
/// into deterministic, ILAsm-style readable text mnemonics.
/// </summary>
public static class MsilDisassembler {

  private enum OperandKind : byte {
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

  private readonly record struct OpSpec(string Name, OperandKind Kind);

  private static readonly Dictionary<int, OpSpec> _opcodes = BuildOpcodes();

  /// <summary>Disassembles raw PE/CLI assembly bytes into ILAsm-style text.</summary>
  /// <param name="bytes">The assembly bytes.</param>
  /// <returns>Readable IL disassembly with LF line endings.</returns>
  public static string Disassemble(byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    using var stream = new MemoryStream(bytes, writable: false);
    using var pe = new PEReader(stream);
    var reader = pe.GetMetadataReader();
    var output = new StringBuilder();
    var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
    output.Append(".assembly ").Append(assemblyName).AppendLine(" {}");

    foreach (var typeHandle in reader.TypeDefinitions) {
      var typeDef = reader.GetTypeDefinition(typeHandle);
      var typeName = reader.GetString(typeDef.Name);
      if (typeName == "<Module>")
        continue;

      output.AppendLine();
      WriteType(pe, reader, typeDef, output);
    }

    return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
  }

  private static void WriteType(PEReader pe, MetadataReader reader, TypeDefinition typeDef, StringBuilder output) {
    var ns = reader.GetString(typeDef.Namespace);
    var name = reader.GetString(typeDef.Name);
    var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    output.Append(".class ").Append(FormatTypeFlags(typeDef.Attributes)).Append(' ').Append(fullName);
    if (!typeDef.BaseType.IsNil)
      output.Append(" extends ").Append(FormatEntityName(reader, typeDef.BaseType));
    output.AppendLine(" {");

    foreach (var fieldHandle in typeDef.GetFields()) {
      var field = reader.GetFieldDefinition(fieldHandle);
      var fieldType = field.DecodeSignature(SignatureTypeFormatter.Instance, null);
      output.Append("  .field ").Append(FormatFieldFlags(field.Attributes)).Append(' ')
        .Append(fieldType).Append(' ').AppendLine(reader.GetString(field.Name));
    }

    foreach (var methodHandle in typeDef.GetMethods()) {
      var method = reader.GetMethodDefinition(methodHandle);
      WriteMethod(pe, reader, method, output);
    }

    output.AppendLine("}");
  }

  private static void WriteMethod(PEReader pe, MetadataReader reader, MethodDefinition method, StringBuilder output) {
    var sig = method.DecodeSignature(SignatureTypeFormatter.Instance, null);
    var paramNames = method.GetParameters()
      .Select(reader.GetParameter)
      .Where(p => p.SequenceNumber > 0)
      .ToDictionary(p => p.SequenceNumber, p => reader.GetString(p.Name));

    var parameters = new List<string>(sig.ParameterTypes.Length);
    for (var i = 0; i < sig.ParameterTypes.Length; ++i)
      parameters.Add(paramNames.TryGetValue(i + 1, out var pName) && !string.IsNullOrEmpty(pName)
        ? $"{sig.ParameterTypes[i]} {pName}"
        : sig.ParameterTypes[i]);

    output.Append("  .method ").Append(FormatMethodFlags(method.Attributes)).Append(' ')
      .Append(sig.ReturnType).Append(' ').Append(reader.GetString(method.Name))
      .Append('(').Append(string.Join(", ", parameters)).AppendLine(") cil managed {");

    if (method.RelativeVirtualAddress != 0) {
      var body = pe.GetMethodBody(method.RelativeVirtualAddress);
      output.Append("    .maxstack ").Append(body.MaxStack.ToString(CultureInfo.InvariantCulture)).AppendLine();
      if (!body.LocalSignature.IsNil) {
        var locals = reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(SignatureTypeFormatter.Instance, null);
        if (locals.Length > 0) {
          var entries = locals.Select((type, index) => $"[{index}] {type}");
          output.Append("    .locals init (").Append(string.Join(", ", entries)).AppendLine(")");
        }
      }

      WriteInstructions(reader, body.GetILBytes() ?? [], output);
    }

    output.AppendLine("  }");
  }

  private static void WriteInstructions(MetadataReader reader, byte[] il, StringBuilder output) {
    var pos = 0;
    while (pos < il.Length) {
      var start = pos;
      var first = il[pos++];
      var code = first == 0xFE && pos < il.Length ? 0xFE00 | il[pos++] : first;
      if (!_opcodes.TryGetValue(code, out var spec)) {
        output.Append("    IL_").Append(start.ToString("X4", CultureInfo.InvariantCulture))
          .Append(": .byte 0x").AppendLine(code.ToString("X2", CultureInfo.InvariantCulture));
        continue;
      }

      var operand = ReadOperand(reader, il, ref pos, spec.Kind);
      output.Append("    IL_").Append(start.ToString("X4", CultureInfo.InvariantCulture)).Append(": ").Append(spec.Name);
      if (operand.Length > 0)
        output.Append(' ').Append(operand);
      output.AppendLine();
    }
  }

  private static string ReadOperand(MetadataReader reader, byte[] il, ref int pos, OperandKind kind) {
    var span = il.AsSpan();
    switch (kind) {
      case OperandKind.ShortVar:
        return il[pos++].ToString(CultureInfo.InvariantCulture);
      case OperandKind.Var: {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos, 2));
        pos += 2;
        return value.ToString(CultureInfo.InvariantCulture);
      }
      case OperandKind.ShortI:
        return ((sbyte)il[pos++]).ToString(CultureInfo.InvariantCulture);
      case OperandKind.Int32: {
        var value = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
        pos += 4;
        return value.ToString(CultureInfo.InvariantCulture);
      }
      case OperandKind.Int64: {
        var value = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos, 8));
        pos += 8;
        return value.ToString(CultureInfo.InvariantCulture);
      }
      case OperandKind.Float32: {
        var value = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(pos, 4));
        pos += 4;
        return value.ToString("G9", CultureInfo.InvariantCulture);
      }
      case OperandKind.Float64: {
        var value = BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(pos, 8));
        pos += 8;
        return value.ToString("G17", CultureInfo.InvariantCulture);
      }
      case OperandKind.ShortBranch: {
        var delta = (sbyte)il[pos++];
        return $"IL_{pos + delta:X4}";
      }
      case OperandKind.Branch: {
        var delta = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
        pos += 4;
        return $"IL_{pos + delta:X4}";
      }
      case OperandKind.Token: {
        var token = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
        pos += 4;
        return FormatToken(reader, token);
      }
      case OperandKind.Switch: {
        var count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
        pos += 4;
        var baseOffset = pos + count * 4;
        var targets = new string[count];
        for (var i = 0; i < count; ++i) {
          var delta = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
          pos += 4;
          targets[i] = $"IL_{baseOffset + delta:X4}";
        }
        return $"({string.Join(", ", targets)})";
      }
      case OperandKind.None:
      default:
        return "";
    }
  }

  private static string FormatToken(MetadataReader reader, int token) {
    var handle = MetadataTokens.Handle(token);
    if (handle.Kind == HandleKind.UserString)
      return "\"" + reader.GetUserString((UserStringHandle)handle).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    return FormatEntityName(reader, (EntityHandle)handle);
  }

  private static string FormatEntityName(MetadataReader reader, EntityHandle handle) => handle.Kind switch {
    HandleKind.TypeReference => FormatTypeRef(reader, (TypeReferenceHandle)handle),
    HandleKind.TypeDefinition => FormatTypeDef(reader, (TypeDefinitionHandle)handle),
    HandleKind.MethodDefinition => FormatMethodDef(reader, (MethodDefinitionHandle)handle),
    HandleKind.MemberReference => FormatMemberRef(reader, (MemberReferenceHandle)handle),
    HandleKind.FieldDefinition => reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
    _ => $"0x{MetadataTokens.GetToken(handle):X8}"
  };

  private static string FormatTypeRef(MetadataReader reader, TypeReferenceHandle handle) {
    var typeRef = reader.GetTypeReference(handle);
    var ns = reader.GetString(typeRef.Namespace);
    var name = reader.GetString(typeRef.Name);
    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
  }

  private static string FormatTypeDef(MetadataReader reader, TypeDefinitionHandle handle) {
    var typeDef = reader.GetTypeDefinition(handle);
    var ns = reader.GetString(typeDef.Namespace);
    var name = reader.GetString(typeDef.Name);
    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
  }

  private static string FormatMethodDef(MetadataReader reader, MethodDefinitionHandle handle) {
    var method = reader.GetMethodDefinition(handle);
    var owner = FormatTypeDef(reader, method.GetDeclaringType());
    var sig = method.DecodeSignature(SignatureTypeFormatter.Instance, null);
    return $"{sig.ReturnType} {owner}::{reader.GetString(method.Name)}({string.Join(", ", sig.ParameterTypes)})";
  }

  private static string FormatMemberRef(MetadataReader reader, MemberReferenceHandle handle) {
    var member = reader.GetMemberReference(handle);
    var owner = FormatEntityName(reader, member.Parent);
    var name = reader.GetString(member.Name);
    if (member.GetKind() == MemberReferenceKind.Method) {
      var sig = member.DecodeMethodSignature(SignatureTypeFormatter.Instance, null);
      return $"{sig.ReturnType} {owner}::{name}({string.Join(", ", sig.ParameterTypes)})";
    }
    return $"{owner}::{name}";
  }

  private static string FormatTypeFlags(TypeAttributes attributes) {
    var parts = new List<string>();
    parts.Add((attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public ? "public" : "private");
    if ((attributes & TypeAttributes.Abstract) != 0)
      parts.Add("abstract");
    if ((attributes & TypeAttributes.Sealed) != 0)
      parts.Add("sealed");
    return string.Join(' ', parts);
  }

  private static string FormatFieldFlags(FieldAttributes attributes) {
    var parts = new List<string>();
    parts.Add((attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public ? "public" : "private");
    if ((attributes & FieldAttributes.Static) != 0)
      parts.Add("static");
    if ((attributes & FieldAttributes.InitOnly) != 0)
      parts.Add("initonly");
    return string.Join(' ', parts);
  }

  private static string FormatMethodFlags(MethodAttributes attributes) {
    var parts = new List<string>();
    parts.Add((attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public ? "public" : "private");
    if ((attributes & MethodAttributes.HideBySig) != 0)
      parts.Add("hidebysig");
    if ((attributes & MethodAttributes.Static) != 0)
      parts.Add("static");
    return string.Join(' ', parts);
  }

  private sealed class SignatureTypeFormatter : ISignatureTypeProvider<string, object?> {

    private static readonly Dictionary<PrimitiveTypeCode, string> _primitives = new() {
      [PrimitiveTypeCode.Void] = "void",
      [PrimitiveTypeCode.Boolean] = "bool",
      [PrimitiveTypeCode.Char] = "char",
      [PrimitiveTypeCode.SByte] = "int8",
      [PrimitiveTypeCode.Byte] = "uint8",
      [PrimitiveTypeCode.Int16] = "int16",
      [PrimitiveTypeCode.UInt16] = "uint16",
      [PrimitiveTypeCode.Int32] = "int32",
      [PrimitiveTypeCode.UInt32] = "uint32",
      [PrimitiveTypeCode.Int64] = "int64",
      [PrimitiveTypeCode.UInt64] = "uint64",
      [PrimitiveTypeCode.Single] = "float32",
      [PrimitiveTypeCode.Double] = "float64",
      [PrimitiveTypeCode.String] = "string",
      [PrimitiveTypeCode.IntPtr] = "native int",
      [PrimitiveTypeCode.UIntPtr] = "native uint",
      [PrimitiveTypeCode.Object] = "object"
    };

    public static SignatureTypeFormatter Instance { get; } = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => _primitives.GetValueOrDefault(typeCode, typeCode.ToString());
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => FormatTypeDef(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => FormatTypeRef(reader, handle);
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";
    public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(", ", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
    public string GetPinnedType(string elementType) => elementType + " pinned";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "method *";
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "typespec";

  }

  private static Dictionary<int, OpSpec> BuildOpcodes() {
    var table = new Dictionary<int, OpSpec>();
    void Add(ILOpCode op, string name, OperandKind kind = OperandKind.None) => table[(int)op] = new(name, kind);

    Add(ILOpCode.Nop, "nop");
    Add(ILOpCode.Ldarg_0, "ldarg.0");
    Add(ILOpCode.Ldarg_1, "ldarg.1");
    Add(ILOpCode.Ldarg_2, "ldarg.2");
    Add(ILOpCode.Ldarg_3, "ldarg.3");
    Add(ILOpCode.Ldloc_0, "ldloc.0");
    Add(ILOpCode.Ldloc_1, "ldloc.1");
    Add(ILOpCode.Ldloc_2, "ldloc.2");
    Add(ILOpCode.Ldloc_3, "ldloc.3");
    Add(ILOpCode.Stloc_0, "stloc.0");
    Add(ILOpCode.Stloc_1, "stloc.1");
    Add(ILOpCode.Stloc_2, "stloc.2");
    Add(ILOpCode.Stloc_3, "stloc.3");
    Add(ILOpCode.Ldarg_s, "ldarg.s", OperandKind.ShortVar);
    Add(ILOpCode.Ldarga_s, "ldarga.s", OperandKind.ShortVar);
    Add(ILOpCode.Starg_s, "starg.s", OperandKind.ShortVar);
    Add(ILOpCode.Ldloc_s, "ldloc.s", OperandKind.ShortVar);
    Add(ILOpCode.Ldloca_s, "ldloca.s", OperandKind.ShortVar);
    Add(ILOpCode.Stloc_s, "stloc.s", OperandKind.ShortVar);
    Add(ILOpCode.Ldnull, "ldnull");
    Add(ILOpCode.Ldc_i4_m1, "ldc.i4.m1");
    Add(ILOpCode.Ldc_i4_0, "ldc.i4.0");
    Add(ILOpCode.Ldc_i4_1, "ldc.i4.1");
    Add(ILOpCode.Ldc_i4_2, "ldc.i4.2");
    Add(ILOpCode.Ldc_i4_3, "ldc.i4.3");
    Add(ILOpCode.Ldc_i4_4, "ldc.i4.4");
    Add(ILOpCode.Ldc_i4_5, "ldc.i4.5");
    Add(ILOpCode.Ldc_i4_6, "ldc.i4.6");
    Add(ILOpCode.Ldc_i4_7, "ldc.i4.7");
    Add(ILOpCode.Ldc_i4_8, "ldc.i4.8");
    Add(ILOpCode.Ldc_i4_s, "ldc.i4.s", OperandKind.ShortI);
    Add(ILOpCode.Ldc_i4, "ldc.i4", OperandKind.Int32);
    Add(ILOpCode.Ldc_i8, "ldc.i8", OperandKind.Int64);
    Add(ILOpCode.Ldc_r4, "ldc.r4", OperandKind.Float32);
    Add(ILOpCode.Ldc_r8, "ldc.r8", OperandKind.Float64);
    Add(ILOpCode.Dup, "dup");
    Add(ILOpCode.Pop, "pop");
    Add(ILOpCode.Call, "call", OperandKind.Token);
    Add(ILOpCode.Ret, "ret");
    Add(ILOpCode.Br_s, "br.s", OperandKind.ShortBranch);
    Add(ILOpCode.Brfalse_s, "brfalse.s", OperandKind.ShortBranch);
    Add(ILOpCode.Brtrue_s, "brtrue.s", OperandKind.ShortBranch);
    Add(ILOpCode.Beq_s, "beq.s", OperandKind.ShortBranch);
    Add(ILOpCode.Bge_s, "bge.s", OperandKind.ShortBranch);
    Add(ILOpCode.Bgt_s, "bgt.s", OperandKind.ShortBranch);
    Add(ILOpCode.Ble_s, "ble.s", OperandKind.ShortBranch);
    Add(ILOpCode.Blt_s, "blt.s", OperandKind.ShortBranch);
    Add(ILOpCode.Bne_un_s, "bne.un.s", OperandKind.ShortBranch);
    Add(ILOpCode.Bge_un_s, "bge.un.s", OperandKind.ShortBranch);
    Add(ILOpCode.Bgt_un_s, "bgt.un.s", OperandKind.ShortBranch);
    Add(ILOpCode.Ble_un_s, "ble.un.s", OperandKind.ShortBranch);
    Add(ILOpCode.Blt_un_s, "blt.un.s", OperandKind.ShortBranch);
    Add(ILOpCode.Br, "br", OperandKind.Branch);
    Add(ILOpCode.Brfalse, "brfalse", OperandKind.Branch);
    Add(ILOpCode.Brtrue, "brtrue", OperandKind.Branch);
    Add(ILOpCode.Beq, "beq", OperandKind.Branch);
    Add(ILOpCode.Bge, "bge", OperandKind.Branch);
    Add(ILOpCode.Bgt, "bgt", OperandKind.Branch);
    Add(ILOpCode.Ble, "ble", OperandKind.Branch);
    Add(ILOpCode.Blt, "blt", OperandKind.Branch);
    Add(ILOpCode.Bne_un, "bne.un", OperandKind.Branch);
    Add(ILOpCode.Bge_un, "bge.un", OperandKind.Branch);
    Add(ILOpCode.Bgt_un, "bgt.un", OperandKind.Branch);
    Add(ILOpCode.Ble_un, "ble.un", OperandKind.Branch);
    Add(ILOpCode.Blt_un, "blt.un", OperandKind.Branch);
    Add(ILOpCode.Switch, "switch", OperandKind.Switch);
    Add(ILOpCode.Add, "add");
    Add(ILOpCode.Sub, "sub");
    Add(ILOpCode.Mul, "mul");
    Add(ILOpCode.Div, "div");
    Add(ILOpCode.Div_un, "div.un");
    Add(ILOpCode.Rem, "rem");
    Add(ILOpCode.Rem_un, "rem.un");
    Add(ILOpCode.And, "and");
    Add(ILOpCode.Or, "or");
    Add(ILOpCode.Xor, "xor");
    Add(ILOpCode.Shl, "shl");
    Add(ILOpCode.Shr, "shr");
    Add(ILOpCode.Shr_un, "shr.un");
    Add(ILOpCode.Neg, "neg");
    Add(ILOpCode.Not, "not");
    Add(ILOpCode.Conv_i1, "conv.i1");
    Add(ILOpCode.Conv_i2, "conv.i2");
    Add(ILOpCode.Conv_i4, "conv.i4");
    Add(ILOpCode.Conv_i8, "conv.i8");
    Add(ILOpCode.Conv_r4, "conv.r4");
    Add(ILOpCode.Conv_r8, "conv.r8");
    Add(ILOpCode.Conv_u4, "conv.u4");
    Add(ILOpCode.Conv_u8, "conv.u8");
    Add(ILOpCode.Conv_r_un, "conv.r.un");
    Add(ILOpCode.Conv_u2, "conv.u2");
    Add(ILOpCode.Conv_u1, "conv.u1");
    Add(ILOpCode.Conv_i, "conv.i");
    Add(ILOpCode.Conv_u, "conv.u");
    Add(ILOpCode.Conv_ovf_i1, "conv.ovf.i1");
    Add(ILOpCode.Conv_ovf_u1, "conv.ovf.u1");
    Add(ILOpCode.Conv_ovf_i2, "conv.ovf.i2");
    Add(ILOpCode.Conv_ovf_u2, "conv.ovf.u2");
    Add(ILOpCode.Conv_ovf_i4, "conv.ovf.i4");
    Add(ILOpCode.Conv_ovf_u4, "conv.ovf.u4");
    Add(ILOpCode.Conv_ovf_i8, "conv.ovf.i8");
    Add(ILOpCode.Conv_ovf_u8, "conv.ovf.u8");
    Add(ILOpCode.Conv_ovf_i1_un, "conv.ovf.i1.un");
    Add(ILOpCode.Conv_ovf_u1_un, "conv.ovf.u1.un");
    Add(ILOpCode.Conv_ovf_i2_un, "conv.ovf.i2.un");
    Add(ILOpCode.Conv_ovf_u2_un, "conv.ovf.u2.un");
    Add(ILOpCode.Conv_ovf_i4_un, "conv.ovf.i4.un");
    Add(ILOpCode.Conv_ovf_u4_un, "conv.ovf.u4.un");
    Add(ILOpCode.Conv_ovf_i8_un, "conv.ovf.i8.un");
    Add(ILOpCode.Conv_ovf_u8_un, "conv.ovf.u8.un");
    Add(ILOpCode.Add_ovf, "add.ovf");
    Add(ILOpCode.Add_ovf_un, "add.ovf.un");
    Add(ILOpCode.Mul_ovf, "mul.ovf");
    Add(ILOpCode.Mul_ovf_un, "mul.ovf.un");
    Add(ILOpCode.Sub_ovf, "sub.ovf");
    Add(ILOpCode.Sub_ovf_un, "sub.ovf.un");
    Add(ILOpCode.Callvirt, "callvirt", OperandKind.Token);
    Add(ILOpCode.Ldstr, "ldstr", OperandKind.Token);
    Add(ILOpCode.Newobj, "newobj", OperandKind.Token);
    Add(ILOpCode.Throw, "throw");
    Add(ILOpCode.Ldfld, "ldfld", OperandKind.Token);
    Add(ILOpCode.Ldflda, "ldflda", OperandKind.Token);
    Add(ILOpCode.Stfld, "stfld", OperandKind.Token);
    Add(ILOpCode.Ldsfld, "ldsfld", OperandKind.Token);
    Add(ILOpCode.Ldsflda, "ldsflda", OperandKind.Token);
    Add(ILOpCode.Stsfld, "stsfld", OperandKind.Token);
    Add(ILOpCode.Box, "box", OperandKind.Token);
    Add(ILOpCode.Newarr, "newarr", OperandKind.Token);
    Add(ILOpCode.Ldlen, "ldlen");
    Add(ILOpCode.Ldelema, "ldelema", OperandKind.Token);
    Add(ILOpCode.Ldelem_i1, "ldelem.i1");
    Add(ILOpCode.Ldelem_u1, "ldelem.u1");
    Add(ILOpCode.Ldelem_i2, "ldelem.i2");
    Add(ILOpCode.Ldelem_u2, "ldelem.u2");
    Add(ILOpCode.Ldelem_i4, "ldelem.i4");
    Add(ILOpCode.Ldelem_u4, "ldelem.u4");
    Add(ILOpCode.Ldelem_i8, "ldelem.i8");
    Add(ILOpCode.Ldelem_r4, "ldelem.r4");
    Add(ILOpCode.Ldelem_r8, "ldelem.r8");
    Add(ILOpCode.Ldelem_ref, "ldelem.ref");
    Add(ILOpCode.Stelem_i1, "stelem.i1");
    Add(ILOpCode.Stelem_i2, "stelem.i2");
    Add(ILOpCode.Stelem_i4, "stelem.i4");
    Add(ILOpCode.Stelem_i8, "stelem.i8");
    Add(ILOpCode.Stelem_r4, "stelem.r4");
    Add(ILOpCode.Stelem_r8, "stelem.r8");
    Add(ILOpCode.Stelem_ref, "stelem.ref");
    Add(ILOpCode.Ceq, "ceq");
    Add(ILOpCode.Cgt, "cgt");
    Add(ILOpCode.Cgt_un, "cgt.un");
    Add(ILOpCode.Clt, "clt");
    Add(ILOpCode.Clt_un, "clt.un");
    Add(ILOpCode.Ldarg, "ldarg", OperandKind.Var);
    Add(ILOpCode.Starg, "starg", OperandKind.Var);
    Add(ILOpCode.Ldloc, "ldloc", OperandKind.Var);
    Add(ILOpCode.Stloc, "stloc", OperandKind.Var);
    Add(ILOpCode.Initobj, "initobj", OperandKind.Token);
    Add(ILOpCode.Leave, "leave", OperandKind.Branch);
    Add(ILOpCode.Leave_s, "leave.s", OperandKind.ShortBranch);
    Add(ILOpCode.Endfinally, "endfinally");
    Add(ILOpCode.Rethrow, "rethrow");
    return table;
  }

}
