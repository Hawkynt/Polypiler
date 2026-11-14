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

  /// <summary>The toolchain's key in the lock.</summary>
  /// <exception cref="ArgumentException">The key is empty.</exception>
  public string Toolchain { get; } = string.IsNullOrEmpty(Toolchain) ? throw new ArgumentException("a process needs a toolchain", nameof(Toolchain)) : Toolchain;

  /// <summary>The arguments, each passed as it is.</summary>
  public IReadOnlyList<string> Arguments { get; } = Arguments?.ToArray() ?? throw new ArgumentNullException(nameof(Arguments));

  /// <summary>The command as a message names it: the toolchain's key and the arguments, separated by spaces.</summary>
  public override string ToString() => string.Join(' ', this.Arguments.Prepend(this.Toolchain));

}
