using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private const int _dosLineLimit = 127;
  private const int _dosEchoLimit = 126;
  private const string _dosToken = "(?:\"[^\"<>|]*\"|[^ \t\"=<>|]+)";
  private static readonly char[] _dosEffects = ['%', '|', '<', '>'];
  private static readonly char[] _dosSeparators = [' ', '\t', '=', '"'];

  [GeneratedRegex("^set[ \\t]+(" + _name + ")=(.*)$", _options)]
  private static partial Regex DosSet();
  [GeneratedRegex("^if[ \\t]+(not[ \\t]+)?(" + _dosToken + ")[ \\t]*==[ \\t]*(" + _dosToken + ")[ \\t]+goto[ \\t]+(" + _name + ")[ \\t]*$", _options)]
  private static partial Regex DosIf();
  [GeneratedRegex("^[Ee][Cc][Hh][Oo][ \\t]+PLYRESULT=%(" + _name + ")%$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
  private static partial Regex DosResult();
  [GeneratedRegex("^%" + _name + "%$", _options)]
  private static partial Regex DosVariable();

  private sealed class DosParser(string source) {

    private readonly Dictionary<string, IrLocal> _locals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instruction> _instructions = [];
    private readonly Dictionary<int, DosExpansion> _expansions = [];

    public Script Read() {
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
        if (!first || !line.TrimEnd().Equals("@echo off", StringComparison.OrdinalIgnoreCase))
          this.ReadLine(line, raw.Length, number);
        first = false;
      }
      if (this._instructions.Count == 0)
        throw new Refusal(number, "the script has no instructions", malformed: true);
      return new(this._instructions, this._labels, this._locals, this.CheckExpansions);
    }

    private void ReadLine(string line, int width, int number) {
      if (Label().Match(line.TrimEnd()) is { Success: true } label) {
        this._labels.TryAdd(DosLabel(label.Groups[1].Value), this._instructions.Count);
      } else if (Goto().Match(line.TrimEnd()) is { Success: true } jump) {
        this._instructions.Add(new Jump(number, DosLabel(jump.Groups[1].Value), null));
      } else if (DosSet().Match(line) is { Success: true } set) {
        var value = set.Groups[2].Value;
        if (value.IndexOfAny(_dosEffects) >= 0)
          throw new Refusal(number, "DOS SET expansion or redirection");
        this._instructions.Add(new Assignment(number, this.Local(set.Groups[1].Value), new IrStrConst(value)));
      } else if (DosIf().Match(line) is { Success: true } branch) {
        var left = this.Operand(branch.Groups[2].Value, number);
        var right = this.Operand(branch.Groups[3].Value, number);
        this._expansions.Add(this._instructions.Count, new(width, [left, right], IsResult: false));
        IrExpr equal = new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left.Expression, right.Expression]);
        if (branch.Groups[1].Success)
          equal = new IrUnary(IrUnaryOp.LogicalNot, equal);
        this._instructions.Add(new Jump(number, DosLabel(branch.Groups[4].Value), equal));
      } else if (DosResult().Match(line) is { Success: true } result) {
        var name = result.Groups[1].Value;
        var operand = new DosOperand($"%{name}%", this.Local(name), Quoted: false);
        this._expansions.Add(this._instructions.Count, new(width, [operand], IsResult: true));
        this._instructions.Add(new Exit(number, DecodeResult(operand.Expression)));
      } else {
        throw new Refusal(number, line, malformed: line.StartsWith(':'));
      }
    }

    private DosOperand Operand(string token, int line) {
      var quoted = token.StartsWith('"');
      var inner = quoted ? token[1..^1] : token;
      if (DosVariable().IsMatch(inner))
        return new(token, this.Local(inner[1..^1]), quoted);
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

  private sealed record DosOperand(string Token, IrLocal? Local, bool Quoted) {

    public IrExpr Expression => this.Local is not { } local ? new IrStrConst(this.Token)
      : this.Quoted ? new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String,
        [new IrStrConst("\""), new IrLocalRef(local), new IrStrConst("\"")])
      : new IrLocalRef(local);

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
