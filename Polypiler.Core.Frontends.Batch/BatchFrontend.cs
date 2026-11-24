using System;
using System.Collections.Generic;
using System.IO;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

/// <summary>A lifted Batch module or its diagnostics.</summary>
public sealed record BatchImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Imports closed integer kernels from the Windows cmd profile without running the script.</summary>
public static partial class BatchFrontend {

  /// <summary>The script has malformed or unresolved syntax.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Invalid = new("PLY2800", DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "invalid Batch script: {0}", "batch.frontend.syntax");

  /// <summary>The script exceeds the supported source semantics.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsupported = new("PLY2802", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' is not supported by the cmd Batch frontend", "batch.frontend.profile");

  /// <summary>Parses and lowers one closed Int32 script.</summary>
  public static BatchImportResult Import(string file, string source) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(source);
    try {
      var script = new Parser(source).Read();
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
  private sealed record Jump(int Line, string Target, IrBinary? Condition) : Instruction(Line);
  private sealed record Exit(int Line, IrExpr Status) : Instruction(Line);

}
