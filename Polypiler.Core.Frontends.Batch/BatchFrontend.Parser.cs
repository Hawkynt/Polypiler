using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private const int _commandLimit = 8191;
  private const int _maxInt32TextLength = 11;
  private const int _maxExpandedInstructions = 100_000;
  private const string _name = "[A-Za-z_][A-Za-z_0-9]*";
  private const string _decimal = "(?:0|-?[1-9][0-9]*)";
  private const string _positional = "%[1-9]";
  private const string _operand = "(?:%" + _name + "%|!" + _name + "!|" + _positional + "|" + _decimal + ")";
  private const RegexOptions _options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

  [GeneratedRegex("^:(" + _name + ")$", _options)]
  private static partial Regex Label();
  [GeneratedRegex("^goto[ \t]+(:eof|:?" + _name + ")$", _options)]
  private static partial Regex Goto();
  [GeneratedRegex("^if[ \t]+(" + _operand + ")[ \t]+(EQU|NEQ|LSS|LEQ|GTR|GEQ)[ \t]+(" + _operand + ")[ \t]+goto[ \t]+(:eof|:?" + _name + ")$", _options)]
  private static partial Regex IfGoto();
  [GeneratedRegex("^if[ \t]+(not[ \t]+)?\"(" + _operand + ")\"==\"(" + _operand + ")\"[ \t]+goto[ \t]+(:eof|:?" + _name + ")$", _options)]
  private static partial Regex IfEqGoto();
  [GeneratedRegex("^if[ \t]+\"%~1\"==\"\"[ \t]+goto[ \t]+(:eof|:?" + _name + ")$", _options)]
  private static partial Regex IfEmptyArgGoto();
  [GeneratedRegex("^if[ \t]+(" + _operand + ")[ \t]+(EQU|NEQ|LSS|LEQ|GTR|GEQ)[ \t]+(" + _operand + ")[ \t]+\\(set[ \t]+\"(" + _name + ")=(" + _operand + ")\"\\)[ \t]+else[ \t]+\\(set[ \t]+\"(" + _name + ")=(" + _operand + ")\"\\)$", _options)]
  private static partial Regex IfCmpSetElse();
  [GeneratedRegex("^if[ \t]+(not[ \t]+)?\"(" + _operand + ")\"==\"(" + _operand + ")\"[ \t]+\\(set[ \t]+\"(" + _name + ")=(" + _operand + ")\"\\)[ \t]+else[ \t]+\\(set[ \t]+\"(" + _name + ")=(" + _operand + ")\"\\)$", _options)]
  private static partial Regex IfEqSetElse();
  [GeneratedRegex("^if[ \t]+\"(" + _operand + ")\"==\"(" + _operand + ")\"[ \t]+\\(>&2[ \t]+echo[ \t]+[^&]*&[ \t]+exit[ \t]+/b[ \t]+(" + _operand + ")\\)$", _options)]
  private static partial Regex IfTrapExit();
  [GeneratedRegex("^exit[ \t]+/b(?:[ \t]+(" + _operand + "))?$", _options)]
  private static partial Regex ExitStatus();
  [GeneratedRegex("^set[ \t]+/a[ \t]+\"([^\"]*)\"$", _options)]
  private static partial Regex SetArithmetic();
  [GeneratedRegex("^(" + _name + ")=(.+)$", _options)]
  private static partial Regex SetArithAssignment();
  [GeneratedRegex("^set[ \t]+(?:\"(" + _name + ")=(" + _operand + ")\"|(" + _name + ")=(" + _operand + "))$", _options)]
  private static partial Regex SetValue();
  [GeneratedRegex("^setlocal(?:[ \t]+EnableExtensions[ \t]+(?:Disable|Enable)DelayedExpansion)?$", _options)]
  private static partial Regex SetLocalCmd();
  [GeneratedRegex("^setlocal[ \t]+EnableExtensions[ \t]+(Disable|Enable)DelayedExpansion$", _options)]
  private static partial Regex Preamble();
  [GeneratedRegex("^endlocal$", _options)]
  private static partial Regex EndLocalCmd();
  [GeneratedRegex("^call[ \t]+:(" + _name + ")(?:[ \t]+(.+))?$", _options)]
  private static partial Regex CallLabel();
  [GeneratedRegex("^for[ \t]+/l[ \t]+%%([A-Za-z])[ \t]+in[ \t]+\\(([^,)]+),([^,)]+),([^,)]+)\\)[ \t]+do[ \t]+set[ \t]+/a[ \t]+\"(" + _name + ")=([^\"]*)\"$", _options)]
  private static partial Regex ForNumeric();

  private sealed class Parser(string source) {

    private readonly Dictionary<string, IrLocal> _envLocals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IrLocal> _allLocals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sourceLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceLine> _lines = [];
    private readonly Dictionary<string, int> _emittedLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instruction> _instructions = [];
    private readonly IrLocal _errorLevel = new("batchErrorLevel", IrType.I32);
    private bool _delayedExpansion;
    private int _nextTemp;
    private int _nextSyntheticLabel;
    private int _nextFrameId;

    private sealed record SourceLine(int Number, string Text, int PhysicalLength);
    private sealed record ChainStage(string Separator, string Command);

    private static void CheckAscii(string raw, int lineNumber) {
      CheckCommandLength(raw.Length, lineNumber, "physical line");
      foreach (var character in raw)
        if (character is not '\t' && character is < ' ' or > '~')
          throw new Refusal(lineNumber, "only ASCII text with LF or CRLF is accepted", malformed: true);
    }

    private static string NormalizeTarget(string raw)
      => raw.StartsWith(':') && !raw.Equals(":eof", StringComparison.OrdinalIgnoreCase) ? raw[1..] : raw;

    public Script Read() {
      var lineNumber = 0;
      var sawEcho = false;
      var localized = false;
      var rawLines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
      for (var rawIndex = 0; rawIndex < rawLines.Length; ++rawIndex) {
        var raw = rawLines[rawIndex];
        ++lineNumber;
        CheckAscii(raw, lineNumber);
        var line = raw.Trim();
        if (line.Length == 0)
          continue;
        if (!localized) {
          if (!sawEcho && line.Equals("@echo off", StringComparison.OrdinalIgnoreCase)) {
            sawEcho = true;
            continue;
          }
          var preamble = Preamble().Match(line);
          if (!preamble.Success)
            throw new Refusal(lineNumber, "missing SETLOCAL EnableExtensions DisableDelayedExpansion prologue", malformed: true);
          this._delayedExpansion = preamble.Groups[1].Value.Equals("Enable", StringComparison.OrdinalIgnoreCase);
          localized = true;
          continue;
        }
        if (line == "(") {
          var blockStart = lineNumber;
          var parts = new List<string>();
          var blockLength = raw.Length;
          var closed = false;
          while (++rawIndex < rawLines.Length) {
            var innerRaw = rawLines[rawIndex];
            ++lineNumber;
            CheckAscii(innerRaw, lineNumber);
            var inner = innerRaw.Trim();
            if (inner.Length == 0)
              continue;
            if (inner == ")") {
              closed = true;
              break;
            }
            parts.Add(inner);
            blockLength += innerRaw.Length;
          }
          if (!closed || parts.Count == 0)
            throw new Refusal(blockStart, "unclosed or empty parenthesized block", malformed: true);
          this._lines.Add(new(blockStart, string.Join(" & ", parts), blockLength));
          continue;
        }
        var label = Label().Match(line);
        if (label.Success) {
          if (!this._sourceLabels.TryAdd(label.Groups[1].Value, this._lines.Count))
            throw new Refusal(lineNumber, $"duplicate label {label.Groups[1].Value}", malformed: true);
        } else {
          this._lines.Add(new(lineNumber, line, raw.Length));
        }
      }

      if (!localized || this._lines.Count == 0)
        throw new Refusal(lineNumber, "the script has no instructions", malformed: true);

      this._allLocals.Add("$errorlevel", this._errorLevel);
      this.ExpandFrame(0, [], isSubroutine: false, returnLabel: null, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

      var preassigned = new HashSet<IrLocal> { this._errorLevel };
      var scoped = this._envLocals.Values.ToHashSet();
      return new(this._instructions, this._emittedLabels, this._allLocals, preassigned: preassigned, scopedLocals: scoped);
    }

    private void ExpandFrame(int startSourceIndex, int[] args, bool isSubroutine, string? returnLabel, HashSet<string> activeCalls) {
      var frameId = this._nextFrameId++;
      // Emit every source line reachable in this frame so local GOTO/IF targets stay direct.
      var entryLabel = this.FrameTarget(frameId, startSourceIndex);
      var reachableSource = this.CollectFrameLines(startSourceIndex, args.Length > 0);
      var ordered = reachableSource.Order().ToArray();

      // Ensure the frame starts at startSourceIndex when entered.
      if (ordered[0] != startSourceIndex)
        this.Emit(new Jump(this._lines[startSourceIndex].Number, entryLabel, null));

      for (var position = 0; position < ordered.Length; ++position) {
        var sourceIndex = ordered[position];
        var nextSourceIndex = position + 1 < ordered.Length ? ordered[position + 1] : -1;
        this.Mark(this.FrameTarget(frameId, sourceIndex));
        var terminated = this.EmitLine(this._lines[sourceIndex], frameId, args, isSubroutine, returnLabel, activeCalls);

        // If this line can fall through to sourceIndex + 1 and the next emitted line isn't sourceIndex + 1, bridge it.
        if (!terminated && sourceIndex + 1 < this._lines.Count && nextSourceIndex != sourceIndex + 1)
          this.Emit(new Jump(this._lines[sourceIndex].Number, this.FrameTarget(frameId, sourceIndex + 1), null));
        else if (!terminated && sourceIndex + 1 == this._lines.Count && isSubroutine) {
          this.Emit(new FrameUnwind(this._lines[sourceIndex].Number, frameId));
          this.Emit(new Jump(this._lines[sourceIndex].Number, returnLabel!, null));
        }
      }
    }

    private HashSet<int> CollectFrameLines(int startSourceIndex, bool hasArgs) {
      var visited = new HashSet<int>();
      var queue = new Queue<int>();
      if (startSourceIndex < this._lines.Count && visited.Add(startSourceIndex))
        queue.Enqueue(startSourceIndex);

      while (queue.TryDequeue(out var index)) {
        var line = this._lines[index];
        if (ForNumeric().IsMatch(line.Text) || IfCmpSetElse().IsMatch(line.Text)
            || IfEqSetElse().IsMatch(line.Text) || IfTrapExit().IsMatch(line.Text)) {
          Enqueue(index + 1);
          continue;
        }
        var emptyArg = IfEmptyArgGoto().Match(line.Text);
        if (emptyArg.Success) {
          if (!hasArgs) {
            var target = NormalizeTarget(emptyArg.Groups[1].Value);
            if (!target.Equals(":eof", StringComparison.OrdinalIgnoreCase) && this._sourceLabels.TryGetValue(target, out var destination))
              Enqueue(destination);
          } else {
            Enqueue(index + 1);
          }
          continue;
        }
        var branch = IfGoto().Match(line.Text);
        if (branch.Success) {
          var target = NormalizeTarget(branch.Groups[4].Value);
          if (!target.Equals(":eof", StringComparison.OrdinalIgnoreCase) && this._sourceLabels.TryGetValue(target, out var destination))
            Enqueue(destination);
          Enqueue(index + 1);
          continue;
        }
        var eqBranch = IfEqGoto().Match(line.Text);
        if (eqBranch.Success) {
          var target = NormalizeTarget(eqBranch.Groups[4].Value);
          if (!target.Equals(":eof", StringComparison.OrdinalIgnoreCase) && this._sourceLabels.TryGetValue(target, out var destination))
            Enqueue(destination);
          Enqueue(index + 1);
          continue;
        }
        var stages = SplitChain(line.Text, line.Number);
        var unconditionalTerminal = false;
        foreach (var stage in stages) {
          var jump = Goto().Match(stage.Command);
          if (jump.Success) {
            var target = NormalizeTarget(jump.Groups[1].Value);
            if (!target.Equals(":eof", StringComparison.OrdinalIgnoreCase) && this._sourceLabels.TryGetValue(target, out var destination))
              Enqueue(destination);
            if (stage.Separator.Length == 0)
              unconditionalTerminal = true;
          } else if (ExitStatus().IsMatch(stage.Command) && stage.Separator.Length == 0) {
            unconditionalTerminal = true;
          }
        }
        if (!unconditionalTerminal)
          Enqueue(index + 1);
      }

      return visited;

      void Enqueue(int target) {
        if (target < this._lines.Count && visited.Add(target))
          queue.Enqueue(target);
      }
    }

    private static IrBinaryOp ParseComparison(string op) => op.ToUpperInvariant() switch {
      "EQU" => IrBinaryOp.Equal,
      "NEQ" => IrBinaryOp.NotEqual,
      "LSS" => IrBinaryOp.Less,
      "LEQ" => IrBinaryOp.LessOrEqual,
      "GTR" => IrBinaryOp.Greater,
      _ => IrBinaryOp.GreaterOrEqual
    };

    private void EmitConditionalJump(int number, IrExpr condition, string rawTarget, int frameId, bool isSubroutine, string? returnLabel) {
      var target = NormalizeTarget(rawTarget);
      if (target.Equals(":eof", StringComparison.OrdinalIgnoreCase)) {
        var eofBranch = this.SyntheticLabel();
        var fallthrough = this.SyntheticLabel();
        this.Emit(new Jump(number, eofBranch, condition));
        this.Emit(new Jump(number, fallthrough, null));
        this.Mark(eofBranch);
        this.EmitReturn(number, frameId, isSubroutine, returnLabel);
        this.Mark(fallthrough);
      } else {
        this.Emit(new Jump(number, this.ResolveLabel(target, frameId, number), condition));
      }
    }

    private bool EmitLine(SourceLine sourceLine, int frameId, int[] args, bool isSubroutine, string? returnLabel, HashSet<string> activeCalls) {
      var line = sourceLine.Text;
      var number = sourceLine.Number;
      var physicalLength = sourceLine.PhysicalLength;

      if (line.StartsWith("for ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("for\t", StringComparison.OrdinalIgnoreCase)) {
        this.EmitForNumeric(line, physicalLength, number);
        return false;
      }

      var emptyArg = IfEmptyArgGoto().Match(line);
      if (emptyArg.Success) {
        if (args.Length == 0) {
          var target = NormalizeTarget(emptyArg.Groups[1].Value);
          if (target.Equals(":eof", StringComparison.OrdinalIgnoreCase))
            this.EmitReturn(number, frameId, isSubroutine, returnLabel);
          else
            this.Emit(new Jump(number, this.ResolveLabel(target, frameId, number), null));
          return true;
        }
        return false;
      }

      var branch = IfGoto().Match(line);
      if (branch.Success) {
        CheckCommandLength(physicalLength + ExpansionGrowth(branch.Groups[1]) + ExpansionGrowth(branch.Groups[3]), number, "expanded IF command");
        var left = this.Operand(branch.Groups[1].Value, number, args);
        var right = this.Operand(branch.Groups[3].Value, number, args);
        var condition = new IrBinary(ParseComparison(branch.Groups[2].Value), left, right);
        this.EmitConditionalJump(number, condition, branch.Groups[4].Value, frameId, isSubroutine, returnLabel);
        return false;
      }

      var eqBranch = IfEqGoto().Match(line);
      if (eqBranch.Success) {
        CheckCommandLength(physicalLength + ExpansionGrowth(eqBranch.Groups[2]) + ExpansionGrowth(eqBranch.Groups[3]), number, "expanded IF command");
        var left = this.Operand(eqBranch.Groups[2].Value, number, args);
        var right = this.Operand(eqBranch.Groups[3].Value, number, args);
        var op = eqBranch.Groups[1].Success ? IrBinaryOp.NotEqual : IrBinaryOp.Equal;
        this.EmitConditionalJump(number, new IrBinary(op, left, right), eqBranch.Groups[4].Value, frameId, isSubroutine, returnLabel);
        return false;
      }

      var cmpSet = IfCmpSetElse().Match(line);
      if (cmpSet.Success) {
        CheckCommandLength(physicalLength + ExpansionGrowth(cmpSet.Groups[1]) + ExpansionGrowth(cmpSet.Groups[3])
          + ExpansionGrowth(cmpSet.Groups[5]) + ExpansionGrowth(cmpSet.Groups[7]), number, "expanded IF command");
        var dst1 = cmpSet.Groups[4].Value;
        var dst2 = cmpSet.Groups[6].Value;
        if (!dst1.Equals(dst2, StringComparison.OrdinalIgnoreCase))
          throw new Refusal(number, line);
        var condition = new IrBinary(ParseComparison(cmpSet.Groups[2].Value),
          this.Operand(cmpSet.Groups[1].Value, number, args), this.Operand(cmpSet.Groups[3].Value, number, args));
        var whenTrue = this.Operand(cmpSet.Groups[5].Value, number, args);
        var whenFalse = this.Operand(cmpSet.Groups[7].Value, number, args);
        this.Emit(new Assignment(number, this.EnvLocal(dst1), new IrConditional(condition, whenTrue, whenFalse)));
        return false;
      }

      var eqSet = IfEqSetElse().Match(line);
      if (eqSet.Success) {
        CheckCommandLength(physicalLength + ExpansionGrowth(eqSet.Groups[2]) + ExpansionGrowth(eqSet.Groups[3])
          + ExpansionGrowth(eqSet.Groups[5]) + ExpansionGrowth(eqSet.Groups[7]), number, "expanded IF command");
        var dst1 = eqSet.Groups[4].Value;
        var dst2 = eqSet.Groups[6].Value;
        if (!dst1.Equals(dst2, StringComparison.OrdinalIgnoreCase))
          throw new Refusal(number, line);
        var op = eqSet.Groups[1].Success ? IrBinaryOp.NotEqual : IrBinaryOp.Equal;
        var condition = new IrBinary(op,
          this.Operand(eqSet.Groups[2].Value, number, args), this.Operand(eqSet.Groups[3].Value, number, args));
        var whenTrue = this.Operand(eqSet.Groups[5].Value, number, args);
        var whenFalse = this.Operand(eqSet.Groups[7].Value, number, args);
        this.Emit(new Assignment(number, this.EnvLocal(dst1), new IrConditional(condition, whenTrue, whenFalse)));
        return false;
      }

      var trapExit = IfTrapExit().Match(line);
      if (trapExit.Success) {
        CheckCommandLength(physicalLength + ExpansionGrowth(trapExit.Groups[1]) + ExpansionGrowth(trapExit.Groups[2])
          + ExpansionGrowth(trapExit.Groups[3]), number, "expanded IF command");
        var left = this.Operand(trapExit.Groups[1].Value, number, args);
        var right = this.Operand(trapExit.Groups[2].Value, number, args);
        var status = this.Operand(trapExit.Groups[3].Value, number, args);
        var trapBranch = this.SyntheticLabel();
        var afterTrap = this.SyntheticLabel();
        this.Emit(new Jump(number, trapBranch, new IrBinary(IrBinaryOp.Equal, left, right)));
        this.Emit(new Jump(number, afterTrap, null));
        this.Mark(trapBranch);
        this.Emit(new Assignment(number, this._errorLevel, status));
        this.EmitReturn(number, frameId, isSubroutine, returnLabel);
        this.Mark(afterTrap);
        return false;
      }

      var stages = SplitChain(line, number);
      var parsedStages = stages.Select(stage => this.ParseStage(stage, number)).ToArray();

      // Phase 1: expand all percent operands across the entire physical line before any command runs.
      var growth = 0;
      var preExpanded = new Dictionary<(int Stage, int Slot), IrExpr>();
      var needsSnapshot = parsedStages.Length > 1;
      for (var i = 0; i < parsedStages.Length; ++i)
        foreach (var (slot, token) in parsedStages[i].PercentOperands()) {
          growth += ExpansionGrowth(token);
          if (token[0] == '!')
            continue;
          var expr = this.Operand(token, number, args);
          if (needsSnapshot && expr is IrLocalRef) {
            var temp = this.Temp();
            this.Emit(new Assignment(number, temp, expr));
            preExpanded.Add((i, slot), new IrLocalRef(temp));
          } else {
            preExpanded.Add((i, slot), expr);
          }
        }
      CheckCommandLength(physicalLength + growth, number, "expanded command");

      // Phase 2: execute stages left-to-right honoring & and &&.
      var lineEnd = parsedStages.Length > 1 ? this.SyntheticLabel() : null;
      var previousCanFail = false;
      var hasConditionalSkip = false;
      var terminated = false;
      for (var i = 0; i < parsedStages.Length; ++i) {
        var stage = parsedStages[i];
        if (stage.Separator == "&&" && previousCanFail) {
          hasConditionalSkip = true;
          this.Emit(new Jump(number, lineEnd!, new IrBinary(IrBinaryOp.NotEqual, new IrLocalRef(this._errorLevel), new IrConst(IrType.I32, 0))));
        }

        previousCanFail = false;
        terminated = false;
        switch (stage) {
          case SetArithStage arith:
            foreach (var (destination, expression) in arith.Assignments)
              this.Emit(new Assignment(number, this.EnvLocal(destination), new Arithmetic(expression, number, this.EnvLocal).Read()));
            break;
          case SetValueStage setValue: {
            var value = setValue.Operand[0] == '!' ? this.Operand(setValue.Operand, number, args) : preExpanded[(i, 0)];
            this.Emit(new Assignment(number, this.EnvLocal(setValue.Destination), value));
            break;
          }
          case SetLocalStage:
            this.Emit(new ScopePush(number, frameId));
            break;
          case EndLocalStage:
            this.Emit(new ScopePop(number, frameId));
            break;
          case GotoStage jump: {
            var target = NormalizeTarget(jump.Target);
            if (target.Equals(":eof", StringComparison.OrdinalIgnoreCase))
              this.EmitReturn(number, frameId, isSubroutine, returnLabel);
            else
              this.Emit(new Jump(number, this.ResolveLabel(target, frameId, number), null));
            terminated = true;
            break;
          }
          case ExitStage exit:
            if (exit.Operand is not null) {
              var status = exit.Operand[0] == '!' ? this.Operand(exit.Operand, number, args) : preExpanded[(i, 0)];
              this.Emit(new Assignment(number, this._errorLevel, status));
            }
            this.EmitReturn(number, frameId, isSubroutine, returnLabel);
            terminated = true;
            break;
          case CallStage call:
            if (!this._sourceLabels.TryGetValue(call.Target, out var calleeIndex))
              throw new Refusal(number, $"unknown label {call.Target}", malformed: true);
            if (!activeCalls.Add(call.Target))
              throw new Refusal(number, $"recursive CALL to :{call.Target}");
            var afterCall = this.SyntheticLabel();
            this.ExpandFrame(calleeIndex, call.Arguments, isSubroutine: true, returnLabel: afterCall, activeCalls);
            activeCalls.Remove(call.Target);
            this.Mark(afterCall);
            previousCanFail = true;
            break;
        }
      }

      if (lineEnd is not null)
        this.Mark(lineEnd);
      return terminated && !hasConditionalSkip;
    }

    private void EmitReturn(int line, int frameId, bool isSubroutine, string? returnLabel) {
      if (isSubroutine) {
        this.Emit(new FrameUnwind(line, frameId));
        this.Emit(new Jump(line, returnLabel!, null));
      } else {
        this.Emit(new Exit(line, new IrLocalRef(this._errorLevel)));
      }
    }

    private void EmitForNumeric(string line, int physicalLength, int number) {
      var match = ForNumeric().Match(line);
      if (!match.Success)
        throw new Refusal(number, line);

      var letter = match.Groups[1].Value[0];
      var startToken = match.Groups[2].Value.Trim();
      var stepToken = match.Groups[3].Value.Trim();
      var endToken = match.Groups[4].Value.Trim();
      if (startToken.Contains('%') || stepToken.Contains('%') || endToken.Contains('%'))
        throw new Refusal(number, "dynamic FOR /L bounds");

      var start = (int)Constant(startToken, number).Value;
      var step = (int)Constant(stepToken, number).Value;
      var end = (int)Constant(endToken, number).Value;
      if (step == 0)
        throw new Refusal(number, "FOR /L step cannot be zero");

      // Check post-body increment after the final executed iteration for Int32 overflow.
      if (step > 0 && start <= end) {
        var trips = ((long)end - start) / step + 1;
        var afterLast = start + trips * step;
        if (afterLast is > int.MaxValue or < int.MinValue)
          throw new Refusal(number, "FOR /L iterator increment exceeds Int32");
      } else if (step < 0 && start >= end) {
        var trips = ((long)start - end) / -(long)step + 1;
        var afterLast = start + trips * step;
        if (afterLast is > int.MaxValue or < int.MinValue)
          throw new Refusal(number, "FOR /L iterator increment exceeds Int32");
      }

      var destinationName = match.Groups[5].Value;
      var bodyText = match.Groups[6].Value;
      var forToken = "%%" + letter;
      var tokenCount = 0;
      for (var offset = 0; (offset = bodyText.IndexOf(forToken, offset, StringComparison.Ordinal)) >= 0; offset += 3)
        ++tokenCount;
      CheckCommandLength(physicalLength + tokenCount * (_maxInt32TextLength - 3), number, "expanded FOR command");

      var iterator = this.Temp();
      var bodyExpr = new Arithmetic(bodyText, number, this.EnvLocal, (letter, iterator)).Read();
      var destination = this.EnvLocal(destinationName);
      var header = this.SyntheticLabel();
      var after = this.SyntheticLabel();

      this.Emit(new Assignment(number, iterator, new IrConst(IrType.I32, start)));
      this.Mark(header);
      var exitCondition = new IrBinary(step > 0 ? IrBinaryOp.Greater : IrBinaryOp.Less, new IrLocalRef(iterator), new IrConst(IrType.I32, end));
      this.Emit(new Jump(number, after, exitCondition));
      this.Emit(new Assignment(number, destination, bodyExpr));
      this.Emit(new Assignment(number, iterator, new IrBinary(IrBinaryOp.Add, new IrLocalRef(iterator), new IrConst(IrType.I32, step))));
      this.Emit(new Jump(number, header, null));
      this.Mark(after);
    }

    private abstract record ParsedStage(string Separator) {
      public virtual IEnumerable<(int Slot, string Token)> PercentOperands() => [];
    }

    private sealed record SetArithStage(string Separator, List<(string Destination, string Expression)> Assignments) : ParsedStage(Separator);

    private sealed record SetValueStage(string Separator, string Destination, string Operand) : ParsedStage(Separator) {
      public override IEnumerable<(int Slot, string Token)> PercentOperands() { yield return (0, this.Operand); }
    }

    private sealed record SetLocalStage(string Separator) : ParsedStage(Separator);
    private sealed record EndLocalStage(string Separator) : ParsedStage(Separator);
    private sealed record GotoStage(string Separator, string Target) : ParsedStage(Separator);

    private sealed record ExitStage(string Separator, string? Operand) : ParsedStage(Separator) {
      public override IEnumerable<(int Slot, string Token)> PercentOperands() {
        if (this.Operand is not null)
          yield return (0, this.Operand);
      }
    }

    private sealed record CallStage(string Separator, string Target, int[] Arguments) : ParsedStage(Separator);

    private static List<(string Destination, string Expression)> SplitArithmeticAssignments(string body, int number) {
      var result = new List<(string Destination, string Expression)>();
      var depth = 0;
      var start = 0;
      for (var i = 0; i <= body.Length; ++i) {
        if (i < body.Length) {
          if (body[i] == '(')
            ++depth;
          else if (body[i] == ')')
            --depth;
          else if (body[i] == ',' && depth == 0) {
            AddPart(body[start..i]);
            start = i + 1;
          }
        } else {
          AddPart(body[start..]);
        }
      }
      return result;

      void AddPart(string raw) {
        var part = raw.Trim();
        var match = SetArithAssignment().Match(part);
        if (!match.Success)
          throw new Refusal(number, $"set /a \"{part}\"", malformed: true);
        result.Add((match.Groups[1].Value, match.Groups[2].Value));
      }
    }

    private ParsedStage ParseStage(ChainStage stage, int number) {
      var command = stage.Command;
      var arith = SetArithmetic().Match(command);
      if (arith.Success)
        return new SetArithStage(stage.Separator, SplitArithmeticAssignments(arith.Groups[1].Value, number));
      var set = SetValue().Match(command);
      if (set.Success) {
        var dest = set.Groups[1].Success ? set.Groups[1].Value : set.Groups[3].Value;
        var val = set.Groups[1].Success ? set.Groups[2].Value : set.Groups[4].Value;
        return new SetValueStage(stage.Separator, dest, val);
      }
      if (SetLocalCmd().IsMatch(command))
        return new SetLocalStage(stage.Separator);
      if (EndLocalCmd().IsMatch(command))
        return new EndLocalStage(stage.Separator);
      var jump = Goto().Match(command);
      if (jump.Success)
        return new GotoStage(stage.Separator, jump.Groups[1].Value);
      var exit = ExitStatus().Match(command);
      if (exit.Success)
        return new ExitStage(stage.Separator, exit.Groups[1].Success ? exit.Groups[1].Value : null);
      var call = CallLabel().Match(command);
      if (call.Success) {
        var rawArgs = call.Groups[2].Success
          ? call.Groups[2].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
          : [];
        if (rawArgs.Length > 9)
          throw new Refusal(number, "CALL with more than nine positional arguments");
        var values = new int[rawArgs.Length];
        for (var index = 0; index < rawArgs.Length; ++index) {
          if (rawArgs[index].Contains('%'))
            throw new Refusal(number, $"dynamic CALL argument {rawArgs[index]}");
          values[index] = (int)Constant(rawArgs[index], number).Value;
        }
        return new CallStage(stage.Separator, call.Groups[1].Value, values);
      }
      throw new Refusal(number, command, malformed: command.StartsWith(':'));
    }

    private static List<ChainStage> SplitChain(string line, int number) {
      var result = new List<ChainStage>();
      var inQuotes = false;
      var start = 0;
      var pendingSeparator = "";
      for (var index = 0; index < line.Length; ++index) {
        var ch = line[index];
        if (ch == '"') {
          inQuotes = !inQuotes;
          continue;
        }
        if (inQuotes || ch != '&')
          continue;

        var segment = line[start..index].Trim();
        if (segment.Length == 0)
          throw new Refusal(number, "empty command in chain", malformed: true);
        result.Add(new(pendingSeparator, segment));
        if (index + 1 < line.Length && line[index + 1] == '&') {
          pendingSeparator = "&&";
          ++index;
        } else {
          pendingSeparator = "&";
        }
        start = index + 1;
      }
      var tail = line[start..].Trim();
      if (tail.Length == 0)
        throw new Refusal(number, "trailing command separator", malformed: true);
      result.Add(new(pendingSeparator, tail));
      return result;
    }

    private string ResolveLabel(string target, int frameId, int number) {
      if (!this._sourceLabels.TryGetValue(target, out var sourceIndex))
        throw new Refusal(number, $"unknown label {target}", malformed: true);
      return this.FrameTarget(frameId, sourceIndex);
    }

    private string FrameTarget(int frameId, int sourceIndex)
      => $"$f{frameId.ToString(CultureInfo.InvariantCulture)}_{sourceIndex.ToString(CultureInfo.InvariantCulture)}";

    private string SyntheticLabel()
      => "$s" + this._nextSyntheticLabel++.ToString(CultureInfo.InvariantCulture);

    private void Mark(string label) => this._emittedLabels[label] = this._instructions.Count;

    private void Emit(Instruction instruction) {
      if (this._instructions.Count >= _maxExpandedInstructions)
        throw new Refusal(instruction.Line, "expanded Batch script exceeds instruction budget");
      this._instructions.Add(instruction);
    }

    private static int ExpansionGrowth(Group operand) => ExpansionGrowth(operand.Value);

    private static int ExpansionGrowth(string token)
      => token.Length > 0 && token[0] is '%' or '!' ? Math.Max(0, _maxInt32TextLength - token.Length) : 0;

    private static void CheckCommandLength(int length, int line, string kind) {
      if (length > _commandLimit)
        throw new Refusal(line, $"{kind} exceeds the {_commandLimit}-character cmd limit");
    }

    private IrExpr EnvOrErrorLevel(string name)
      => name.Equals("errorlevel", StringComparison.OrdinalIgnoreCase)
        ? new IrLocalRef(this._errorLevel)
        : new IrLocalRef(this.EnvLocal(name));

    private IrExpr Operand(string token, int line, int[] args) {
      if (token[0] == '!') {
        if (!this._delayedExpansion)
          throw new Refusal(line, $"delayed expansion {token} requires EnableDelayedExpansion");
        return this.EnvOrErrorLevel(token[1..^1]);
      }
      if (token[0] != '%')
        return Constant(token, line);
      if (token.Length == 2 && char.IsAsciiDigit(token[1])) {
        var slot = token[1] - '1';
        if (slot < 0 || slot >= args.Length)
          throw new Refusal(line, $"unbound positional parameter {token}");
        return new IrConst(IrType.I32, args[slot]);
      }
      return this.EnvOrErrorLevel(token[1..^1]);
    }

    private IrLocal EnvLocal(string name) {
      if (name.Equals("errorlevel", StringComparison.OrdinalIgnoreCase))
        throw new Refusal(1, "assigning to ERRORLEVEL shadows the dynamic status");
      if (!this._envLocals.TryGetValue(name, out var local)) {
        local = new($"v{this._envLocals.Count.ToString(CultureInfo.InvariantCulture)}", IrType.I32);
        this._envLocals.Add(name, local);
        this._allLocals.Add(name, local);
      }
      return local;
    }

    private IrLocal Temp() {
      var temp = new IrLocal($"t{this._nextTemp++.ToString(CultureInfo.InvariantCulture)}", IrType.I32);
      this._allLocals.Add("$" + temp.Name, temp);
      return temp;
    }

  }

  private static IrConst Constant(string text, int line) {
    if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
        !text.Equals(value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
      throw new Refusal(line, $"expected a canonical decimal Int32 literal, found {text}", malformed: true);
    return new(IrType.I32, value);
  }

}
