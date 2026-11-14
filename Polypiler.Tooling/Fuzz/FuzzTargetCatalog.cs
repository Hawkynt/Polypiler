using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Polypiler.Core.Verification.Fuzzing;

namespace Polypiler.Tooling.Fuzz;

/// <summary>
/// The fuzz targets <c>polypiler-tooling</c> can run: those of the Polypiler assemblies it references
/// and of every assembly named on the command line. The first target with an ID wins.
/// </summary>
public static class FuzzTargetCatalog {

  /// <summary>Loads the assemblies and discovers their targets.</summary>
  /// <param name="assemblies">The paths given with <c>--assembly</c>, relative to <paramref name="currentDirectory"/>.</param>
  /// <param name="currentDirectory">The directory relative paths start in.</param>
  /// <exception cref="FuzzUsageException">An assembly cannot be loaded; the message says which and why.</exception>
  public static IReadOnlyDictionary<string, IFuzzTarget> Load(IEnumerable<string> assemblies, string currentDirectory) {
    var loaded = new List<Assembly> { typeof(Program).Assembly };
    loaded.AddRange(typeof(Program).Assembly.GetReferencedAssemblies()
      .Where(name => name.Name!.StartsWith("Polypiler", StringComparison.Ordinal))
      .Select(Assembly.Load));
    foreach (var path in assemblies) {
      var full = Path.GetFullPath(path, currentDirectory);
      try {
        loaded.Add(Assembly.LoadFrom(full));
      } catch (Exception exception) when (exception is IOException or BadImageFormatException) {
        throw new FuzzUsageException($"assembly '{path}' cannot be loaded: {exception.Message}");
      }
    }

    var targets = new Dictionary<string, IFuzzTarget>(StringComparer.Ordinal);
    foreach (var target in loaded.Distinct().SelectMany(FuzzTargets.Discover))
      targets.TryAdd(target.Id, target);
    return targets;
  }

}
