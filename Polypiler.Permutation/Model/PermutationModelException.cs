using System;
using Polypiler.Core;

namespace Polypiler.Permutation.Model;

/// <summary>A mapping, a mapping text or a mapping document was refused; <see cref="Diagnostic"/> says why.</summary>
public sealed class PermutationModelException : Exception {

  /// <summary>Creates the exception for a diagnostic.</summary>
  /// <param name="diagnostic">The reason, a code of the permutation block such as <c>PLY0800</c>–<c>PLY0802</c> or <c>PLY0805</c>.</param>
  /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
  public PermutationModelException(Diagnostic diagnostic) : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Code + ": " + diagnostic.Message)
    => this.Diagnostic = diagnostic;

  /// <summary>The diagnostic that refused the input.</summary>
  public Diagnostic Diagnostic { get; }

}
