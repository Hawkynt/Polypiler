using System;
using System.Collections.Generic;
using System.Globalization;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Batch;

/// <summary>Emits standalone Int32 kernels for native Windows cmd.exe.</summary>
public static class BatchEmitter {

  /// <summary>The Windows cmd.exe profile with command extensions enabled.</summary>
  public const string Profile = "cmd";

  /// <summary>A construct outside the signed Int32 Batch profile.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsupported = new("PLY2850", DiagnosticSeverity.Error,
    ExitCode.UnsupportedConstruct, "'{0}' cannot be emitted for the cmd Batch profile", "batch.backend.profile");

  /// <summary>Checks that a verified module can be emitted as Batch.</summary>
  public static IReadOnlyList<Diagnostic> Check(IrModule module) => Build(module).Diagnostics;

  /// <summary>Emits a single standalone batch file for a module accepted by <see cref="Check"/>.</summary>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) {
    var (lines, diagnostics) = Build(module);
    if (diagnostics.Count > 0)
      throw new ArgumentException(diagnostics[0].Message, nameof(module));
    return [new("kernel.bat", string.Join("\r\n", lines) + "\r\n")];
  }

  private static (IReadOnlyList<string> Lines, IReadOnlyList<Diagnostic> Diagnostics) Build(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var verification = IrVerifier.Verify(module);
    if (verification.Count > 0)
      return ([], verification);
    try {
      var function = Kernel(module) ?? throw new Refusal("module other than a single public parameterless Int32 kernel");
      var writer = new Writer(function.Locals);
      writer.Block(function.Body);
      return (writer.Lines, []);
    } catch (Refusal error) {
      return ([], [Diagnostic.Create(Unsupported, null, error.Message)]);
    }
  }

  internal static IrFunction? Kernel(IrModule module)
    => module.Functions is [{ IsPublic: true, ReturnType: var type, Parameters.Count: 0 } function]
       && type == IrType.I32 && module.Constants.Count == 0 && module.Globals.Count == 0
       && module.Types.Count == 0 && module.Initializer is null ? function : null;

  private sealed class Refusal(string detail) : Exception(detail);

  private sealed class Writer {

    private readonly Dictionary<IrLocal, string> _locals = [];
    private int _nextLabel;

    public Writer(IReadOnlyList<IrLocal> locals) {
      foreach (var local in locals) {
        var name = "_ply_v" + this._locals.Count.ToString(CultureInfo.InvariantCulture);
        if (local.Type != IrType.I32 || !this._locals.TryAdd(local, name))
          throw new Refusal($"local {local.Name} of type {local.Type}");
      }
    }

    public List<string> Lines { get; } = ["@echo off", "setlocal EnableExtensions DisableDelayedExpansion"];

    private static string Temporary(int slot) => "_ply_t" + slot.ToString(CultureInfo.InvariantCulture);
    private string Label() => "_ply_l" + this._nextLabel++.ToString(CultureInfo.InvariantCulture);

    private void Line(string line) {
      if (line.Length > 1024)
        throw new Refusal("a generated command longer than 1024 bytes");
      this.Lines.Add(line);
    }

    private void Mark(string label) => this.Line($":{label}");
    private void Jump(string label) => this.Line($"goto {label}");
    private void Assign(string name, string value) => this.Line($"set /a \"{name}={value}\"");

    public void Block(IrBlock block, (string Break, string Continue)? loop = null) {
      foreach (var statement in block.Statements)
        switch (statement) {
          case IrBlock nested:
            this.Block(nested, loop);
            break;
          case IrDeclare { Initializer: { } value } declaration:
            this.Assign(this._locals[declaration.Local], this.Value(value));
            break;
          case IrAssign assignment:
            this.Assign(this._locals[assignment.Local], this.Value(assignment.Value));
            break;
          case IrReturn { Value: { } result }:
            var valueName = this.Value(result);
            this.Line($"exit /b %{valueName}%");
            break;
          case IrIf conditional:
            this.If(conditional, loop);
            break;
          case IrWhile whileLoop:
            this.Loop(whileLoop.Body, whileLoop.Condition, null, false);
            break;
          case IrDoWhile doLoop:
            this.Loop(doLoop.Body, doLoop.Condition, null, true);
            break;
          case IrFor { PerIteration.Count: 0 } forLoop:
            this.Loop(forLoop.Body, forLoop.Condition, forLoop.Step, false);
            break;
          case IrBreak when loop is { } breaking:
            this.Jump(breaking.Break);
            break;
          case IrContinue when loop is { } continuing:
            this.Jump(continuing.Continue);
            break;
          default:
            throw new Refusal(statement.GetType().Name);
        }
    }

    private void If(IrIf conditional, (string Break, string Continue)? loop) {
      var yes = this.Label();
      var end = this.Label();
      var no = conditional.Else is null ? end : this.Label();
      this.Branch(conditional.Condition, yes, no);
      this.Mark(yes);
      this.Block(conditional.Then, loop);
      if (conditional.Else is { } alternative) {
        this.Jump(end);
        this.Mark(no);
        this.Block(alternative, loop);
      }
      this.Mark(end);
    }

    private void Loop(IrBlock body, IrExpr condition, IrBlock? step, bool postTest) {
      var start = this.Label();
      var test = this.Label();
      var end = this.Label();
      var advance = step is null ? test : this.Label();
      if (!postTest)
        this.Jump(test);
      this.Mark(start);
      this.Block(body, (end, advance));
      if (step is not null) {
        this.Mark(advance);
        this.Block(step);
      }
      this.Mark(test);
      this.Branch(condition, start, end);
      this.Mark(end);
    }

    private void Branch(IrExpr condition, string yes, string no) {
      if (condition is IrConst constant && constant.Type == IrType.Bool) {
        this.Jump(constant.Value == 0 ? no : yes);
        return;
      }
      if (condition is not IrBinary binary)
        throw new Refusal(condition.GetType().Name + " as a condition");
      var compare = binary.Op switch {
        IrBinaryOp.Equal => "EQU",
        IrBinaryOp.NotEqual => "NEQ",
        IrBinaryOp.Less => "LSS",
        IrBinaryOp.LessOrEqual => "LEQ",
        IrBinaryOp.Greater => "GTR",
        IrBinaryOp.GreaterOrEqual => "GEQ",
        _ => throw new Refusal(binary.Op.ToString())
      };
      var left = this.Value(binary.Left);
      var right = this.Value(binary.Right, 1);
      var leftBits = Temporary(2);
      var rightBits = Temporary(3);
      this.Assign(leftBits, $"({left}>>31)&1");
      this.Assign(rightBits, $"({right}>>31)&1");
      var sameSign = this.Label();
      this.Line($"if %{leftBits}% EQU %{rightBits}% goto {sameSign}");
      switch (binary.Op) {
        case IrBinaryOp.Equal:
          this.Jump(no);
          break;
        case IrBinaryOp.NotEqual:
          this.Jump(yes);
          break;
        default:
          var signs = binary.Op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual ? "GTR" : "LSS";
          this.Line($"if %{leftBits}% {signs} %{rightBits}% goto {yes}");
          this.Jump(no);
          break;
      }

      this.Mark(sameSign);
      this.Assign(leftBits, $"{left}&2147483647");
      this.Assign(rightBits, $"{right}&2147483647");
      this.Line($"if %{leftBits}% {compare} %{rightBits}% goto {yes}");
      this.Jump(no);
    }

    // The result owns its slot. Evaluating an operand can reuse higher slots until that operand returns.
    private string Value(IrExpr expression, int slot = 0) {
      if (expression.Type != IrType.I32)
        throw new Refusal(expression.Type.ToString());
      if (expression is IrLocalRef local)
        return this._locals[local.Local];

      var destination = Temporary(slot);
      switch (expression) {
        case IrConst constant:
          this.Assign(destination, constant.Value == int.MinValue ? "-2147483647-1" : constant.Value.ToString(CultureInfo.InvariantCulture));
          break;
        case IrUnary { Op: IrUnaryOp.Negate } unary:
          this.Assign(destination, $"-{this.Value(unary.Operand, slot + 1)}");
          break;
        case IrBinary binary:
          var op = binary.Op switch {
            IrBinaryOp.Add => "+",
            IrBinaryOp.Subtract => "-",
            IrBinaryOp.Multiply => "*",
            _ => throw new Refusal(binary.Op.ToString())
          };
          var left = this.Value(binary.Left, slot + 1);
          var right = this.Value(binary.Right, slot + 2);
          this.Assign(destination, $"{left}{op}{right}");
          break;
        default:
          throw new Refusal(expression.GetType().Name);
      }
      return destination;
    }

  }

}
