using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.Jvm.Bytecode;
using Polypiler.Core.Backends.Jvm.ClassFile;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

/// <summary>
/// Maps IR types, declarations, and exception classes to JVM descriptors, verification types,
/// array element tags, synthetic helper class names, and JDK exception internal names.
/// </summary>
internal static class JvmTypeMapper {

  /// <summary>Whether an <see cref="IrType"/> is supported by the JVM backend.</summary>
  public static bool IsSupportedType(IrType type) => type.Kind switch {
    IrTypeKind.BigInt => false,
    IrTypeKind.Optional => IsSupportedType(type.Underlying!),
    IrTypeKind.Array or IrTypeKind.List or IrTypeKind.Set or IrTypeKind.Sequence or IrTypeKind.Async => IsSupportedType(type.Element!),
    IrTypeKind.Map => IsSupportedType(type.Key!) && IsSupportedType(type.Element!),
    IrTypeKind.Function => IsSupportedType(type.Signature!.Result) && type.Signature.Parameters.All(IsSupportedType),
    _ => true
  };

  /// <summary>Whether an IR type is represented as a JVM primitive (non-reference) value.</summary>
  public static bool IsJvmPrimitive(IrType type) {
    var integral = type.Integral;
    return integral == IrType.Bool || integral.IsNumeric;
  }

  /// <summary>Returns the number of JVM local/stack slots occupied by a non-void IR type (2 for 64-bit, 1 otherwise).</summary>
  public static int SlotCount(IrType type) => type.Integral.Kind is IrTypeKind.I64 or IrTypeKind.U64 or IrTypeKind.F64 ? 2 : 1;

  /// <summary>Returns the JVM boxed wrapper internal class name and unbox method name for a primitive/enum IR type.</summary>
  public static (string BoxInternalName, string UnboxMethod) BoxInfo(IrType underlying) => underlying.Integral.Kind switch {
    IrTypeKind.Bool => ("java/lang/Boolean", "booleanValue"),
    IrTypeKind.I8 or IrTypeKind.U8 => ("java/lang/Byte", "byteValue"),
    IrTypeKind.I16 => ("java/lang/Short", "shortValue"),
    IrTypeKind.U16 => ("java/lang/Character", "charValue"),
    IrTypeKind.I64 or IrTypeKind.U64 => ("java/lang/Long", "longValue"),
    IrTypeKind.F32 => ("java/lang/Float", "floatValue"),
    IrTypeKind.F64 => ("java/lang/Double", "doubleValue"),
    _ => ("java/lang/Integer", "intValue")
  };

  /// <summary>Returns the JVMS <c>newarray</c> type code for a primitive array element type.</summary>
  public static JvmArrayType PrimitiveArrayType(IrType element) => element.Integral.Kind switch {
    IrTypeKind.Bool => JvmArrayType.Boolean,
    IrTypeKind.I8 or IrTypeKind.U8 => JvmArrayType.Byte,
    IrTypeKind.I16 => JvmArrayType.Short,
    IrTypeKind.U16 => JvmArrayType.Char,
    IrTypeKind.I32 or IrTypeKind.U32 => JvmArrayType.Int,
    IrTypeKind.I64 or IrTypeKind.U64 => JvmArrayType.Long,
    IrTypeKind.F32 => JvmArrayType.Float,
    _ => JvmArrayType.Double
  };

  /// <summary>Returns the JVM array load opcode for an element type.</summary>
  public static JvmOpcode ArrayLoadOpcode(IrType element) => element.Integral.Kind switch {
    IrTypeKind.Bool or IrTypeKind.I8 or IrTypeKind.U8 => JvmOpcode.Baload,
    IrTypeKind.I16 => JvmOpcode.Saload,
    IrTypeKind.U16 => JvmOpcode.Caload,
    IrTypeKind.I32 or IrTypeKind.U32 => JvmOpcode.Iaload,
    IrTypeKind.I64 or IrTypeKind.U64 => JvmOpcode.Laload,
    IrTypeKind.F32 => JvmOpcode.Faload,
    IrTypeKind.F64 => JvmOpcode.Daload,
    _ => JvmOpcode.Aaload
  };

  /// <summary>Returns the JVM array store opcode for an element type.</summary>
  public static JvmOpcode ArrayStoreOpcode(IrType element) => element.Integral.Kind switch {
    IrTypeKind.Bool or IrTypeKind.I8 or IrTypeKind.U8 => JvmOpcode.Bastore,
    IrTypeKind.I16 => JvmOpcode.Sastore,
    IrTypeKind.U16 => JvmOpcode.Castore,
    IrTypeKind.I32 or IrTypeKind.U32 => JvmOpcode.Iastore,
    IrTypeKind.I64 or IrTypeKind.U64 => JvmOpcode.Lastore,
    IrTypeKind.F32 => JvmOpcode.Fastore,
    IrTypeKind.F64 => JvmOpcode.Dastore,
    _ => JvmOpcode.Aastore
  };

  /// <summary>Returns the JVM return opcode for a return type.</summary>
  public static JvmOpcode ReturnOpcode(IrType type) {
    if (type == IrType.Void)
      return JvmOpcode.Return;
    if (!IsJvmPrimitive(type))
      return JvmOpcode.Areturn;

    return type.Integral.Kind switch {
      IrTypeKind.I64 or IrTypeKind.U64 => JvmOpcode.Lreturn,
      IrTypeKind.F32 => JvmOpcode.Freturn,
      IrTypeKind.F64 => JvmOpcode.Dreturn,
      _ => JvmOpcode.Ireturn
    };
  }

  /// <summary>Maps an IR library exception class to its corresponding JDK exception internal name.</summary>
  public static string ExceptionInternalName(IrClassType cls) => cls.Name switch {
    "ArithmeticException" or "DivideByZeroException" or "OverflowException" => "java/lang/ArithmeticException",
    "IndexOutOfRangeException" => "java/lang/IndexOutOfBoundsException",
    "NullReferenceException" => "java/lang/NullPointerException",
    "InvalidCastException" => "java/lang/ClassCastException",
    "InvalidOperationException" => "java/lang/IllegalStateException",
    "ArgumentException" or "ArgumentOutOfRangeException" => "java/lang/IllegalArgumentException",
    "KeyNotFoundException" => "java/util/NoSuchElementException",
    "FormatException" => "java/lang/NumberFormatException",
    "AssertionException" => "java/lang/RuntimeException",
    _ => "java/lang/Exception"
  };

  /// <summary>Whether the JDK exception class has a direct <c>(String, Throwable)</c> constructor in Java 8+.</summary>
  public static bool HasMessageAndCauseCtor(string internalName)
    => internalName is "java/lang/Exception" or "java/lang/RuntimeException" or "java/lang/IllegalArgumentException" or "java/lang/IllegalStateException";

  /// <summary>Sanitizes an IR module name into a valid JVM simple class identifier.</summary>
  public static string ClassName(string moduleName) {
    var sb = new StringBuilder(moduleName.Length);
    foreach (var ch in moduleName)
      if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '$')
        sb.Append(ch);

    if (sb.Length == 0 || char.IsDigit(sb[0]))
      sb.Insert(0, '_');

    return sb.ToString();
  }

  /// <summary>Sanitizes a type, method, or field name into a valid, collision-free JVMS unqualified identifier.</summary>
  public static string SanitizeIdentifier(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var ch in name) {
      if (char.IsLetterOrDigit(ch) || ch is '_' or '$')
        sb.Append(ch);
      else
        sb.Append("$x").Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
    }

    return sb.ToString();
  }

}

/// <summary>
/// Resolves JVM internal class names, field/method descriptors, and verification types within one IR module.
/// </summary>
internal sealed class JvmModuleNames {

  private readonly string _packagePrefix;
  private readonly string _moduleSimpleName;
  private readonly Dictionary<IrTypeDeclaration, string> _declNames = new(ReferenceEqualityComparer.Instance);

  /// <summary>Initializes name resolution for <paramref name="module"/> with optional <paramref name="packageName"/>.</summary>
  public JvmModuleNames(IrModule module, string? packageName) {
    this._moduleSimpleName = JvmTypeMapper.ClassName(module.Name);
    this._packagePrefix = string.IsNullOrWhiteSpace(packageName) ? "" : packageName.Trim().Replace('.', '/') + "/";
    this.ModuleInternalName = this._packagePrefix + this._moduleSimpleName;

    var used = new HashSet<string>(StringComparer.Ordinal) { this.ModuleInternalName };
    foreach (var decl in module.Types) {
      if (decl is IrEnumType)
        continue;

      var simple = JvmTypeMapper.SanitizeIdentifier(decl.Name);
      var candidate = this._packagePrefix + simple;
      if (!used.Add(candidate)) {
        candidate = this._packagePrefix + simple + "$Type";
        used.Add(candidate);
      }

      this._declNames[decl] = candidate;
    }
  }

  /// <summary>The internal JVM name of the main module class.</summary>
  public string ModuleInternalName { get; }

  /// <summary>The internal JVM name of the module's restartable sequence interface.</summary>
  public string SequenceInterfaceName => this.ModuleInternalName + "$Seq";

  /// <summary>Returns the internal JVM name for a declared struct, class, interface, or library exception class.</summary>
  public string DeclarationInternalName(IrTypeDeclaration decl) {
    if (decl is IrClassType cls && IrExceptionClass.IsLibrary(cls))
      return JvmTypeMapper.ExceptionInternalName(cls);

    return this._declNames[decl];
  }

  /// <summary>Returns the JVM superclass internal name for a user <see cref="IrClassType"/>.</summary>
  public string SuperClassInternalName(IrClassType cls) {
    if (cls.Base is null)
      return "java/lang/Object";
    if (cls.Base == IrExceptionClass.Exception)
      return "java/lang/RuntimeException";

    return this.DeclarationInternalName(cls.Base);
  }

  /// <summary>Builds a deterministic short key for an IR type used in synthetic helper class names.</summary>
  public string TypeKey(IrType type) {
    if (type.IsFunction)
      return "Fn_" + this.FunctionKey(type.Signature!);

    return JvmTypeMapper.SanitizeIdentifier(this.Descriptor(type));
  }

  /// <summary>Builds a deterministic signature key for an <see cref="IrFunctionType"/>.</summary>
  public string FunctionKey(IrFunctionType sig)
    => string.Join("_", sig.Parameters.Select(this.TypeKey)) + "__" + this.TypeKey(sig.Result);

  /// <summary>Returns the internal JVM name of the functional interface for <paramref name="signature"/>.</summary>
  public string FunctionInterfaceName(IrFunctionType signature)
    => this.ModuleInternalName + "$Fn_" + this.FunctionKey(signature);

  /// <summary>Returns the internal JVM name of the mutable capture cell class for <paramref name="type"/>.</summary>
  public string CellClassName(IrType type)
    => this.ModuleInternalName + "$Cell_" + this.TypeKey(type);

  /// <summary>Returns the internal JVM name of the array-sequence wrapper class for <paramref name="element"/>.</summary>
  public string ArraySequenceClassName(IrType element)
    => this.ModuleInternalName + "$ArrSeq_" + this.TypeKey(element);

  /// <summary>Returns the internal JVM name of the span/memory slice helper class for <paramref name="element"/>.</summary>
  public string SpanClassName(IrType element)
    => this.ModuleInternalName + "$Span_" + this.TypeKey(element);

  /// <summary>Returns the internal JVM name of the bounded pointer helper class for <paramref name="element"/>.</summary>
  public string PointerClassName(IrType element)
    => element == IrType.Void ? "java/lang/Object" : this.ModuleInternalName + "$Ptr_" + this.TypeKey(element);

  /// <summary>Returns the internal JVM class/array name representing a non-primitive IR type.</summary>
  public string ReferenceInternalName(IrType type) {
    if (type.IsString)
      return "java/lang/String";
    if (type.IsOptional)
      return type.Underlying!.IsStruct ? this.DeclarationInternalName(type.Underlying.Declaration!) : JvmTypeMapper.BoxInfo(type.Underlying).BoxInternalName;
    if (type.IsArray)
      return this.Descriptor(type);
    if (type.IsStruct || type.IsObject)
      return this.DeclarationInternalName(type.Declaration!);
    if (type.IsFunction)
      return this.FunctionInterfaceName(type.Signature!);
    if (type.IsList)
      return "java/util/ArrayList";
    if (type.Kind == IrTypeKind.Map)
      return "java/util/LinkedHashMap";
    if (type.Kind == IrTypeKind.Set)
      return "java/util/LinkedHashSet";
    if (type.IsLazySequence)
      return this.SequenceInterfaceName;
    if (type.IsAsync)
      return "java/util/concurrent/CompletableFuture";
    if (type.IsView || type.IsMemory)
      return this.SpanClassName(type.Element!);

    return this.PointerClassName(type.Element!);
  }

  /// <summary>Returns the JVM field/parameter/return descriptor for a supported IR type.</summary>
  public string Descriptor(IrType type) {
    if (type.IsEnum)
      return this.Descriptor(type.Integral);
    if (type.IsArray)
      return "[" + this.Descriptor(type.Element!);
    if (type == IrType.Void)
      return "V";
    if (!JvmTypeMapper.IsJvmPrimitive(type))
      return "L" + this.ReferenceInternalName(type) + ";";

    return type.Kind switch {
      IrTypeKind.Bool => "Z",
      IrTypeKind.I8 or IrTypeKind.U8 => "B",
      IrTypeKind.I16 => "S",
      IrTypeKind.U16 => "C",
      IrTypeKind.I32 or IrTypeKind.U32 => "I",
      IrTypeKind.I64 or IrTypeKind.U64 => "J",
      IrTypeKind.F32 => "F",
      _ => "D"
    };
  }

  /// <summary>Returns the JVM method descriptor for parameter types and a return type.</summary>
  public string MethodDescriptor(IReadOnlyList<IrType> parameterTypes, IrType returnType) {
    var sb = new StringBuilder("(");
    foreach (var param in parameterTypes)
      sb.Append(this.Descriptor(param));
    sb.Append(')').Append(this.Descriptor(returnType));
    return sb.ToString();
  }

  /// <summary>Returns the JVM method descriptor for an <see cref="IrFunction"/>, optionally skipping the receiver parameter.</summary>
  public string MethodDescriptor(IrFunction function, bool skipFirstParam = false) {
    var sb = new StringBuilder("(");
    for (var i = skipFirstParam ? 1 : 0; i < function.Parameters.Count; ++i)
      sb.Append(this.Descriptor(function.Parameters[i].Type));
    sb.Append(')').Append(this.Descriptor(function.ReturnType));
    return sb.ToString();
  }

  /// <summary>Returns the JVM method descriptor for a foreign function declaration.</summary>
  public string MethodDescriptor(IrForeignFunction foreign) {
    var sb = new StringBuilder("(");
    foreach (var param in foreign.Parameters)
      sb.Append(this.Descriptor(param.Type));
    sb.Append(')').Append(this.Descriptor(foreign.ReturnType));
    return sb.ToString();
  }

  /// <summary>Returns the JVMS §4.7.4 verification type for a supported non-void IR type.</summary>
  public VType VerificationType(IrType type, ConstantPool pool) {
    if (type.IsEnum)
      return this.VerificationType(type.Integral, pool);
    if (!JvmTypeMapper.IsJvmPrimitive(type))
      return VType.Object(pool.AddClass(this.ReferenceInternalName(type)));

    return type.Kind switch {
      IrTypeKind.I64 or IrTypeKind.U64 => VType.Long,
      IrTypeKind.F32 => VType.Float,
      IrTypeKind.F64 => VType.Double,
      _ => VType.Int
    };
  }

}
