using System.Reflection;

namespace Polypiler.Core;

/// <summary>Identity of the Polypiler product as reported by every entry point.</summary>
public static class ProductInfo {

  /// <summary>The single public product and command name.</summary>
  public const string Name = "polypiler";

  /// <summary>Reported when an assembly carries no informational version.</summary>
  public const string UnknownVersion = "0.0.0";

  /// <summary>The informational version of the core assembly, without build metadata.</summary>
  public static string Version
    => VersionOf(typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>());

  /// <summary>The version an informational-version attribute declares, without build metadata.</summary>
  /// <param name="attribute">The attribute, or <see langword="null"/> when the assembly has none.</param>
  /// <returns>The declared version, or <see cref="UnknownVersion"/>.</returns>
  public static string VersionOf(AssemblyInformationalVersionAttribute? attribute)
    => StripBuildMetadata(attribute?.InformationalVersion);

  /// <summary>Removes SemVer build metadata (everything from the first <c>+</c>) from a version string.</summary>
  /// <param name="informationalVersion">The raw informational version, or <see langword="null"/> when absent.</param>
  /// <returns>The version without build metadata, or <see cref="UnknownVersion"/> when none was given.</returns>
  public static string StripBuildMetadata(string? informationalVersion) {
    if (string.IsNullOrEmpty(informationalVersion))
      return UnknownVersion;

    var metadata = informationalVersion.IndexOf('+');
    return metadata < 0 ? informationalVersion : informationalVersion[..metadata];
  }

}
