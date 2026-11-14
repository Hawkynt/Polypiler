using System;
using System.Collections.Generic;

namespace Polypiler.Tooling.Toolchains;

/// <summary>One error about the toolchain lock or a toolchain it pins.</summary>
/// <param name="File">The lock file as it was named.</param>
/// <param name="Line">The line, counted from 1; 0 when the error has no position in the file.</param>
/// <param name="Column">The column, counted from 1; 0 when the error has no position in the file.</param>
/// <param name="Code">The diagnostic code, e.g. <see cref="ToolchainDiagnostic.LockInvalid"/>.</param>
/// <param name="Message">The reason.</param>
public sealed record ToolchainDiagnostic(string File, int Line, int Column, string Code, string Message) {

  /// <summary>The lock is invalid, or names no toolchain under the key asked for.</summary>
  public const string LockInvalid = "PLY0020";

  /// <summary>A toolchain the lock pins is not installed.</summary>
  public const string NotInstalled = "PLY0021";

  /// <summary>A toolchain is installed at another version than the lock pins.</summary>
  public const string VersionMismatch = "PLY0022";

  /// <summary>The lock file cannot be read.</summary>
  public const string Unreadable = "PLY0004";

  /// <summary>The diagnostic as a compiler prints it: <c>file(line,col): error CODE: message</c>.</summary>
  public override string ToString()
    => this.Line > 0 ? $"{this.File}({this.Line},{this.Column}): error {this.Code}: {this.Message}" : $"{this.File}: error {this.Code}: {this.Message}";

}

/// <summary>Thrown when a lock cannot be loaded or names no toolchain under a key.</summary>
public sealed class ToolchainLockException : Exception {

  /// <summary>Creates the exception for one or more diagnostics.</summary>
  public ToolchainLockException(IReadOnlyList<ToolchainDiagnostic> diagnostics)
    : base(string.Join(Environment.NewLine, diagnostics)) => this.Diagnostics = diagnostics;

  /// <summary>Everything that is wrong, in the order it was found.</summary>
  public IReadOnlyList<ToolchainDiagnostic> Diagnostics { get; }

}
