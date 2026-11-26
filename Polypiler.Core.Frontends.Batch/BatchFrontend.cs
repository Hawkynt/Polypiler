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
  public static BatchImportResult Import(string file, string source) => Import(file, source, BatchDialect.Cmd);

  /// <summary>Parses and lowers a closed script for the selected source dialect.</summary>
  public static BatchImportResult Import(string file, string source, BatchDialect dialect) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(source);
    try {
      var script = dialect switch {
        BatchDialect.Cmd => new Parser(source).Read(),
        BatchDialect.Dos622 => new DosParser(source).Read(),
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
  private sealed record Assignment(int Line, IrLocal Destination, IrExpr Value) : Instruction(Line);
  private sealed record NumericLoop(int Line, IrLocal Iterator, IrConst Start, IrConst Step, IrConst End,
    bool HasIterations, Assignment Body) : Instruction(Line);
  private sealed record Jump(int Line, string Target, IrExpr? Condition) : Instruction(Line);
  private sealed record Exit(int Line, IrExpr Status) : Instruction(Line);

}
