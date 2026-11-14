using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Polypiler.Tooling.Ci;

/// <summary>
/// The layers a change has to run: every layer that runs <c>always</c>, and every layer one of whose
/// globs matches a changed path. No change removes a layer that runs always, so the result is never
/// empty while one exists.
/// </summary>
public static class CiMatrix {

  /// <summary>The names of the layers to run, sorted.</summary>
  /// <param name="layers">The layers of the repository.</param>
  /// <param name="changed">The changed paths, relative to the repository root; blank lines are ignored.</param>
  /// <param name="all">Whether to run every layer whatever changed.</param>
  /// <returns>The names, in ordinal order.</returns>
  public static IReadOnlyList<string> Select(IEnumerable<CiLayer> layers, IEnumerable<string> changed, bool all) {
    ArgumentNullException.ThrowIfNull(layers);
    ArgumentNullException.ThrowIfNull(changed);
    var paths = changed.Select(path => path.Trim()).Where(path => path.Length > 0).ToArray();
    return [.. layers.Where(layer => all || layer.Always || paths.Any(layer.Selects)).Select(layer => layer.Name).Order(StringComparer.Ordinal)];
  }

  /// <summary>The names as one line of JSON, for a <c>matrix</c> of GitHub Actions: <c>["core","rust"]</c>.</summary>
  /// <param name="names">The names.</param>
  /// <returns>The JSON array.</returns>
  public static string Json(IReadOnlyList<string> names) => JsonSerializer.Serialize(names);

}
