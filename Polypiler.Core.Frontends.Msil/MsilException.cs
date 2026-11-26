using System;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>Internal exception carrying a structured frontend diagnostic.</summary>
internal sealed class MsilImportException(DiagnosticDescriptor descriptor, string message) : Exception(message) {

  /// <summary>The diagnostic descriptor describing the failure category.</summary>
  public DiagnosticDescriptor Descriptor { get; } = descriptor;

}
