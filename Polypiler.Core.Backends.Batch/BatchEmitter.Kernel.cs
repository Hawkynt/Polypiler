using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Batch;

public static partial class BatchEmitter {

  private const int _maxInt32TextLength = 11;
  private static readonly IrType _i32Array = IrType.Array(IrType.I32);

  /// <summary>
  /// Emits the compact Int32 kernel form. Returns <see langword="null"/> when the body needs
  /// a construct provided by the full runtime.
  /// </summary>
  private static IReadOnlyList<string>? KernelLines(IrModule module) {
    if (Kernel(module) is not { } function)
      return null;

    try {
      var writer = new Writer(function);
      writer.Block(function.Body);
      writer.Finish();
      return writer.Lines;
    } catch (Refusal refusal) when (refusal is not KernelDiagnostic) {
      return null;
    }
  }

  private sealed partial class Writer {

    private readonly Dictionary<IrLocal, string> _locals = [];
    private int _nextLabel;
    private int _nextArrayId;
    private int _totalCollectionSlots;
    private string? _exhaustedStorage;
    private int _loopDepth;
    private bool _needsBoundsTrap;

    public Writer(IrFunction function) {
      this._mapKeyIds = CollectMapKeys(function);
      foreach (var local in function.Locals) {
        var name = "_ply_v" + this._locals.Count.ToString(CultureInfo.InvariantCulture);
        if (!this.SupportsValueType(local.Type) || !this._locals.TryAdd(local, name))
          throw new Refusal($"local {local.Name} of type {local.Type}");
      }
    }

    public List<string> Lines { get; } = ["@echo off", "setlocal EnableExtensions DisableDelayedExpansion"];

    public void Finish() {
      if (this._exhaustedStorage is { } operation)
        throw new KernelDiagnostic($"{operation} exceeding the {CmdArrayStorageBudget}-slot collection budget");

      if (this._needsBoundsTrap)
        this.Trap("_ply_trap_oor", "IndexOutOfRange");
      if (this._needsMissingKeyTrap)
        this.Trap(_missingKeyLabel, "KeyNotFound");
      if (this._needsDuplicateKeyTrap)
        this.Trap(_duplicateKeyLabel, "DuplicateKey");
    }

    private void Trap(string label, string name) {
      this.Mark(label);
      this.Line($">&2 echo PLY_BATCH_TRAP={name}");
      this.Line("exit /b 97");
    }

    private static string Temporary(int slot) => "_ply_t" + slot.ToString(CultureInfo.InvariantCulture);
    private string Label() => "_ply_l" + this._nextLabel++.ToString(CultureInfo.InvariantCulture);

    private void Line(string line) {
      if (line.Length > _cmdLineLimit)
        throw new Refusal($"a generated command longer than {_cmdLineLimit} bytes");
      this.Lines.Add(line);
    }

    private void Mark(string label) => this.Line($":{label}");
    private void Jump(string label) => this.Line($"goto {label}");
    private void Assign(string name, string value) => this.Line($"set /a \"{name}={value}\"");

    public void Block(IrBlock block, (string? Break, string? Continue) targets = default) {
      foreach (var statement in block.Statements)
        switch (statement) {
          case IrBlock nested:
            this.Block(nested, targets);
            break;
          case IrDeclare { Initializer: null }:
            break;
          case IrDeclare { Initializer: { } value } declaration:
            this.Assign(this._locals[declaration.Local], this.Value(value));
            break;
          case IrAssign assignment:
            this.Assign(this._locals[assignment.Local], this.Value(assignment.Value));
            break;
          case IrStore store:
            this.Store(store);
            break;
          case IrExprStmt { Expression: IrCollection { Op: IrCollectionOp.MapPut or IrCollectionOp.MapAdd } map }:
            this.WriteMap(map);
            break;
          case IrWriteLine write:
            this.WriteLine(write);
            break;
          case IrReturn { Value: { } result }:
            var valueName = this.Value(result);
            this.Line($"exit /b %{valueName}%");
            break;
          case IrIf conditional:
            this.If(conditional, targets);
            break;
          case IrSwitch switching:
            this.Switch(switching, targets);
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
          case IrBreak when targets.Break is { } breaking:
            this.Jump(breaking);
            break;
          case IrContinue when targets.Continue is { } continuing:
            this.Jump(continuing);
            break;
          default:
            throw new Refusal(statement.GetType().Name);
        }
    }

    private void If(IrIf conditional, (string? Break, string? Continue) targets) {
      var yes = this.Label();
      var end = this.Label();
      var no = conditional.Else is null ? end : this.Label();
      this.Branch(conditional.Condition, yes, no);
      this.Mark(yes);
      this.Block(conditional.Then, targets);
      if (conditional.Else is { } alternative) {
        this.Jump(end);
        this.Mark(no);
        this.Block(alternative, targets);
      }
      this.Mark(end);
    }

    private void Switch(IrSwitch switching, (string? Break, string? Continue) targets) {
      if (switching.Value.Type != IrType.I32)
        throw new Refusal($"switch selector of type {switching.Value.Type}");

      var selector = this.Value(switching.Value);
      var end = this.Label();
      var armLabels = new string[switching.Sections.Count];
      string? defaultLabel = null;
      var nonNegative = new List<(int Value, string Label)>();
      var negative = new List<(int Value, string Label)>();

      for (var index = 0; index < switching.Sections.Count; ++index) {
        var section = switching.Sections[index];
        var label = this.Label();
        armLabels[index] = label;
        if (section.IsDefault)
          defaultLabel = label;
        foreach (var raw in section.Labels) {
          var constant = (int)raw;
          if (constant < 0)
            negative.Add((constant & int.MaxValue, label));
          else
            nonNegative.Add((constant, label));
        }
      }

      var fallback = defaultLabel ?? end;
      this.Assign("_ply_sws", $"({selector}>>31)&1");
      this.Assign("_ply_swm", $"{selector}&2147483647");

      if (negative.Count > 0) {
        var negativeBranch = this.Label();
        this.Line($"if %_ply_sws% EQU 1 goto {negativeBranch}");
        foreach (var (value, label) in nonNegative)
          this.Line($"if %_ply_swm% EQU {value.ToString(CultureInfo.InvariantCulture)} goto {label}");
        this.Jump(fallback);
        this.Mark(negativeBranch);
        foreach (var (value, label) in negative)
          this.Line($"if %_ply_swm% EQU {value.ToString(CultureInfo.InvariantCulture)} goto {label}");
        this.Jump(fallback);
      } else {
        this.Line($"if %_ply_sws% NEQ 0 goto {fallback}");
        foreach (var (value, label) in nonNegative)
          this.Line($"if %_ply_swm% EQU {value.ToString(CultureInfo.InvariantCulture)} goto {label}");
        this.Jump(fallback);
      }

      var sectionTargets = (end, targets.Continue);
      for (var index = 0; index < switching.Sections.Count; ++index) {
        this.Mark(armLabels[index]);
        this.Block(switching.Sections[index].Body, sectionTargets);
        this.Jump(end);
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
      ++this._loopDepth;
      this.Block(body, (end, advance));
      if (step is not null) {
        this.Mark(advance);
        this.Block(step);
      }
      --this._loopDepth;
      this.Mark(test);
      this.Branch(condition, start, end);
      this.Mark(end);
    }

    private void Store(IrStore store) {
      if (store.Array.Type != _i32Array || store.Index.Type != IrType.I32 || store.Value.Type != IrType.I32)
        throw new Refusal($"array store on {store.Array.Type}");

      var array = this.Value(store.Array, 0);
      var index = this.Value(store.Index, 1);
      var value = this.Value(store.Value, 2);
      this.EmitBoundsCheck(array, index);
      this.Line($"set /a \"_ply_a%{array}%_%{index}%={value}\"");
    }

    private void EmitBoundsCheck(string array, string index) {
      this._needsBoundsTrap = true;
      this.Assign("_ply_bs", $"({index}>>31)&1");
      this.Line("if %_ply_bs% NEQ 0 goto _ply_trap_oor");
      this.Line($"set /a \"_ply_bl=_ply_al%{array}%\"");
      this.Line($"if %{index}% GEQ %_ply_bl% goto _ply_trap_oor");
    }

    private void WriteLine(IrWriteLine write) {
      var fragments = new List<OutputFragment>();
      CollectOutputFragments(write.Value, fragments);

      if (fragments.Count == 0) {
        this.Line("echo(");
        return;
      }

      var refs = new List<string>(fragments.Count);
      var intSlot = 0;
      var literalSlot = 0;
      var expandedLength = 5; // "echo("

      // Evaluate Int32 expressions left-to-right before printing so evaluation order and traps stay exact.
      var intNames = new Dictionary<int, string>();
      for (var index = 0; index < fragments.Count; ++index)
        if (fragments[index].Expression is { } expr) {
          var evaluated = this.Value(expr);
          var slot = "_ply_w" + intSlot++.ToString(CultureInfo.InvariantCulture);
          this.Assign(slot, evaluated);
          intNames.Add(index, slot);
          expandedLength += _maxInt32TextLength;
        }

      for (var index = 0; index < fragments.Count; ++index) {
        if (fragments[index].Text is { } text) {
          var slot = "_ply_o" + literalSlot++.ToString(CultureInfo.InvariantCulture);
          this.Line($"set {slot}={EscapeSetLiteral(text)}");
          refs.Add($"!{slot}!");
          expandedLength += text.Length;
        } else {
          refs.Add($"!{intNames[index]}!");
        }
      }

      if (expandedLength > _cmdLineLimit)
        throw new Refusal($"an expanded ECHO command longer than {_cmdLineLimit} bytes");

      this.Line("setlocal EnableDelayedExpansion");
      this.Line("echo(" + string.Concat(refs));
      this.Line("endlocal");
    }

    private static void CollectOutputFragments(IrExpr expression, List<OutputFragment> fragments) {
      switch (expression) {
        case IrNull when expression.Type == IrType.String:
          break;
        case IrStrConst constant:
          foreach (var character in constant.Value)
            if (character is < ' ' or > '~')
              throw new Refusal("non-printable or non-ASCII text in WriteLine");
          if (constant.Value.Length > 0)
            fragments.Add(new(constant.Value, null));
          break;
        case IrIntrinsic { Op: IrIntrinsicOp.FormatDotNet, Arguments: [var value] } when value.Type == IrType.I32:
          fragments.Add(new(null, value));
          break;
        case IrIntrinsic { Op: IrIntrinsicOp.StrConcat, Arguments: var parts }:
          foreach (var part in parts)
            CollectOutputFragments(part, fragments);
          break;
        default:
          throw new Refusal($"WriteLine operand {expression.GetType().Name}");
      }
    }

    private static string EscapeSetLiteral(string text) {
      var builder = new StringBuilder(text.Length * 2);
      foreach (var character in text) {
        if (character == '%') {
          builder.Append("%%");
        } else if (character is '^' or '&' or '|' or '<' or '>' or '(' or ')' or '"') {
          builder.Append('^').Append(character);
        } else {
          builder.Append(character);
        }
      }
      return builder.ToString();
    }

    private readonly record struct OutputFragment(string? Text, IrExpr? Expression);

    private void Branch(IrExpr condition, string yes, string no, int slot = 0) {
      if (condition is IrConst constant && constant.Type == IrType.Bool) {
        this.Jump(constant.Value == 0 ? no : yes);
        return;
      }
      if (condition is IrUnary { Op: IrUnaryOp.LogicalNot } negated) {
        this.Branch(negated.Operand, no, yes, slot);
        return;
      }
      if (condition is IrBinary { Op: IrBinaryOp.LogicalAnd } both) {
        var next = this.Label();
        this.Branch(both.Left, next, no, slot);
        this.Mark(next);
        this.Branch(both.Right, yes, no, slot);
        return;
      }
      if (condition is IrBinary { Op: IrBinaryOp.LogicalOr } either) {
        var next = this.Label();
        this.Branch(either.Left, yes, next, slot);
        this.Mark(next);
        this.Branch(either.Right, yes, no, slot);
        return;
      }
      if (condition is IrLocalRef local && local.Type == IrType.Bool) {
        this.Line($"if %{this.Value(local, slot)}% NEQ 0 goto {yes}");
        this.Jump(no);
        return;
      }
      if (condition is IrCollection { Op: IrCollectionOp.MapContainsKey } contains) {
        var (_, entry) = this.MapEntry(contains, slot);
        this.Line($"if defined {entry} goto {yes}");
        this.Jump(no);
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
      var left = this.Value(binary.Left, slot);
      var right = this.Value(binary.Right, slot + 1);
      var leftBits = Temporary(slot + 2);
      var rightBits = Temporary(slot + 3);
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

    private int ReserveArray(int length) {
      if (this._loopDepth > 0)
        throw new Refusal("array allocation inside a loop");
      this.ReserveCollectionSlots(length, "array storage");
      return ++this._nextArrayId;
    }

    private void ReserveCollectionSlots(int size, string operation) {
      if (size <= CmdArrayStorageBudget - this._totalCollectionSlots) {
        this._totalCollectionSlots += size;
      } else if (this._mapKeyIds is not null) {
        this._exhaustedStorage ??= operation;
      } else {
        throw new Refusal($"{operation} exceeding the {CmdArrayStorageBudget}-slot collection budget");
      }
    }

    private bool SupportsValueType(IrType type)
      => type == IrType.I32 || type == _i32Array
        || this._mapKeyIds is not null && (type == _stringMap || type == IrType.String || type == IrType.Bool);

    // The result owns its slot. Evaluating an operand can reuse higher slots until that operand returns.
    private string Value(IrExpr expression, int slot = 0) {
      if (!this.SupportsValueType(expression.Type))
        throw new Refusal(expression.Type.ToString());
      if (expression is IrLocalRef local)
        return this._locals[local.Local];

      var destination = Temporary(slot);
      switch (expression) {
        case IrConst constant when constant.Type == IrType.I32 || constant.Type == IrType.Bool:
          this.Assign(destination, constant.Value == int.MinValue ? "-2147483647-1" : constant.Value.ToString(CultureInfo.InvariantCulture));
          break;
        case IrStrConst key when this._mapKeyIds is not null && this._mapKeyIds.TryGetValue(key.Value, out var keyId):
          this.Assign(destination, keyId.ToString(CultureInfo.InvariantCulture));
          break;
        case IrConditional conditional when this._mapKeyIds is not null:
          this.AssignChoice(destination, conditional.Condition, conditional.WhenTrue, conditional.WhenFalse, slot + 1);
          break;
        case IrCollection collection when collection.Op != IrCollectionOp.MapContainsKey:
          this.ReadMap(destination, collection, slot + 1);
          break;
        case { Type: var type } when type == IrType.Bool:
          this.AssignChoice(destination, expression, new IrConst(IrType.Bool, 1), new IrConst(IrType.Bool, 0), slot + 1);
          break;
        case IrUnary { Op: IrUnaryOp.Negate } unary when unary.Type == IrType.I32:
          this.Assign(destination, $"-{this.Value(unary.Operand, slot + 1)}");
          break;
        case IrUnary { Op: IrUnaryOp.BitNot } unary when unary.Type == IrType.I32:
          this.Assign(destination, $"~{this.Value(unary.Operand, slot + 1)}");
          break;
        case IrBinary binary when binary.Type == IrType.I32:
          this.Binary(binary, destination, slot);
          break;
        case IrIntrinsic { Op: IrIntrinsicOp.ArrayOf, Arguments: var elements } when expression.Type == _i32Array:
          var arrayId = this.ReserveArray(elements.Count);
          var idText = arrayId.ToString(CultureInfo.InvariantCulture);
          for (var index = 0; index < elements.Count; ++index) {
            var item = this.Value(elements[index], slot + 1);
            this.Assign($"_ply_a{idText}_{index.ToString(CultureInfo.InvariantCulture)}", item);
          }
          this.Assign($"_ply_al{idText}", elements.Count.ToString(CultureInfo.InvariantCulture));
          this.Assign(destination, idText);
          break;
        case IrNewArray { Length: IrConst { Value: >= 0 and <= int.MaxValue } count } when expression.Type == _i32Array:
          var length = (int)count.Value;
          var newId = this.ReserveArray(length);
          var newIdText = newId.ToString(CultureInfo.InvariantCulture);
          if (length > 0)
            this.Line($"for /l %%I in (0,1,{(length - 1).ToString(CultureInfo.InvariantCulture)}) do set /a \"_ply_a{newIdText}_%%I=0\"");
          this.Assign($"_ply_al{newIdText}", length.ToString(CultureInfo.InvariantCulture));
          this.Assign(destination, newIdText);
          break;
        case IrArrayLength lengthExpr when lengthExpr.Array.Type == _i32Array:
          var targetArray = this.Value(lengthExpr.Array, slot + 1);
          this.Line($"set /a \"{destination}=_ply_al%{targetArray}%\"");
          break;
        case IrArrayLoad load when load.Array.Type == _i32Array && load.Index.Type == IrType.I32:
          var loadArray = this.Value(load.Array, slot + 1);
          var loadIndex = this.Value(load.Index, slot + 2);
          this.EmitBoundsCheck(loadArray, loadIndex);
          this.Line($"set /a \"{destination}=_ply_a%{loadArray}%_%{loadIndex}%\"");
          break;
        default:
          throw new Refusal(expression.GetType().Name);
      }
      return destination;
    }

    private void AssignChoice(string destination, IrExpr condition, IrExpr whenTrue, IrExpr whenFalse, int slot) {
      var yes = this.Label();
      var no = this.Label();
      var done = this.Label();
      this.Branch(condition, yes, no, slot);
      this.Mark(yes);
      this.Assign(destination, this.Value(whenTrue, slot));
      this.Jump(done);
      this.Mark(no);
      this.Assign(destination, this.Value(whenFalse, slot));
      this.Mark(done);
    }

    private void Binary(IrBinary binary, string destination, int slot) {
      var left = this.Value(binary.Left, slot + 1);
      var right = this.Value(binary.Right, slot + 2);
      switch (binary.Op) {
        case IrBinaryOp.Add:
          this.Assign(destination, $"{left}+{right}");
          break;
        case IrBinaryOp.Subtract:
          this.Assign(destination, $"{left}-{right}");
          break;
        case IrBinaryOp.Multiply:
          this.Assign(destination, $"{left}*{right}");
          break;
        case IrBinaryOp.And:
          this.Assign(destination, $"{left}&{right}");
          break;
        case IrBinaryOp.Or:
          this.Assign(destination, $"{left}|{right}");
          break;
        case IrBinaryOp.Xor:
          this.Assign(destination, $"{left}^{right}");
          break;
        case IrBinaryOp.ShiftLeft:
          var leftCount = Temporary(slot + 3);
          this.Assign(leftCount, $"{right}&31");
          this.Assign(destination, $"{left}<<{leftCount}");
          break;
        case IrBinaryOp.ShiftRight:
          var rightCount = Temporary(slot + 3);
          var sign = Temporary(slot + 4);
          this.Assign(rightCount, $"{right}&31");
          this.Assign(sign, $"-((({left}>>31)&1))");
          this.Assign(destination, $"(({left}^{sign})>>{rightCount})^{sign}");
          break;
        case IrBinaryOp.ShiftRightLogical:
          var logicalCount = Temporary(slot + 3);
          var done = this.Label();
          this.Assign(logicalCount, $"{right}&31");
          this.Assign(destination, left);
          this.Line($"if %{logicalCount}% EQU 0 goto {done}");
          this.Assign(destination, $"({left}>>{logicalCount})&(2147483647>>({logicalCount}-1))");
          this.Mark(done);
          break;
        default:
          throw new Refusal(binary.Op.ToString());
      }
    }

  }

}
