using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Batch;

/// <summary>Emits closed Int32 kernels for MS-DOS 6.22 COMMAND.COM.</summary>
public static class DosBatchEmitter {

  /// <summary>The MS-DOS 6.22 COMMAND.COM target profile.</summary>
  public const string Profile = "dos622";

  /// <summary>A construct or generated resource requirement outside the DOS profile.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsupported = new("PLY2851", DiagnosticSeverity.Error,
    ExitCode.UnsupportedConstruct, "'{0}' cannot be emitted for the dos622 Batch profile", "batch.backend.dos622");

  /// <summary>Checks that a verified module can be emitted under the DOS profile.</summary>
  public static IReadOnlyList<Diagnostic> Check(IrModule module) => Build(module).Diagnostics;

  /// <summary>Emits one ASCII CRLF batch file for a module accepted by <see cref="Check"/>.</summary>
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
      var function = BatchEmitter.Kernel(module) ?? throw new Refusal("module other than a single public parameterless Int32 kernel");
      var writer = new Writer(function.Locals);
      writer.Block(function.Body);
      writer.Finish();
      return (writer.Lines, []);
    } catch (Refusal error) {
      return ([], [Diagnostic.Create(Unsupported, null, error.Message)]);
    }
  }

  private sealed class Refusal(string detail) : Exception(detail);

  private sealed class Writer {

    private const int _environmentCapacity = 32768;
    private const int _inheritedEnvironmentBytes = 1024;
    private const int _digitCount = 8;
    private const string _hex = "0123456789ABCDEF";
    private const string _carry = "PLYC";
    private const string _digitResult = "PLYD";
    private const string _continuation = "PLYN";
    private const string _output = "PLYOUT";
    private const string _print = "PLYPRINT";
    private const string _end = "PLYEND";

    private readonly Dictionary<IrLocal, string> _locals = [];
    private readonly HashSet<int> _temporaries = [];
    private int _environmentBytes = _inheritedEnvironmentBytes + 1;
    private int _nextLabel;
    private bool _needsAdder;
    private bool _hasReturn;

    public Writer(IReadOnlyList<IrLocal> locals) {
      foreach (var local in locals) {
        var name = Slot('V', this._locals.Count);
        if (local.Type != IrType.I32 || !this._locals.TryAdd(local, name))
          throw new Refusal($"local {local.Name} of type {local.Type}");
        this.Reserve(name.Length + 1, 1, _digitCount);
      }
    }

    public List<string> Lines { get; } = ["@ECHO OFF"];

    // COMMAND.COM can truncate SET silently when the block is full. Reserve the final NUL
    // and count each possible generated entry as NAME=VALUE plus the entry terminator.
    private void Reserve(int nameLength, int valueLength, int entries = 1) {
      this._environmentBytes += entries * (nameLength + valueLength + 2);
      if (this._environmentBytes > _environmentCapacity)
        throw new Refusal($"generated environment exceeds {_environmentCapacity} bytes including the {_inheritedEnvironmentBytes}-byte inherited allowance");
    }

    private static string Slot(char kind, int number) => $"{kind}{number.ToString("X3", CultureInfo.InvariantCulture)}";
    private static string Digit(string slot, int index) => slot + _hex[index];
    private static string Read(string name) => $"%{name}%";
    private static string AdderLabel(char left, char right, char carry) => $"A{left}{right}{carry}";
    private string Label() {
      if (this._nextLabel == 0x10000000)
        throw new Refusal("generated label count exceeds the DOS label namespace");
      return "L" + this._nextLabel++.ToString("X7", CultureInfo.InvariantCulture);
    }

    // With bounded slot and label names, the longest command is the 67-byte SET of PLYOUT.
    // Expansions in these commands replace variable names with one hex digit or a short label.
    private void Set(string name, string value) => this.Lines.Add($"SET {name}={value}");
    private void Mark(string label) => this.Lines.Add($":{label}");
    private void Jump(string label) => this.Lines.Add($"GOTO {label}");

    public void Block(IrBlock block, (string Break, string Continue)? loop = null) {
      foreach (var statement in block.Statements)
        switch (statement) {
          case IrBlock nested:
            this.Block(nested, loop);
            break;
          case IrDeclare { Initializer: { } value } declaration:
            this.Assign(this._locals[declaration.Local], value, 0);
            break;
          case IrAssign assignment:
            this.Assign(this._locals[assignment.Local], assignment.Value, 0);
            break;
          case IrReturn { Value: { } result }:
            this.Return(this.Value(result, 0));
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

    private void Return(string value) {
      if (!this._hasReturn) {
        this.Reserve(_output.Length, _digitCount);
        this._hasReturn = true;
      }
      var packed = new StringBuilder();
      for (var index = 0; index < _digitCount; ++index)
        packed.Append(Read(Digit(value, index)));
      this.Set(_output, packed.ToString());
      this.Jump(_print);
    }

    public void Finish() {
      if (!this._hasReturn)
        throw new Refusal("kernel with no Int32 return");
      this.Jump(_end);
      if (this._needsAdder)
        this.AdderTable();
      this.Mark(_print);
      this.Lines.Add($"ECHO PLYRESULT={Read(_output)}");
      this.Mark(_end);
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
      if (condition is IrConst { Type: var type } constant && type == IrType.Bool) {
        this.Jump(constant.Value == 0 ? no : yes);
        return;
      }
      if (condition is not IrBinary binary)
        throw new Refusal(condition.GetType().Name + " as a condition");
      var (same, different) = binary.Op switch {
        IrBinaryOp.Equal => (yes, no),
        IrBinaryOp.NotEqual => (no, yes),
        _ => throw new Refusal(binary.Op.ToString())
      };
      var left = this.Value(binary.Left, 0);
      var right = this.Value(binary.Right, 1);
      for (var index = 0; index < _digitCount; ++index)
        this.Lines.Add($"IF NOT {Read(Digit(left, index))}=={Read(Digit(right, index))} GOTO {different}");
      this.Jump(same);
    }

    private string Value(IrExpr expression, int slot) {
      if (expression.Type != IrType.I32)
        throw new Refusal(expression.Type.ToString());
      if (expression is IrLocalRef local)
        return this._locals[local.Local];

      var destination = Slot('T', slot);
      if (this._temporaries.Add(slot))
        this.Reserve(destination.Length + 1, 1, _digitCount);
      this.Assign(destination, expression, slot + 1);
      return destination;
    }

    private void Assign(string destination, IrExpr expression, int firstTemporary) {
      switch (expression) {
        case IrConst { Type: var type } constant when type == IrType.I32:
          var bits = unchecked((uint)constant.Value).ToString("X8", CultureInfo.InvariantCulture);
          for (var index = 0; index < _digitCount; ++index)
            this.Set(Digit(destination, index), bits[index].ToString());
          break;
        case IrLocalRef reference:
          var source = this._locals[reference.Local];
          if (destination == source)
            break;
          for (var index = 0; index < _digitCount; ++index)
            this.Set(Digit(destination, index), Read(Digit(source, index)));
          break;
        case IrBinary { Op: IrBinaryOp.Add } binary:
          var left = this.Value(binary.Left, firstTemporary);
          var right = this.Value(binary.Right, firstTemporary + 1);
          this.Add(destination, left, right);
          break;
        case IrBinary binary:
          throw new Refusal(binary.Op.ToString());
        default:
          throw new Refusal(expression.GetType().Name);
      }
    }

    private void Add(string destination, string left, string right) {
      if (!this._needsAdder) {
        this.Reserve(_carry.Length, 1);
        this.Reserve(_digitResult.Length, 1);
        this.Reserve(_continuation.Length, 8);
        this._needsAdder = true;
      }
      this.Set(_carry, "0");
      // Read low digits first. The destination can share either operand's slot because
      // subsequent dispatches read only higher digits. A carry past the last digit wraps.
      for (var index = _digitCount - 1; index >= 0; --index) {
        var resume = this.Label();
        this.Set(_continuation, resume);
        this.Jump($"A{Read(Digit(left, index))}{Read(Digit(right, index))}{Read(_carry)}");
        this.Mark(resume);
        this.Set(Digit(destination, index), Read(_digitResult));
      }
    }

    private void AdderTable() {
      for (var left = 0; left < _hex.Length; ++left)
        for (var right = 0; right < _hex.Length; ++right)
          for (var carry = 0; carry <= 1; ++carry) {
            var sum = left + right + carry;
            this.Mark(AdderLabel(_hex[left], _hex[right], _hex[carry]));
            this.Set(_digitResult, _hex[sum & 0xF].ToString());
            this.Set(_carry, sum < 16 ? "0" : "1");
            this.Jump(Read(_continuation));
          }
    }

  }

}
