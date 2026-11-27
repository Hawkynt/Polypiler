using System;
using System.Collections.Generic;
using System.IO;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

/// <summary>A lifted Batch module or its diagnostics.</summary>
public sealed record BatchImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>A Batch source dialect.</summary>
public enum BatchDialect {
  /// <summary>Windows cmd.exe with command extensions.</summary>
  Cmd,
  /// <summary>English MS-DOS 6.22 COMMAND.COM.</summary>
  Dos622
}

/// <summary>Optional inputs for multi-file and argument-bearing Batch imports.</summary>
public sealed record BatchImportOptions {

  /// <summary>Declared companion batch sources keyed by filename or relative path (matched case-insensitively).</summary>
  public IReadOnlyDictionary<string, string>? CompanionFiles { get; init; }

  /// <summary>Initial positional arguments (<c>%1</c>…) supplied to the entry batch file.</summary>
  public IReadOnlyList<string>? EntryArguments { get; init; }

  /// <summary>The invocation spelling bound to <c>%0</c> for the entry batch file.</summary>
  public string? EntryInvocation { get; init; }

  /// <summary>Declared status-only test fixtures (such as <c>PLYRC.COM</c>) mapped to their fixed exit status.</summary>
  public IReadOnlyDictionary<string, int>? StatusFixtures { get; init; }

}

/// <summary>Imports closed Batch scripts without running the source.</summary>
public static partial class BatchFrontend {

  /// <summary>The script has malformed or unresolved syntax.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Invalid = new("PLY2800", DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "invalid Batch script: {0}", "batch.frontend.syntax");

  /// <summary>The script exceeds the supported source semantics.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsupported = new("PLY2802", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' is not supported by the Batch frontend", "batch.frontend.profile");

  /// <summary>Parses and lowers one closed Int32 script.</summary>
  public static BatchImportResult Import(string file, string source) => Import(file, source, BatchDialect.Cmd, null);

  /// <summary>Parses and lowers a closed script for the selected source dialect.</summary>
  public static BatchImportResult Import(string file, string source, BatchDialect dialect) => Import(file, source, dialect, null);

  /// <summary>Parses and lowers a closed script for the selected source dialect with optional multi-file inputs.</summary>
  public static BatchImportResult Import(string file, string source, BatchDialect dialect, BatchImportOptions? options) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(source);
    try {
      var script = dialect switch {
        BatchDialect.Cmd => new Parser(source).Read(),
        BatchDialect.Dos622 => new DosParser(file, source, options).Read(),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect))
      };
      var module = script.Lift(Path.GetFileNameWithoutExtension(file));
      var diagnostics = IrVerifier.Verify(module);
      return diagnostics.Count == 0 ? new(module, []) : new(null, diagnostics);
    } catch (Refusal error) {
      return new(null, [Diagnostic.Create(error.Malformed ? Invalid : Unsupported,
        new(file, error.Line, 1, error.Line, 1), error.Message)]);
    }
  }

  private sealed class Refusal(int line, string detail, bool malformed = false) : Exception(detail) {
    public int Line { get; } = Math.Max(1, line);
    public bool Malformed { get; } = malformed;
  }

  private abstract record Instruction(int Line);
  private sealed record Assignment(int Line, IrLocal Destination, IrExpr Value, bool ReadsDelayedExpansion = false,
    IReadOnlyList<IrLocal>? DecimalTextOperands = null) : Instruction(Line);
  private sealed record WriteLine(int Line, IrExpr Text) : Instruction(Line);
  private sealed record Jump(int Line, string Target, IrExpr? Condition) : Instruction(Line);
  private sealed record Exit(int Line, IrExpr Status) : Instruction(Line);
  private sealed record ScopePush(int Line, int FrameId, bool? DelayedExpansion = null) : Instruction(Line);
  private sealed record ScopePop(int Line, int FrameId) : Instruction(Line);
  private sealed record FrameUnwind(int Line, int FrameId) : Instruction(Line);

}
