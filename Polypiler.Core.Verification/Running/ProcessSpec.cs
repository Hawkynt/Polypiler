using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// One process of a run: a toolchain of <c>tooling/toolchains.lock</c>, the arguments it is started
/// with, passed as an array and never through a shell, and the bytes it reads on standard input.
/// </summary>
/// <param name="Toolchain">The toolchain's key in the lock, for example <c>dotnet-sdk</c>; its executable is the process started.</param>
/// <param name="Arguments">The arguments, each passed as it is.</param>
/// <param name="Stdin">What the process reads on standard input; standard input is closed after it.</param>
public sealed record ProcessSpec(string Toolchain, IReadOnlyList<string> Arguments, ReadOnlyMemory<byte> Stdin) {

  /// <summary>A process that reads nothing on standard input.</summary>
  /// <param name="toolchain">The toolchain's key in the lock.</param>
  /// <param name="arguments">The arguments, each passed as it is.</param>
  public ProcessSpec(string toolchain, params IReadOnlyList<string> arguments) : this(toolchain, arguments, ReadOnlyMemory<byte>.Empty) { }

  /// <summary>
  /// The prefix of a <see cref="Toolchain"/> that names no toolchain but a program the build steps
  /// wrote into the run's directory, by its name relative to that directory: a native compiler's
  /// output, which only exists once the build has run.
  /// </summary>
  public const string BuiltPrefix = "built:";

  /// <summary>The toolchain's key in the lock, or <see cref="BuiltPrefix"/> and the name of a program the build wrote.</summary>
  /// <exception cref="ArgumentException">The key is empty, or names a built program outside the run's directory.</exception>
  public string Toolchain { get; } = CheckToolchain(Toolchain);

  /// <summary>
  /// The program, relative to the run's directory, a build step wrote and this step starts;
  /// <see langword="null"/> for a step that starts a toolchain.
  /// </summary>
  public string? BuiltProgram => this.Toolchain.StartsWith(BuiltPrefix, StringComparison.Ordinal) ? this.Toolchain[BuiltPrefix.Length..] : null;

  /// <summary>
  /// A step that starts a program the build steps wrote into the run's directory. A program the build
  /// did not write cannot be started: the run then throws as for any executable that cannot be.
  /// </summary>
  /// <param name="program">The program's name relative to the run's directory, with its extension where the system needs one (<c>.exe</c> on Windows).</param>
  /// <param name="arguments">The arguments, each passed as it is.</param>
  /// <exception cref="ArgumentException">The name is empty, absolute, or contains a <c>..</c> segment.</exception>
  public static ProcessSpec Built(string program, params IReadOnlyList<string> arguments) => new(BuiltPrefix + program, arguments);

  private static string CheckToolchain(string toolchain) {
    if (string.IsNullOrEmpty(toolchain))
      throw new ArgumentException("a process needs a toolchain", nameof(Toolchain));
    if (toolchain.StartsWith(BuiltPrefix, StringComparison.Ordinal) && !RunPlan.IsRelativeName(toolchain[BuiltPrefix.Length..]))
      throw new ArgumentException($"the built program '{toolchain[BuiltPrefix.Length..]}' leaves the working directory: only relative names without '..' are allowed", nameof(Toolchain));

    return toolchain;
  }

  /// <summary>The arguments, each passed as it is.</summary>
  public IReadOnlyList<string> Arguments { get; } = Arguments?.ToArray() ?? throw new ArgumentNullException(nameof(Arguments));

  /// <summary>
  /// Variables added to the scrubbed environment every step gets (<see cref="StepLauncher.Environment"/>);
  /// a name the launcher sets already is overridden. Empty unless given.
  /// </summary>
  /// <exception cref="ArgumentException">A name is empty or contains <c>=</c>.</exception>
  public IReadOnlyDictionary<string, string> Environment {
    get;
    init => field = CheckEnvironment(value);
  } = new Dictionary<string, string>(StringComparer.Ordinal);

  private static Dictionary<string, string> CheckEnvironment(IReadOnlyDictionary<string, string> environment) {
    ArgumentNullException.ThrowIfNull(environment);
    foreach (var name in environment.Keys)
      if (name.Length == 0 || name.Contains('=', StringComparison.Ordinal))
        throw new ArgumentException($"'{name}' is not an environment variable name", nameof(environment));

    return new(environment, StringComparer.Ordinal);
  }

  /// <summary>The command as a message names it: the toolchain's key and the arguments, separated by spaces.</summary>
  public override string ToString() => string.Join(' ', this.Arguments.Prepend(this.Toolchain));

}
