using System;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// Finds the executable of a toolchain of <c>tooling/toolchains.lock</c>. The runner names toolchains
/// by key only; the lock and its locator live above this layer, so they reach the runner through this
/// port.
/// </summary>
public interface IToolchainResolver {

  /// <summary>Finds a toolchain's executable.</summary>
  /// <param name="toolchain">The toolchain's key in the lock.</param>
  /// <returns>The executable, or the diagnostic saying why there is none.</returns>
  ToolchainResolution Resolve(string toolchain);

}

/// <summary>A toolchain's executable, or why it cannot be used.</summary>
public sealed record ToolchainResolution {

  private ToolchainResolution(string? executable, Diagnostic? problem) {
    this.Executable = executable;
    this.Problem = problem;
  }

  /// <summary>The full path of the executable; <see langword="null"/> when the toolchain cannot be used.</summary>
  public string? Executable { get; }

  /// <summary>Why the toolchain cannot be used, for example <c>PLY0021</c>; <see langword="null"/> when it can.</summary>
  public Diagnostic? Problem { get; }

  /// <summary>The toolchain was found.</summary>
  /// <param name="executable">The full path of its executable.</param>
  public static ToolchainResolution Found(string executable) => new(executable ?? throw new ArgumentNullException(nameof(executable)), null);

  /// <summary>The toolchain cannot be used.</summary>
  /// <param name="problem">Why.</param>
  public static ToolchainResolution Failed(Diagnostic problem) => new(null, problem ?? throw new ArgumentNullException(nameof(problem)));

}
