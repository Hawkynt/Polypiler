using System;
using System.Collections.Generic;
using System.IO;

namespace Polypiler.Core.Frontends.Rust.Cargo;

/// <summary>Why the overflow mode of a Cargo profile cannot be read: a profile that is not defined, a manifest that is not TOML, a value that is no boolean.</summary>
/// <param name="message">What is wrong, naming the profile and the manifest.</param>
public sealed class CargoProfileException(string message) : Exception(message);

/// <summary>An overflow mode and where it came from, as the manifest of a translation records it.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="Origin">Where it came from: the Cargo profile and the manifest, relative to the source's folder, or its default.</param>
public sealed record OverflowModeChoice(OverflowChecks Mode, string Origin);

/// <summary>
/// The overflow mode a Rust source is built with when the command line does not say: what Cargo
/// passes to <c>rustc</c> as <c>-C overflow-checks</c> for the profile that applies (The Cargo Book,
/// "Profiles"). Cargo reads the profiles of the workspace root only, so the manifest that counts is
/// the nearest <c>Cargo.toml</c> above the source, or the workspace root it belongs to: the one its
/// <c>package.workspace</c> names, else the nearest one above it with a <c>[workspace]</c> table.
/// A profile's own <c>overflow-checks</c> wins; without one it inherits: <c>dev</c> is on,
/// <c>release</c> is off, <c>test</c> inherits <c>dev</c>, <c>bench</c> <c>release</c>, and a custom
/// profile the one its <c>inherits</c> names. Without a <c>Cargo.toml</c> the built-in profiles
/// keep their defaults: <c>dev</c>, the profile of a plain <c>cargo build</c>, panics.
/// </summary>
public static class CargoOverflowMode {

  /// <summary>The profile of a plain <c>cargo build</c>.</summary>
  public const string DefaultProfile = "dev";

  /// <summary>The file name of a Cargo manifest.</summary>
  public const string ManifestName = "Cargo.toml";

  /// <summary>The mode for a source file under a profile, from the manifest that applies to it.</summary>
  /// <param name="sourcePath">The path of the source file.</param>
  /// <param name="profile">The Cargo profile, <see cref="DefaultProfile"/> when the command line names none.</param>
  /// <returns>The mode and its origin.</returns>
  /// <exception cref="CargoProfileException">The profile is not defined or the manifest cannot be read as one.</exception>
  public static OverflowModeChoice ForSource(string sourcePath, string profile = DefaultProfile) {
    ArgumentNullException.ThrowIfNull(sourcePath);
    ArgumentNullException.ThrowIfNull(profile);
    var folder = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
    var nearest = Above(folder, _ => true);
    if (nearest is null) {
      var mode = Builtin(profile) ?? throw new CargoProfileException($"Cargo profile '{profile}' is not built in, and there is no {ManifestName} above the source to define it");
      return new(mode, $"the default of Cargo profile '{profile}', no {ManifestName} above the source");
    }

    var (manifest, document) = Root(nearest.Value.Path, nearest.Value.Document);
    var relative = Path.GetRelativePath(folder, manifest).Replace('\\', '/');
    return new(FromDocument(document, profile, relative), $"Cargo profile '{profile}' of {relative}");
  }

  /// <summary>The mode of a profile in the text of a workspace root's manifest.</summary>
  /// <param name="manifest">The text of the <c>Cargo.toml</c>.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="name">How messages name the manifest.</param>
  /// <returns>The mode.</returns>
  /// <exception cref="CargoProfileException">The profile is not defined or the text is not TOML.</exception>
  public static OverflowChecks FromManifest(string manifest, string profile, string name = ManifestName) {
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(profile);
    return FromDocument(Parse(manifest, name), profile, name);
  }

  /// <summary>The mode of a built-in profile that its manifest does not change; <see langword="null"/> for a custom one.</summary>
  /// <param name="profile">The profile.</param>
  public static OverflowChecks? Builtin(string profile) => profile switch {
    "dev" or "test" => OverflowChecks.Panic,
    "release" or "bench" => OverflowChecks.Wrap,
    _ => null
  };

  private static Dictionary<string, object> Parse(string text, string name) {
    try {
      return TomlReader.Read(text);
    } catch (TomlException error) {
      throw new CargoProfileException($"{name} is no TOML: {error.Message}");
    }
  }

  /// <summary>The nearest manifest at or above a folder that satisfies a condition, read.</summary>
  private static (string Path, Dictionary<string, object> Document)? Above(string folder, Func<Dictionary<string, object>, bool> condition) {
    for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent) {
      var path = Path.Combine(current.FullName, ManifestName);
      if (!File.Exists(path))
        continue;

      var document = Parse(File.ReadAllText(path), path);
      if (condition(document))
        return (path, document);
    }

    return null;
  }

  /// <summary>The manifest whose profiles apply to a package: the workspace root it names or lies in, else its own.</summary>
  private static (string Path, Dictionary<string, object> Document) Root(string path, Dictionary<string, object> document) {
    if (document.ContainsKey("workspace"))
      return (path, document);

    var folder = Path.GetDirectoryName(path)!;
    if (document.GetValueOrDefault("package") is Dictionary<string, object> package && package.GetValueOrDefault("workspace") is string named) {
      var root = Path.GetFullPath(Path.Combine(folder, named, ManifestName));
      if (!File.Exists(root))
        throw new CargoProfileException($"the workspace '{named}' of {path} has no {ManifestName}");
      return (root, Parse(File.ReadAllText(root), root));
    }

    return Path.GetDirectoryName(folder) is { } parent && Above(parent, candidate => candidate.ContainsKey("workspace")) is { } workspace
      ? workspace
      : (path, document);
  }

  private static OverflowChecks FromDocument(Dictionary<string, object> document, string profile, string name) {
    var profiles = document.GetValueOrDefault("profile") as Dictionary<string, object>;
    var seen = new List<string>();
    for (var current = profile; ;) {
      if (seen.Contains(current))
        throw new CargoProfileException($"Cargo profile '{profile}' of {name} inherits in a circle: {string.Join(" -> ", seen)} -> {current}");
      seen.Add(current);

      var table = profiles?.GetValueOrDefault(current) as Dictionary<string, object>;
      switch (table?.GetValueOrDefault("overflow-checks")) {
        case bool on:
          return on ? OverflowChecks.Panic : OverflowChecks.Wrap;
        case { } other:
          throw new CargoProfileException($"'overflow-checks' of [profile.{current}] in {name} is no boolean: {(other is string text ? '"' + text + '"' : other is TomlRaw raw ? raw.Text : "a table or an array")}");
      }

      switch (current) {
        case "dev" or "release":
          return Builtin(current)!.Value;
        case "test":
          current = "dev";
          continue;
        case "bench":
          current = "release";
          continue;
      }

      if (table is null)
        throw new CargoProfileException($"Cargo profile '{current}' is not defined in {name}");
      current = table.GetValueOrDefault("inherits") as string
        ?? throw new CargoProfileException($"Cargo profile '{current}' of {name} has no 'inherits', which a custom profile needs");
    }
  }

}
