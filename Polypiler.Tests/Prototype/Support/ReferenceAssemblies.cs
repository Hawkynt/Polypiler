using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The reference assemblies of a C# profile: what a compiler for that framework sees of the base
/// library. They come from NuGet packages the test projects download without referencing
/// (<c>ReferenceAssemblies.props</c>), and they are what makes "compiles for .NET Framework 2.0" a
/// statement about .NET Framework 2.0 and not about the runtime the tests happen to run on.
/// </summary>
internal static class ReferenceAssemblies {

  private const string Prefix = "ReferenceAssemblies:";

  private static readonly Dictionary<string, string> _metadata = typeof(ReferenceAssemblies).Assembly
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? "");

  private static readonly Dictionary<string, IReadOnlyList<MetadataReference>> _loaded = [];

  /// <summary>The NuGet package folder the build restored into.</summary>
  public static string PackageRoot => _metadata["NuGetPackageRoot"];

  /// <summary>The profiles that name reference assemblies, in the order the project file lists them.</summary>
  public static IReadOnlyList<string> Profiles { get; } = [.. _metadata.Keys.Where(key => key.StartsWith(Prefix, StringComparison.Ordinal)).Select(key => key[Prefix.Length..])];

  /// <summary>The reference assemblies of a profile in the package folder of the build, read once.</summary>
  /// <param name="profile">The name of the profile.</param>
  /// <exception cref="InvalidOperationException">The package is not restored.</exception>
  public static IReadOnlyList<MetadataReference> Of(string profile) {
    lock (_loaded) {
      if (!_loaded.TryGetValue(profile, out var references))
        _loaded[profile] = references = Of(profile, PackageRoot);

      return references;
    }
  }

  /// <summary>The reference assemblies of a profile in a package folder.</summary>
  /// <param name="profile">The name of the profile.</param>
  /// <param name="packageRoot">The NuGet package folder.</param>
  /// <exception cref="InvalidOperationException">
  /// The package is not in the folder. A profile whose reference assemblies are missing is never
  /// compiled against something else instead: the test that asks for them fails.
  /// </exception>
  public static IReadOnlyList<MetadataReference> Of(string profile, string packageRoot) {
    var parts = _metadata[Prefix + profile].Split('|');
    var (package, version) = (parts[0], parts[1]);
    var directory = Path.Combine([packageRoot, package.ToLowerInvariant(), version, .. parts[2].Split('/')]);
    // A framework directory also holds native libraries and bare modules, which no compiler references.
    var assemblies = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.dll").Where(IsManaged).ToArray() : [];
    if (assemblies.Length == 0)
      throw new InvalidOperationException(
        $"The reference assemblies of the profile '{profile}' are missing: the package {package} {version} is not restored in '{packageRoot}' (no assembly in '{directory}').");

    return [.. assemblies.Order(StringComparer.Ordinal).Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(assembly))];
  }

  private static bool IsManaged(string path) {
    using var image = new PEReader(File.OpenRead(path));
    return image.HasMetadata && image.GetMetadataReader().IsAssembly;
  }

}
