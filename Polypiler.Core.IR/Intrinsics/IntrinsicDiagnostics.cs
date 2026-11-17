using System;

namespace Polypiler.Core.IR.Intrinsics;

/// <summary>
/// The diagnostics of the intrinsic registry. <c>PLY0250</c>, <c>PLY0252</c> and <c>PLY0255</c> say that
/// IR is invalid; the others fail the construction of a registry, a defect of Polypiler.
/// </summary>
public static class IntrinsicDiagnostics {

  /// <summary><c>PLY0250</c>: the IR names an intrinsic the registry does not have.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unknown = new("PLY0250", DiagnosticSeverity.Error, ExitCode.InternalError, "unknown intrinsic '{0}'", "intrinsics.registry.known");

  /// <summary><c>PLY0251</c>: a contract document is not one the loader can read.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor BadContract = new("PLY0251", DiagnosticSeverity.Error, ExitCode.InternalError, "intrinsic contract '{0}': {1}", "intrinsics.registry.contract");

  /// <summary><c>PLY0252</c>: an operand or the result of a node has a type the contract does not admit.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotAdmitted = new("PLY0252", DiagnosticSeverity.Error, ExitCode.InternalError, "intrinsic '{0}' does not admit {1} as {2}", "intrinsics.registry.types");

  /// <summary><c>PLY0253</c>: two contracts of one name.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Duplicate = new("PLY0253", DiagnosticSeverity.Error, ExitCode.InternalError, "intrinsic '{0}' is registered twice", "intrinsics.registry.unique");

  /// <summary><c>PLY0254</c>: a contract with neither a decomposition nor <c>requiresRuntime: true</c>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoFallback = new("PLY0254", DiagnosticSeverity.Error, ExitCode.InternalError, "intrinsic '{0}' has no fallback", "intrinsics.registry.fallback");

  /// <summary><c>PLY0255</c>: an attribute operand outside its declared range.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OutsideRange = new("PLY0255", DiagnosticSeverity.Error, ExitCode.InternalError, "intrinsic '{0}': attribute '{1}' = {2} is outside [{3}, {4}]", "intrinsics.registry.attribute");

  /// <summary><c>PLY0256</c>: the decompositions lower an intrinsic to itself.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Cycle = new("PLY0256", DiagnosticSeverity.Error, ExitCode.InternalError, "intrinsic '{0}' lowers to itself through {1}", "intrinsics.registry.acyclic");

  /// <summary>The message of <see cref="NotAdmitted"/>.</summary>
  internal static string NotAdmittedText(IntrinsicId id, string type, string position) => $"intrinsic '{id}' does not admit {type} as {position}";

}

/// <summary>Thrown when a registry cannot be built, or a contract document not read; it carries the diagnostic.</summary>
public sealed class IntrinsicRegistryException : Exception {

  /// <summary>Creates the exception.</summary>
  /// <param name="diagnostic">What is wrong.</param>
  public IntrinsicRegistryException(Diagnostic diagnostic) : base($"{diagnostic.Code}: {diagnostic.Message}") => this.Diagnostic = diagnostic;

  /// <summary>What is wrong.</summary>
  public Diagnostic Diagnostic { get; }

}
