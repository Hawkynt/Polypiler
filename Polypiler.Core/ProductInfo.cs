using System.Reflection;

namespace Polypiler.Core;

/// <summary>Identity of the Polypiler product as reported by every entry point.</summary>
public static class ProductInfo {

  /// <summary>The single public product and command name.</summary>
  public const string Name = "polypiler";

  /// <summary>The informational version of the core assembly.</summary>
  public static string Version {
    get {
      var attribute = typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
      var version = attribute?.InformationalVersion ?? "0.0.0";
      var metadata = version.IndexOf('+');
      return metadata < 0 ? version : version[..metadata];
    }
  }

}
