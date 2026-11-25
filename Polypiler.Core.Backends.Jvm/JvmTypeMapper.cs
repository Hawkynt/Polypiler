using System;
using System.Text;
using Polypiler.Core.Backends.Jvm.Bytecode;
using Polypiler.Core.Backends.Jvm.ClassFile;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

/// <summary>
/// Maps IR types and exception classes to JVM descriptors, verification types, array element tags,
/// and JDK exception internal names.
/// </summary>
internal static class JvmTypeMapper {

  /// <summary>Whether an <see cref="IrType"/> is supported by the JVM backend.</summary>
  public static bool IsSupportedType(IrType type) {
    if (type == IrType.Void || type == IrType.Bool || type.IsNumeric || type.IsString)
      return true;
    if (type.IsEnum)
      return true;
    if (type.IsOptional && type.Underlying is { } under && (under == IrType.Bool || under.IsNumeric || under.IsEnum))
      return true;
    if (type.IsArray && type.Element is { } elem && (elem == IrType.Bool || elem.IsNumeric || elem.IsString || elem.IsEnum))
      return true;
    if (type.ClassType is { } cls && IrExceptionClass.IsLibrary(cls))
      return true;

    return false;
  }

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

  /// <summary>Returns the JVM field/parameter/return descriptor for a supported IR type.</summary>
  public static string Descriptor(IrType type) {
    if (type.IsEnum)
      return Descriptor(type.Integral);
    if (type.IsOptional)
      return "L" + BoxInfo(type.Underlying!).BoxInternalName + ";";
    if (type.IsArray)
      return "[" + Descriptor(type.Element!);
    if (type.ClassType is { } cls && IrExceptionClass.IsLibrary(cls))
      return "L" + ExceptionInternalName(cls) + ";";

    return type.Kind switch {
      IrTypeKind.Void => "V",
      IrTypeKind.Bool => "Z",
      IrTypeKind.I8 or IrTypeKind.U8 => "B",
      IrTypeKind.I16 => "S",
      IrTypeKind.U16 => "C",
      IrTypeKind.I32 or IrTypeKind.U32 => "I",
      IrTypeKind.I64 or IrTypeKind.U64 => "J",
      IrTypeKind.F32 => "F",
      IrTypeKind.F64 => "D",
      _ => "Ljava/lang/String;"
    };
  }

  /// <summary>Returns the JVM method descriptor for an <see cref="IrFunction"/>.</summary>
  public static string MethodDescriptor(IrFunction function) {
    var sb = new StringBuilder("(");
    foreach (var param in function.Parameters)
      sb.Append(Descriptor(param.Type));
    sb.Append(')').Append(Descriptor(function.ReturnType));
    return sb.ToString();
  }

  /// <summary>Returns the JVMS §4.7.4 verification type for a supported non-void IR type.</summary>
  public static VType VerificationType(IrType type, ConstantPool pool) {
    if (type.IsEnum)
      return VerificationType(type.Integral, pool);
    if (type.IsOptional)
      return VType.Object(pool.AddClass(BoxInfo(type.Underlying!).BoxInternalName));
    if (type.IsArray)
      return VType.Object(pool.AddClass(Descriptor(type)));
    if (type.ClassType is { } cls && IrExceptionClass.IsLibrary(cls))
      return VType.Object(pool.AddClass(ExceptionInternalName(cls)));

    return type.Kind switch {
      IrTypeKind.I64 or IrTypeKind.U64 => VType.Long,
      IrTypeKind.F32 => VType.Float,
      IrTypeKind.F64 => VType.Double,
      IrTypeKind.String => VType.Object(pool.AddClass("java/lang/String")),
      _ => VType.Int
    };
  }

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
  public static JvmOpcode ReturnOpcode(IrType type) => type.Integral.Kind switch {
    IrTypeKind.Void => JvmOpcode.Return,
    IrTypeKind.I64 or IrTypeKind.U64 => JvmOpcode.Lreturn,
    IrTypeKind.F32 => JvmOpcode.Freturn,
    IrTypeKind.F64 => JvmOpcode.Dreturn,
    IrTypeKind.String or IrTypeKind.Array or IrTypeKind.Object or IrTypeKind.Optional => JvmOpcode.Areturn,
    _ => JvmOpcode.Ireturn
  };

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

  /// <summary>Combines an optional dotted Java package name and simple class name into an internal JVM class name.</summary>
  public static string InternalClassName(string moduleName, string? packageName) {
    var simple = ClassName(moduleName);
    if (string.IsNullOrWhiteSpace(packageName))
      return simple;

    return packageName.Trim().Replace('.', '/') + "/" + simple;
  }

}
