using System;

namespace Polypiler.Core.Verification.Images;

/// <summary>The diagnostics of value images and call signatures, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class ImageDiagnostics {

  /// <summary>
  /// <c>PLY0064</c>: a value image, an image type or a call signature is invalid - an unknown type, a
  /// value that does not fit its type, an image that is truncated, has bytes left over or is not
  /// canonical. Argument: the reason.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Invalid = new("PLY0064", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "verification.images");

}

/// <summary>
/// A value image, an image type or a call signature that is invalid: reported as <c>PLY0064</c>. It
/// is a <see cref="FormatException"/>, so a reader of vector files that refuses a malformed file
/// refuses one with a malformed image the same way.
/// </summary>
public sealed class ImageException : FormatException {

  /// <summary>Creates the exception.</summary>
  /// <param name="reason">What is wrong.</param>
  public ImageException(string reason) : base(reason) { }

  /// <summary>The problem as the diagnostic <c>PLY0064</c>.</summary>
  public Diagnostic ToDiagnostic() => Diagnostic.Create(ImageDiagnostics.Invalid, null, this.Message);

}
