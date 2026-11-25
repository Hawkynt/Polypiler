using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.Jvm.ClassFile;
using Polypiler.Core.Frontends.Jvm.Lowering;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Jvm;

/// <summary>Options controlling how a JVM <c>.class</c> file or <c>.jar</c> archive is imported.</summary>
public sealed record JvmImportOptions {

  /// <summary>Optional list of method names (simple or class-qualified) to export as public IR functions.</summary>
  public IReadOnlyList<string>? Exports { get; init; }

}

/// <summary>The result of importing a JVM <c>.class</c> file or <c>.jar</c> archive.</summary>
/// <param name="Module">The lifted, verified IR module, or <see langword="null"/> when diagnostics were reported.</param>
/// <param name="Diagnostics">Errors encountered while reading or lifting the input; empty when <paramref name="Module"/> is set.</param>
public sealed record JvmImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Imports JVM <c>.class</c> files (JVMS Chapter 4, major version 50+) and <c>.jar</c> archives into
/// Polypiler's structured typed IR, preserving exact JVM arithmetic, shift, conversion, array,
/// static-field, exception and JDK intrinsic semantics.
/// </summary>
public static class JvmFrontend {

  /// <summary>Input is empty or neither a JVM <c>.class</c> (<c>0xCAFEBABE</c>) nor a <c>.jar</c> (<c>PK\x03\x04</c>) archive.</summary>
  public const string NotClassOrJarCode = "PLY3100";

  /// <summary>Truncated or structurally corrupt class file, invalid constant pool, or unreadable archive.</summary>
  public const string CorruptClassFileCode = "PLY3101";

  /// <summary>Class file major version is below 50 (pre-Java 6 <c>jsr</c>/<c>ret</c> bytecode).</summary>
  public const string UnsupportedClassVersionCode = "PLY3102";

  /// <summary>A requested export name was not found in the imported class or archive.</summary>
  public const string ExportNotFoundCode = "PLY3103";

  /// <summary>A bytecode instruction is outside the supported JVM subset.</summary>
  public const string UnsupportedOpcodeCode = "PLY3104";

  /// <summary>A field or method descriptor uses a type outside the supported JVM subset.</summary>
  public const string UnsupportedDescriptorCode = "PLY3105";

  /// <summary>A <c>StackMapTable</c> attribute is malformed or references an out-of-bounds offset or constant pool entry.</summary>
  public const string InvalidStackMapCode = "PLY3106";

  /// <summary>The <c>invokedynamic</c> instruction is not supported by the JVM frontend.</summary>
  public const string InvokeDynamicUnsupportedCode = "PLY3107";

  /// <summary>A method has no liftable body (abstract, native, or empty <c>Code</c> attribute).</summary>
  public const string UnsupportedMethodBodyCode = "PLY3108";

  /// <summary>Registered diagnostic descriptor for <see cref="NotClassOrJarCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotClassOrJar = new(
    NotClassOrJarCode, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "jvm.frontend.format");

  /// <summary>Registered diagnostic descriptor for <see cref="CorruptClassFileCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor CorruptClassFile = new(
    CorruptClassFileCode, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "jvm.frontend.corrupt");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedClassVersionCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedClassVersion = new(
    UnsupportedClassVersionCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "jvm.frontend.version");

  /// <summary>Registered diagnostic descriptor for <see cref="ExportNotFoundCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ExportNotFound = new(
    ExportNotFoundCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "jvm.frontend.export");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedOpcodeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedOpcode = new(
    UnsupportedOpcodeCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "jvm.frontend.opcode");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedDescriptorCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDescriptor = new(
    UnsupportedDescriptorCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "jvm.frontend.descriptor");

  /// <summary>Registered diagnostic descriptor for <see cref="InvalidStackMapCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InvalidStackMap = new(
    InvalidStackMapCode, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "jvm.frontend.stackmap");

  /// <summary>Registered diagnostic descriptor for <see cref="InvokeDynamicUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InvokeDynamicUnsupported = new(
    InvokeDynamicUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "jvm.frontend.invokedynamic");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedMethodBodyCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedMethodBody = new(
    UnsupportedMethodBodyCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "jvm.frontend.method-body");

  /// <summary>Imports a JVM <c>.class</c> file or <c>.jar</c> archive into a verified PolyIR module.</summary>
  /// <param name="fileName">Logical file name used for module naming and diagnostics.</param>
  /// <param name="bytes">Raw bytes of the <c>.class</c> file or <c>.jar</c> archive.</param>
  /// <param name="options">Optional import settings such as explicit exports.</param>
  /// <returns>The lifted module or the diagnostics explaining why the input was refused.</returns>
  public static JvmImportResult Import(string fileName, byte[] bytes, JvmImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(bytes);
    var diagnostics = new List<Diagnostic>();
    var classes = JvmClassReader.LoadArchiveOrClass(fileName, bytes, diagnostics);
    if (diagnostics.Count > 0)
      return new(null, diagnostics);

    return JvmModuleLifter.Lift(fileName, classes, options ?? new(), diagnostics);
  }

}
