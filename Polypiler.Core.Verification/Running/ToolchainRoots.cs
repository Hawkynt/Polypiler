using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Polypiler.Core.Verification.Running;

/// <summary>Which rule found a toolchain's install root.</summary>
public enum ToolchainRootKind {

  /// <summary>A Java development kit: the nearest folder above <c>bin</c> with a <c>release</c> file.</summary>
  Jdk,

  /// <summary>A toolchain of rustup: the folder above <c>bin</c> with <c>lib/rustlib</c>.</summary>
  RustToolchain,

  /// <summary>A rustup proxy in <c>CARGO_HOME/bin</c>, beside <c>rustup</c>: that folder, cargo's crate caches, and the default <c>RUSTUP_HOME</c>.</summary>
  Rustup,

  /// <summary>A Python virtual environment: the folder with <c>pyvenv.cfg</c>.</summary>
  PythonEnvironment,

  /// <summary>The prefix of the interpreter a virtual environment was made from, which its <c>pyvenv.cfg</c> names.</summary>
  PythonBase,

  /// <summary>node: the folder above <c>bin/node</c>.</summary>
  Node,

  /// <summary>Any other toolchain in a <c>bin</c> folder: the folder above it.</summary>
  Generic,

  /// <summary>No rule applied: only the executable's own folder.</summary>
  Folder,

  /// <summary>Even the executable's folder is too wide to show: only the executable itself.</summary>
  File

}

/// <summary>A folder (or, for <see cref="ToolchainRootKind.File"/>, a file) a toolchain needs to see.</summary>
/// <param name="Path">The full path.</param>
/// <param name="Kind">The rule that found it.</param>
/// <param name="Reason">Why a narrower fallback was taken; <see langword="null"/> when a rule applied.</param>
public sealed record ToolchainRoot(string Path, ToolchainRootKind Kind, string? Reason = null);

/// <summary>
/// Finds the install root of a toolchain from its executable: a toolchain needs more than its
/// <c>bin</c> folder (its libraries, its runtime, the environment it was installed into), and a
/// sandbox that hides the folder it is installed in has to show that root again, read-only. Each kind
/// of toolchain has a rule; where none applies, the executable's folder is the root.
/// </summary>
public static class ToolchainRoots {

  /// <summary>
  /// The roots of the toolchain an executable belongs to, for the executable and, when it is a link,
  /// for its final target too: both must stay visible.
  /// </summary>
  /// <param name="executable">The full path of the executable.</param>
  /// <param name="allowed">
  /// Whether a folder may be shown at all. A root that may not (the whole home folder, say) narrows to
  /// the executable's folder, and where that may not either, to the executable alone.
  /// </param>
  /// <returns>The roots, each once, in the order found.</returns>
  public static IReadOnlyList<ToolchainRoot> Of(string executable, Func<string, bool> allowed) {
    ArgumentException.ThrowIfNullOrEmpty(executable);
    ArgumentNullException.ThrowIfNull(allowed);
    var target = System.IO.File.Exists(executable) ? System.IO.File.ResolveLinkTarget(executable, true)?.FullName ?? executable : executable;
    return new[] { executable, target }
      .Distinct(StringComparer.Ordinal)
      .SelectMany(path => Narrowed(path, allowed))
      .DistinctBy(root => root.Path, StringComparer.Ordinal)
      .ToArray();
  }

  /// <summary>The roots of one executable, each narrowed until it is allowed.</summary>
  private static IEnumerable<ToolchainRoot> Narrowed(string executable, Func<string, bool> allowed) {
    var folder = System.IO.Path.GetDirectoryName(executable)!;
    foreach (var root in OfFolder(folder, System.IO.Path.GetFileName(executable))) {
      if (allowed(root.Path))
        yield return root;
      else if (root.Path != folder && allowed(folder))
        yield return new(folder, ToolchainRootKind.Folder, $"the {root.Kind} root '{root.Path}' would show too much");
      else
        yield return new(executable, ToolchainRootKind.File, $"the folder '{root.Path}' would show too much");
    }
  }

  /// <summary>The roots of the toolchain whose executable lies in a folder.</summary>
  private static IEnumerable<ToolchainRoot> OfFolder(string folder, string name) {
    var parent = System.IO.Path.GetDirectoryName(folder);
    if (parent == null)
      return [new(folder, ToolchainRootKind.Folder, $"'{folder}' has no folder above it")];

    var environment = System.IO.Path.Combine(parent, "pyvenv.cfg");
    if (System.IO.File.Exists(environment))
      return PythonEnvironment(parent, environment);

    if (Directory.Exists(System.IO.Path.Combine(parent, "lib", "rustlib")))
      return [new(parent, ToolchainRootKind.RustToolchain)];

    var isBin = System.IO.Path.GetFileName(folder) == "bin";
    if (isBin && System.IO.File.Exists(System.IO.Path.Combine(folder, "rustup")))
      return Rustup(folder, parent);

    if (Jdk(parent) is { } jdk)
      return [new(jdk, ToolchainRootKind.Jdk)];

    if (isBin)
      return [new(parent, name == "node" ? ToolchainRootKind.Node : ToolchainRootKind.Generic)];

    return [new(folder, ToolchainRootKind.Folder, $"no root marker: '{folder}' is not a bin folder and has no pyvenv.cfg, lib/rustlib or JDK release file above it")];
  }

  /// <summary>The nearest of the folder above <c>bin</c> and the one above that (a JRE inside a JDK) with a JDK <c>release</c> file.</summary>
  private static string? Jdk(string parent) {
    var grandparent = System.IO.Path.GetDirectoryName(parent);
    return new[] { parent, grandparent }.OfType<string>().FirstOrDefault(candidate => System.IO.File.Exists(System.IO.Path.Combine(candidate, "release")));
  }

  /// <summary>The virtual environment and the prefix of its base interpreter: <c>home</c> of <c>pyvenv.cfg</c> names that interpreter's folder.</summary>
  private static IEnumerable<ToolchainRoot> PythonEnvironment(string root, string configuration) {
    yield return new(root, ToolchainRootKind.PythonEnvironment);
    var home = System.IO.File.ReadLines(configuration)
      .Select(line => line.Split('=', 2))
      .Where(pair => pair.Length == 2 && pair[0].Trim() == "home")
      .Select(pair => pair[1].Trim())
      .FirstOrDefault(value => System.IO.Path.IsPathFullyQualified(value));
    if (home == null)
      yield break;

    home = System.IO.Path.TrimEndingDirectorySeparator(home);
    var prefix = System.IO.Path.GetFileName(home) == "bin" ? System.IO.Path.GetDirectoryName(home)! : home;
    if (Directory.Exists(prefix))
      yield return new(prefix, ToolchainRootKind.PythonBase);
  }

  /// <summary>A rustup proxy: its folder, cargo's crate caches beside it, and rustup's default home beside cargo's.</summary>
  private static IEnumerable<ToolchainRoot> Rustup(string bin, string cargoHome) {
    yield return new(bin, ToolchainRootKind.Rustup);
    foreach (var cache in new[] { "registry", "git" }.Select(name => System.IO.Path.Combine(cargoHome, name)).Where(Directory.Exists))
      yield return new(cache, ToolchainRootKind.Rustup);

    var rustupHome = System.IO.Path.GetFullPath(System.IO.Path.Combine(cargoHome, "..", ".rustup"));
    if (Directory.Exists(rustupHome))
      yield return new(rustupHome, ToolchainRootKind.Rustup);
  }

}
