using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>Options controlling how a .NET MSIL assembly is imported.</summary>
public sealed record MsilImportOptions {

  /// <summary>
  /// Optional list of exported method selectors (<c>MethodName</c>, <c>TypeName.MethodName</c>, or
  /// <c>Namespace.TypeName.MethodName</c>). When <see langword="null"/> or empty, all eligible
  /// static methods in the assembly are imported.
  /// </summary>
  public IReadOnlyList<string>? Exports { get; init; }

}

/// <summary>The result of importing a .NET MSIL assembly.</summary>
/// <param name="Module">The verified IR module, or <see langword="null"/> when import failed.</param>
/// <param name="Diagnostics">Diagnostics emitted during loading, decoding, or lifting.</param>
public sealed record MsilImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Imports .NET PE32 and PE32+ IL-only assemblies directly from bytes via
/// <see cref="System.Reflection.PortableExecutable.PEReader"/> and <see cref="MetadataReader"/>,
/// lifting ECMA-335 CIL method bodies into Polypiler's verified structured typed IR.
/// </summary>
public static class MsilFrontend {

  /// <summary>Input bytes are not a valid PE/CLI managed assembly.</summary>
  public const string NotPeOrCliCode = "PLY3000";

  /// <summary>CLI metadata tables, signatures, or method headers are corrupt.</summary>
  public const string CorruptMetadataCode = "PLY3001";

  /// <summary>A requested export was not found or matched more than one method.</summary>
  public const string ExportNotFoundCode = "PLY3002";

  /// <summary>Mixed-mode (non-ILOnly) assemblies are not supported.</summary>
  public const string MixedModeUnsupportedCode = "PLY3003";

  /// <summary>Method has no IL body (abstract, extern, P/Invoke, or non-IL implementation).</summary>
  public const string UnsupportedMethodBodyCode = "PLY3004";

  /// <summary>IL opcode is outside the supported subset.</summary>
  public const string UnsupportedOpcodeCode = "PLY3005";

  /// <summary>CLR type is outside the supported subset.</summary>
  public const string UnsupportedTypeCode = "PLY3006";

  /// <summary>Evaluation stack depth or type state is unverifiable.</summary>
  public const string UnverifiableStackCode = "PLY3007";

  /// <summary>Call target is neither an intra-assembly static method nor a recognized BCL intrinsic.</summary>
  public const string UnsupportedCallCode = "PLY3008";

  /// <summary>Registered diagnostic descriptor for <see cref="NotPeOrCliCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotPeOrCli = new(
    NotPeOrCliCode, DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "{0}", "msil.frontend.pe");

  /// <summary>Registered diagnostic descriptor for <see cref="CorruptMetadataCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor CorruptMetadata = new(
    CorruptMetadataCode, DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "{0}", "msil.frontend.metadata");

  /// <summary>Registered diagnostic descriptor for <see cref="ExportNotFoundCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ExportNotFound = new(
    ExportNotFoundCode, DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "{0}", "msil.frontend.export");

  /// <summary>Registered diagnostic descriptor for <see cref="MixedModeUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MixedModeUnsupported = new(
    MixedModeUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "msil.frontend.mixed-mode");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedMethodBodyCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedMethodBody = new(
    UnsupportedMethodBodyCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "msil.frontend.method-body");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedOpcodeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedOpcode = new(
    UnsupportedOpcodeCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "msil.frontend.opcode");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedTypeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedType = new(
    UnsupportedTypeCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "msil.frontend.type");

  /// <summary>Registered diagnostic descriptor for <see cref="UnverifiableStackCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnverifiableStack = new(
    UnverifiableStackCode, DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "{0}", "msil.frontend.stack");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedCallCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedCall = new(
    UnsupportedCallCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "msil.frontend.call");

  /// <summary>Imports a .NET assembly from raw PE bytes into a verified <see cref="IrModule"/>.</summary>
  /// <param name="fileName">Logical file name used in diagnostics and fallback module naming.</param>
  /// <param name="assemblyBytes">Raw bytes of the PE32 or PE32+ managed assembly.</param>
  /// <param name="options">Optional import configuration such as export filters.</param>
  /// <returns>The verified IR module or error diagnostics.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="fileName"/> or <paramref name="assemblyBytes"/> is <see langword="null"/>.</exception>
  public static MsilImportResult Import(string fileName, byte[] assemblyBytes, MsilImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(assemblyBytes);

    try {
      using var loader = PeMetadataLoader.Load(fileName, assemblyBytes);
      var module = loader.LiftModule(options);
      return new(module, []);
    } catch (MsilImportException ex) {
      return new(null, [ex.Descriptor.At(ex.Message, fileName)]);
    } catch (BadImageFormatException ex) {
      return new(null, [CorruptMetadata.At($"corrupt CLI metadata in '{fileName}': {ex.Message}", fileName)]);
    }
  }

}
