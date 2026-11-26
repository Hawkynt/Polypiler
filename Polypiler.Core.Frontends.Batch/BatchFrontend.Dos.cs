using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private const int _dosLineLimit = 127;
  private const int _dosEchoLimit = 126;
  private const string _dosToken = "(?:\"[^\"<>|]*\"|[^ \t\"=<>|]+)";
  private static readonly char[] _dosRedirections = ['|', '<', '>'];
  private static readonly char[] _dosEffects = ['%', '|', '<', '>'];
  private static readonly char[] _dosSeparators = [' ', '\t', '=', '"'];
  private static readonly char[] _dosArgDelimiters = [' ', '\t', ',', ';', '='];

  [GeneratedRegex("^set[ \\t]+(" + _name + ")=(.*)$", _options)]
  private static partial Regex DosSet();
  [GeneratedRegex("^if[ \\t]+(not[ \\t]+)?(" + _dosToken + ")[ \\t]*==[ \\t]*(" + _dosToken + ")[ \\t]+(.+)$", _options)]
  private static partial Regex DosIfEqual();
  [GeneratedRegex("^if[ \\t]+(not[ \\t]+)?errorlevel[ \\t]+([0-9]+)[ \\t]+(.+)$", _options)]
  private static partial Regex DosIfErrorLevel();
  [GeneratedRegex("^[Ee][Cc][Hh][Oo][ \\t]+PLYRESULT=%(" + _name + ")%$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
  private static partial Regex DosResult();
  [GeneratedRegex("^echo[ \\t]+(.*)$", _options)]
  private static partial Regex DosEcho();
  [GeneratedRegex("^call[ \\t]+([^ \\t]+)(?:[ \\t]+(.*))?$", _options)]
  private static partial Regex DosCall();
  [GeneratedRegex("^shift(?:[ \\t]+(.*))?$", _options)]
  private static partial Regex DosShift();
  [GeneratedRegex("^%" + _name + "%$", _options)]
  private static partial Regex DosVariable();

  private sealed class DosParser {

    private readonly string _entryKey;
    private readonly string _entryInvocation;
    private readonly string _entrySource;
    private readonly IReadOnlyList<string> _entryArguments;
    private readonly Dictionary<string, string> _companions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _statusFixtures = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IrLocal> _locals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instruction> _instructions = [];
    private readonly Dictionary<int, DosExpansion> _expansions = [];
    private readonly IrLocal _errorLevel = new("batchErrorLevel", IrType.I32);
    private bool _usesExtendedDos;
    private int _nextLabelId;
    private int _nextFrameId;

    public DosParser(string entryFile, string entrySource, BatchImportOptions? options) {
      this._entryKey = NormalizeDosFileKey(entryFile);
      this._entryInvocation = options?.EntryInvocation ?? Path.GetFileName(entryFile);
      this._entrySource = entrySource;
      this._entryArguments = options?.EntryArguments ?? [];
      if (options?.CompanionFiles is { } files) {
        this._usesExtendedDos = true;
        foreach (var (key, content) in files) {
          var normalized = NormalizeDosFileKey(key);
          this._companions[normalized] = content;
          this._companions.TryAdd(Path.GetFileName(normalized), content);
        }
      }
      if (options?.EntryArguments is { Count: > 0 } || options?.EntryInvocation is not null)
        this._usesExtendedDos = true;
      if (options?.StatusFixtures is { } fixtures)
        foreach (var (key, status) in fixtures)
          this._statusFixtures[NormalizeDosFileKey(key)] = status;
    }

    public Script Read() {
      this._locals.Add("$errorlevel", this._errorLevel);
      var entryFrame = new DosFrame([new IrStrConst(this._entryInvocation), .. this._entryArguments.Select(arg => new IrStrConst(arg))]);
      var activeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { this._entryKey };
      var doneLabel = this.SyntheticLabel();
      this.ExpandFile(this._entrySource, entryFrame, returnLabel: doneLabel, isTopLevel: true, activeFiles);

      if (this._instructions.Count == 0)
        throw new Refusal(1, "the script has no instructions", malformed: true);

      if (this._usesExtendedDos) {
        this.Mark(doneLabel);
        this._instructions.Add(new Exit(1, new IrLocalRef(this._errorLevel)));
      }

      var preassigned = new HashSet<IrLocal> { this._errorLevel };
      return new(this._instructions, this._labels, this._locals, this._usesExtendedDos ? null : this.CheckExpansions, preassigned: preassigned);
    }

    private sealed class DosFrame(IReadOnlyList<IrExpr> arguments) {
      public IReadOnlyList<IrExpr> Arguments { get; } = arguments;
      public int ShiftOffset { get; set; }

      public IrExpr Slot(int index) {
        var effective = this.ShiftOffset + index;
        return effective >= 0 && effective < this.Arguments.Count ? this.Arguments[effective] : new IrStrConst("");
      }
    }

    private sealed record RawDosLine(int Number, string Text, int PhysicalWidth);

    private List<RawDosLine> ReadRawLines(string source, out Dictionary<string, int> fileLabels) {
      fileLabels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var lines = new List<RawDosLine>();
      var number = 0;
      var first = true;
      foreach (var raw in source.Split("\r\n", StringSplitOptions.None)) {
        ++number;
        if (raw.Any(character => character != '\t' && character is < ' ' or > '~'))
          throw new Refusal(number, "only ASCII text with CRLF line endings is accepted by DOS", malformed: true);
        if (raw.Length > _dosLineLimit)
          throw new Refusal(number, $"physical line exceeds the {_dosLineLimit}-byte DOS limit");

        var line = raw.TrimStart(' ', '\t');
        if (line.Length == 0)
          continue;
        if (first && line.TrimEnd().Equals("@echo off", StringComparison.OrdinalIgnoreCase)) {
          first = false;
          continue;
        }
        first = false;
        var labelMatch = Label().Match(line.TrimEnd());
        if (labelMatch.Success)
          fileLabels.TryAdd(DosLabel(labelMatch.Groups[1].Value), lines.Count);
        else
          lines.Add(new(number, line, raw.Length));
      }
      return lines;
    }

    private void ExpandFile(string source, DosFrame frame, string returnLabel, bool isTopLevel, HashSet<string> activeFiles) {
      var frameId = this._nextFrameId++;
      var rawLines = this.ReadRawLines(source, out var fileLabels);

      // Register file-local labels scoped to this frame invocation.
      var lineLabels = new string[rawLines.Count];
      for (var index = 0; index < rawLines.Count; ++index)
        lineLabels[index] = $"$df{frameId.ToString(CultureInfo.InvariantCulture)}_{index.ToString(CultureInfo.InvariantCulture)}";

      string ResolveTarget(string rawTarget, int line) {
        var shortName = DosLabel(rawTarget);
        if (!fileLabels.TryGetValue(shortName, out var targetIndex))
          throw new Refusal(line, $"unknown label {rawTarget}", malformed: true);
        return targetIndex < lineLabels.Length ? lineLabels[targetIndex] : returnLabel;
      }

      var transferred = false;
      for (var index = 0; index < rawLines.Count; ++index) {
        this.Mark(lineLabels[index]);
        transferred = this.EmitDosCommand(rawLines[index].Text, rawLines[index].PhysicalWidth, rawLines[index].Number,
          frame, returnLabel, ResolveTarget, activeFiles);
        if (transferred) {
          // A direct batch transfer replaces the current file; remaining lines never run.
          break;
        }
      }

      if (!transferred && (!isTopLevel || this._usesExtendedDos))
        this._instructions.Add(new Jump(rawLines.Count > 0 ? rawLines[^1].Number : 1, returnLabel, null));
    }

    private bool EmitDosCommand(string line, int width, int number, DosFrame frame, string returnLabel,
      Func<string, int, string> resolveTarget, HashSet<string> activeFiles) {
      var trimmed = line.TrimEnd();

      var jump = Goto().Match(trimmed);
      if (jump.Success) {
        if (jump.Groups[1].Value.Equals(":eof", StringComparison.OrdinalIgnoreCase))
          throw new Refusal(number, "GOTO :EOF is not supported in dos622");
        this._instructions.Add(new Jump(number, resolveTarget(jump.Groups[1].Value, number), null));
        return false;
      }

      var shift = DosShift().Match(trimmed);
      if (shift.Success) {
        if (shift.Groups[1].Success && shift.Groups[1].Value.Trim().Length > 0)
          throw new Refusal(number, $"SHIFT {shift.Groups[1].Value.Trim()}");
        this._usesExtendedDos = true;
        ++frame.ShiftOffset;
        return false;
      }

      var set = DosSet().Match(line);
      if (set.Success) {
        var value = set.Groups[2].Value;
        if (value.IndexOfAny(_dosRedirections) >= 0)
          throw new Refusal(number, "DOS SET redirection");
        if (!this._usesExtendedDos && value.Contains('%'))
          throw new Refusal(number, "DOS SET expansion or redirection");
        var expr = value.Contains('%') ? this.ExpandDosTemplate(value, frame, number) : new IrStrConst(value);
        this._instructions.Add(new Assignment(number, this.Local(set.Groups[1].Value), expr));
        return false;
      }

      var ifError = DosIfErrorLevel().Match(trimmed);
      if (ifError.Success) {
        this._usesExtendedDos = true;
        var threshold = (int)Constant(ifError.Groups[2].Value, number).Value;
        var op = ifError.Groups[1].Success ? IrBinaryOp.Less : IrBinaryOp.GreaterOrEqual;
        var condition = new IrBinary(op, new IrLocalRef(this._errorLevel), new IrConst(IrType.I32, threshold));
        var thenLabel = this.SyntheticLabel();
        var afterLabel = this.SyntheticLabel();
        this._instructions.Add(new Jump(number, thenLabel, condition));
        this._instructions.Add(new Jump(number, afterLabel, null));
        this.Mark(thenLabel);
        this.EmitDosCommand(ifError.Groups[3].Value.Trim(), width, number, frame, returnLabel, resolveTarget, activeFiles);
        this.Mark(afterLabel);
        return false;
      }

      var branch = DosIfEqual().Match(trimmed);
      if (branch.Success) {
        var left = this.Operand(branch.Groups[2].Value, frame, number);
        var right = this.Operand(branch.Groups[3].Value, frame, number);
        var rest = branch.Groups[4].Value.Trim();
        IrExpr equal = new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left.Expression, right.Expression]);
        if (branch.Groups[1].Success)
          equal = new IrUnary(IrUnaryOp.LogicalNot, equal);

        var targetJump = Goto().Match(rest);
        if (targetJump.Success) {
          if (targetJump.Groups[1].Value.Equals(":eof", StringComparison.OrdinalIgnoreCase))
            throw new Refusal(number, "GOTO :EOF is not supported in dos622");
          if (!this._usesExtendedDos)
            this._expansions.Add(this._instructions.Count, new(width, [left, right], IsResult: false));
          this._instructions.Add(new Jump(number, resolveTarget(targetJump.Groups[1].Value, number), equal));
          return false;
        }

        this._usesExtendedDos = true;
        var thenLabel = this.SyntheticLabel();
        var afterLabel = this.SyntheticLabel();
        this._instructions.Add(new Jump(number, thenLabel, equal));
        this._instructions.Add(new Jump(number, afterLabel, null));
        this.Mark(thenLabel);
        this.EmitDosCommand(rest, width, number, frame, returnLabel, resolveTarget, activeFiles);
        this.Mark(afterLabel);
        return false;
      }

      if (!this._usesExtendedDos) {
        var result = DosResult().Match(line);
        if (result.Success) {
          var name = result.Groups[1].Value;
          var operand = new DosOperand($"%{name}%", this.Local(name), Quoted: false);
          this._expansions.Add(this._instructions.Count, new(width, [operand], IsResult: true));
          this._instructions.Add(new Exit(number, DecodeResult(operand.Expression)));
          return false;
        }
      }

      var echo = DosEcho().Match(line);
      if (echo.Success) {
        var text = echo.Groups[1].Value;
        if (text.IndexOfAny(_dosRedirections) >= 0)
          throw new Refusal(number, "DOS ECHO redirection");
        this._usesExtendedDos = true;
        this._instructions.Add(new WriteLine(number, this.ExpandDosTemplate(text, frame, number)));
        return false;
      }

      var call = DosCall().Match(trimmed);
      if (call.Success) {
        this._usesExtendedDos = true;
        var targetSpelling = call.Groups[1].Value;
        var rawArgs = call.Groups[2].Success ? call.Groups[2].Value : "";
        var (calleeKey, calleeSource) = this.ResolveDosBatch(targetSpelling, number);
        if (!activeFiles.Add(calleeKey))
          throw new Refusal(number, $"recursive DOS batch call to {targetSpelling}");

        var calleeFrame = this.CreateCallFrame(targetSpelling, rawArgs, frame, number);
        var afterCall = this.SyntheticLabel();
        this.ExpandFile(calleeSource, calleeFrame, returnLabel: afterCall, isTopLevel: false, activeFiles);
        activeFiles.Remove(calleeKey);
        this.Mark(afterCall);
        return false;
      }

      // Direct invocation: declared status fixture or direct batch file transfer.
      var splitIndex = trimmed.IndexOfAny([' ', '\t']);
      var commandToken = splitIndex < 0 ? trimmed : trimmed[..splitIndex];
      var argTail = splitIndex < 0 ? "" : trimmed[(splitIndex + 1)..];
      if (!commandToken.Contains('%') && !commandToken.StartsWith(':')) {
        var normalized = NormalizeDosFileKey(commandToken);
        if (this._statusFixtures.TryGetValue(normalized, out var fixedStatus)) {
          this._usesExtendedDos = true;
          this._instructions.Add(new Assignment(number, this._errorLevel, new IrConst(IrType.I32, fixedStatus)));
          return false;
        }
        if (this._companions.ContainsKey(normalized) || commandToken.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) {
          this._usesExtendedDos = true;
          var (transferKey, transferSource) = this.ResolveDosBatch(commandToken, number);
          if (!activeFiles.Add(transferKey))
            throw new Refusal(number, $"recursive DOS batch transfer to {commandToken}");

          var transferFrame = this.CreateCallFrame(commandToken, argTail, frame, number);
          this.ExpandFile(transferSource, transferFrame, returnLabel: returnLabel, isTopLevel: false, activeFiles);
          activeFiles.Remove(transferKey);
          return true;
        }
      }

      throw new Refusal(number, line, malformed: line.StartsWith(':'));
    }

    private (string Key, string Source) ResolveDosBatch(string targetSpelling, int line) {
      if (targetSpelling.StartsWith(':'))
        throw new Refusal(line, $"CALL {targetSpelling} is not supported in dos622");
      if (targetSpelling.Contains('%'))
        throw new Refusal(line, $"computed DOS batch target {targetSpelling}");
      var key = NormalizeDosFileKey(targetSpelling);
      if (this._companions.TryGetValue(key, out var source))
        return (key, source);
      throw new Refusal(line, $"undeclared DOS batch target {targetSpelling}");
    }

    private DosFrame CreateCallFrame(string targetSpelling, string rawArgs, DosFrame callerFrame, int line) {
      if (rawArgs.IndexOfAny(_dosRedirections) >= 0)
        throw new Refusal(line, "DOS CALL redirection");
      var tokens = TokenizeDosArguments(rawArgs);
      var arguments = new List<IrExpr>(tokens.Count + 1) { new IrStrConst(targetSpelling) };
      foreach (var token in tokens)
        arguments.Add(token.Contains('%') ? this.ExpandDosTemplate(token, callerFrame, line) : new IrStrConst(token));
      return new(arguments);
    }

    private static List<string> TokenizeDosArguments(string raw) {
      // MS-DOS 6.22 COMMAND.COM splits on space, tab, comma, semicolon, and equals without grouping quotes.
      var tokens = new List<string>();
      var index = 0;
      while (index < raw.Length) {
        while (index < raw.Length && raw[index] is ' ' or '\t' or ',' or ';' or '=')
          ++index;
        if (index >= raw.Length)
          break;
        var start = index;
        while (index < raw.Length && Array.IndexOf(_dosArgDelimiters, raw[index]) < 0)
          ++index;
        tokens.Add(raw[start..index]);
      }
      return tokens;
    }

    private IrExpr ExpandDosTemplate(string text, DosFrame frame, int line) {
      var pieces = new List<IrExpr>();
      var literalStart = 0;
      for (var index = 0; index < text.Length; ++index) {
        if (text[index] != '%')
          continue;

        if (index > literalStart)
          pieces.Add(new IrStrConst(text[literalStart..index]));

        if (index + 1 < text.Length && text[index + 1] == '%') {
          pieces.Add(new IrStrConst("%"));
          ++index;
          literalStart = index + 1;
          continue;
        }
        if (index + 1 < text.Length && char.IsAsciiDigit(text[index + 1])) {
          pieces.Add(frame.Slot(text[index + 1] - '0'));
          ++index;
          literalStart = index + 1;
          continue;
        }
        var close = text.IndexOf('%', index + 1);
        if (close < 0)
          throw new Refusal(line, $"unterminated variable expansion in {text}");
        var name = text[(index + 1)..close];
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')))
          throw new Refusal(line, $"unsupported DOS expansion %{name}%");
        pieces.Add(new IrLocalRef(this.Local(name)));
        index = close;
        literalStart = index + 1;
      }

      if (literalStart < text.Length)
        pieces.Add(new IrStrConst(text[literalStart..]));

      if (pieces.Count == 0)
        return new IrStrConst("");
      return pieces.Count == 1 ? pieces[0] : new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, pieces);
    }

    private static string NormalizeDosFileKey(string path) {
      var normalized = path.Replace('\\', '/');
      if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        normalized = normalized[2..];
      return normalized.TrimStart('/');
    }

    private string SyntheticLabel() => "$ds" + this._nextLabelId++.ToString(CultureInfo.InvariantCulture);

    private void Mark(string label) => this._labels[label] = this._instructions.Count;

    private DosOperand Operand(string token, DosFrame frame, int line) {
      var quoted = token.StartsWith('"');
      var inner = quoted ? token[1..^1] : token;
      if (DosVariable().IsMatch(inner))
        return new(token, this.Local(inner[1..^1]), quoted);
      if (inner.Length == 2 && inner[0] == '%' && char.IsAsciiDigit(inner[1])) {
        this._usesExtendedDos = true;
        var slotExpr = frame.Slot(inner[1] - '0');
        var expr = quoted
          ? new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [new IrStrConst("\""), slotExpr, new IrStrConst("\"")])
          : slotExpr;
        return new(token, null, quoted, expr);
      }
      if (inner.IndexOfAny(_dosEffects) >= 0 || inner.IndexOfAny(_dosSeparators) >= 0)
        throw new Refusal(line, "DOS IF operand with an embedded delimiter or expansion");
      return new(token, null, quoted);
    }

    private IrLocal Local(string name) {
      if (!this._locals.TryGetValue(name, out var local)) {
        local = new($"v{this._locals.Count.ToString(CultureInfo.InvariantCulture)}", IrType.String);
        this._locals.Add(name, local);
      }
      return local;
    }

    private void CheckExpansions(int[][] edges, int[] reachable) {
      this.CheckResultPaths(edges, reachable);
      var entries = new Dictionary<int, Dictionary<IrLocal, DosValueShape>> { [0] = [] };
      var pending = new Queue<int>([0]);
      while (pending.TryDequeue(out var index)) {
        var values = new Dictionary<IrLocal, DosValueShape>(entries[index]);
        if (this._instructions[index] is Assignment { Value: IrStrConst text } assignment)
          values[assignment.Destination] = DosValueShape.Of(text.Value);
        foreach (var successor in edges[index]) {
          if (!entries.TryGetValue(successor, out var next)) {
            entries.Add(successor, new(values));
            pending.Enqueue(successor);
          } else if (MergeShapes(next, values)) {
            pending.Enqueue(successor);
          }
        }
      }

      foreach (var index in reachable)
        if (this._expansions.TryGetValue(index, out var expansion))
          expansion.Verify(entries[index], this._instructions[index].Line);
    }

    private void CheckResultPaths(int[][] edges, int[] reachable) {
      var predecessors = reachable.SelectMany(from => edges[from].Select(to => (From: from, To: to)))
        .ToLookup(edge => edge.To, edge => edge.From);
      var canReturn = reachable.Where(index => this._instructions[index] is Exit).ToHashSet();
      var pending = new Queue<int>(canReturn);
      while (pending.TryDequeue(out var index))
        foreach (var predecessor in predecessors[index])
          if (canReturn.Add(predecessor))
            pending.Enqueue(predecessor);

      foreach (var index in reachable)
        if (!canReturn.Contains(index))
          throw new Refusal(this._instructions[index].Line, "control flow cannot reach a PLYRESULT record", malformed: true);
    }

    private static bool MergeShapes(Dictionary<IrLocal, DosValueShape> destination, Dictionary<IrLocal, DosValueShape> source) {
      var changed = false;
      foreach (var (local, shape) in source) {
        if (destination.TryGetValue(local, out var existing)) {
          var combined = existing.Join(shape);
          if (combined == existing)
            continue;
          destination[local] = combined;
        } else {
          destination.Add(local, shape);
        }
        changed = true;
      }
      return changed;
    }

    private static string DosLabel(string name) => name[..Math.Min(8, name.Length)];

    private static IrExpr DecodeResult(IrExpr text) {
      IrExpr value = Number(0);
      for (var index = 0; index < 8; ++index) {
        var code = new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [text, Number(index)]));
        var digit = new IrConditional(new IrBinary(IrBinaryOp.LessOrEqual, code, Number('9')),
          new IrBinary(IrBinaryOp.Subtract, code, Number('0')), new IrBinary(IrBinaryOp.Subtract, code, Number('A' - 10)));
        value = new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, value, Number(16)), digit);
      }
      return value;
    }

    private static IrConst Number(int value) => new(IrType.I32, value);

  }

  private sealed record DosOperand(string Token, IrLocal? Local, bool Quoted, IrExpr? OverrideExpression = null) {

    public IrExpr Expression => this.OverrideExpression ?? (this.Local is not { } local ? new IrStrConst(this.Token)
      : this.Quoted ? new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String,
        [new IrStrConst("\""), new IrLocalRef(local), new IrStrConst("\"")])
      : new IrLocalRef(local));

  }

  private sealed record DosExpansion(int PhysicalWidth, IReadOnlyList<DosOperand> Operands, bool IsResult) {

    public void Verify(IReadOnlyDictionary<IrLocal, DosValueShape> values, int line) {
      var width = this.PhysicalWidth;
      foreach (var operand in this.Operands) {
        if (operand.Local is not { } local)
          continue;
        var shape = values[local];
        if (this.IsResult && shape.NotHex)
          throw new Refusal(line, "PLYRESULT requires eight uppercase hexadecimal digits", malformed: true);
        if (!this.IsResult && (shape.Delimited || (shape.CanBeEmpty && !operand.Quoted)))
          throw new Refusal(line, "DOS IF expansion has a missing operand or an embedded delimiter");
        width += shape.Width + (operand.Quoted ? 2 : 0) - operand.Token.Length;
      }
      var limit = this.IsResult ? _dosEchoLimit : _dosLineLimit;
      if (width > limit)
        throw new Refusal(line, $"expanded {(this.IsResult ? "ECHO" : "IF")} command exceeds the {limit}-byte DOS limit");
    }

  }

  private readonly record struct DosValueShape(int Width, bool CanBeEmpty, bool Delimited, bool NotHex) {

    public static DosValueShape Of(string text) => new(text.Length, text.Length == 0,
      text.IndexOfAny(_dosSeparators) >= 0, text.Length != 8 || text.Any(character => !char.IsAsciiHexDigitUpper(character)));

    public DosValueShape Join(DosValueShape other) => new(Math.Max(this.Width, other.Width), this.CanBeEmpty || other.CanBeEmpty,
      this.Delimited || other.Delimited, this.NotHex || other.NotHex);

  }

}
