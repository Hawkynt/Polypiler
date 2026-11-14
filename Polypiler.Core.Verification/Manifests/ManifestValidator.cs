using System;
using System.IO;

namespace Polypiler.Core.Verification.Manifests;

/// <summary>
/// Checks a manifest file: JSON, the schema (<c>PLY0053</c>), then the state rule (<c>PLY0054</c>),
/// which it recomputes from the recorded evidence and compares with the written state.
/// </summary>
public static class ManifestValidator {

  /// <summary>Checks a manifest file against <see cref="ManifestSchema.Default"/>.</summary>
  /// <param name="path">The file; the diagnostics name it as given.</param>
  /// <param name="sink">Receives the diagnostics.</param>
  /// <returns>Whether the manifest is valid.</returns>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public static bool Validate(string path, IDiagnosticSink sink) => Validate(path, sink, ManifestSchema.Default);

  /// <summary>Checks a manifest file against a schema.</summary>
  /// <param name="path">The file; the diagnostics name it as given.</param>
  /// <param name="sink">Receives the diagnostics.</param>
  /// <param name="schema">The schema, for example <see cref="ManifestSchema.Load(string)"/> of <c>spec/schemas</c>.</param>
  /// <returns>Whether the manifest is valid.</returns>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public static bool Validate(string path, IDiagnosticSink sink, ManifestSchema schema) => ManifestReader.Read(path, sink, schema) is not null;

}
