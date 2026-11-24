using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Batch;

public static partial class BatchFrontend {

  private const int _commandLimit = 8191;
  private const int _maxInt32TextLength = 11;
  private const string _name = "[A-Za-z_][A-Za-z_0-9]*";
  private const string _decimal = "(?:0|-?[1-9][0-9]*)";
  private const string _operand = "(?:%" + _name + "%|" + _decimal + ")";
  private const RegexOptions _options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

  [GeneratedRegex("^:(" + _name + ")$", _options)]
  private static partial Regex Label();
  [GeneratedRegex("^goto[ \t]+(" + _name + ")$", _options)]
  private static partial Regex Goto();
  [GeneratedRegex("^if[ \t]+(%" + _name + "%)[ \t]+(EQU|NEQ|LSS|LEQ|GTR|GEQ)[ \t]+(" + _operand + ")[ \t]+goto[ \t]+(" + _name + ")$", _options)]
  private static partial Regex IfGoto();
  [GeneratedRegex("^exit[ \t]+/b[ \t]+(" + _operand + ")$", _options)]
  private static partial Regex ExitStatus();
  [GeneratedRegex("^set[ \t]+/a[ \t]+\"(" + _name + ")=([^\"]*)\"$", _options)]
  private static partial Regex SetArithmetic();
  [GeneratedRegex("^setlocal[ \t]+EnableExtensions[ \t]+DisableDelayedExpansion$", _options)]
  private static partial Regex Preamble();

  private sealed class Parser(string source) {

    private readonly Dictionary<string, IrLocal> _locals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instruction> _instructions = [];

    public Script Read() {
      var lineNumber = 0;
      var sawEcho = false;
      var localized = false;
      foreach (var raw in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
        ++lineNumber;
        CheckCommandLength(raw.Length, lineNumber, "physical line");
        foreach (var character in raw)
          if (character is not '\t' && character is < ' ' or > '~')
            throw new Refusal(lineNumber, "only ASCII text with LF or CRLF is accepted", malformed: true);
        var line = raw.Trim();
        if (line.Length == 0)
          continue;
        if (!localized) {
          if (!sawEcho && line.Equals("@echo off", StringComparison.OrdinalIgnoreCase)) {
            sawEcho = true;
            continue;
          }
          if (!Preamble().IsMatch(line))
            throw new Refusal(lineNumber, "missing SETLOCAL EnableExtensions DisableDelayedExpansion prologue", malformed: true);
          localized = true;
          continue;
        }
        this.ReadLine(line, raw.Length, lineNumber);
      }

      if (!localized || this._instructions.Count == 0)
        throw new Refusal(lineNumber, "the script has no instructions", malformed: true);
      return new(this._instructions, this._labels, this._locals);
    }

    private void ReadLine(string line, int physicalLength, int number) {
      if (Label().Match(line) is { Success: true } label) {
        if (!this._labels.TryAdd(label.Groups[1].Value, this._instructions.Count))
          throw new Refusal(number, $"duplicate label {label.Groups[1].Value}", malformed: true);
      } else if (Goto().Match(line) is { Success: true } jump)
        this._instructions.Add(new Jump(number, jump.Groups[1].Value, null));
      else if (IfGoto().Match(line) is { Success: true } branch) {
        CheckCommandLength(physicalLength + ExpansionGrowth(branch.Groups[1]) + ExpansionGrowth(branch.Groups[3]), number, "expanded IF command");
        this._instructions.Add(new Jump(number, branch.Groups[4].Value, new BinaryCondition(this, number, branch).Expression));
      } else if (SetArithmetic().Match(line) is { Success: true } assignment)
        this._instructions.Add(new Assignment(number, this.Local(assignment.Groups[1].Value),
          new Arithmetic(assignment.Groups[2].Value, number, this.Local).Read()));
      else if (ExitStatus().Match(line) is { Success: true } exit) {
        CheckCommandLength(physicalLength + ExpansionGrowth(exit.Groups[1]), number, "expanded EXIT command");
        this._instructions.Add(new Exit(number, this.Operand(exit.Groups[1].Value, number)));
      } else
        throw new Refusal(number, line, malformed: line.StartsWith(':'));
    }

    private static int ExpansionGrowth(Group operand)
      => operand.ValueSpan[0] == '%' ? _maxInt32TextLength - operand.Length : 0;

    private static void CheckCommandLength(int length, int line, string kind) {
      if (length > _commandLimit)
        throw new Refusal(line, $"{kind} exceeds the {_commandLimit}-character cmd limit");
    }

    private IrExpr Operand(string token, int line) => token[0] == '%' ? new IrLocalRef(this.Local(token[1..^1])) : Constant(token, line);

    private IrLocal Local(string name) {
      if (!this._locals.TryGetValue(name, out var local)) {
        local = new($"v{this._locals.Count.ToString(CultureInfo.InvariantCulture)}", IrType.I32);
        this._locals.Add(name, local);
      }
      return local;
    }

    private sealed class BinaryCondition(Parser parser, int line, Match branch) {
      public IrBinary Expression => new(branch.Groups[2].Value.ToUpperInvariant() switch {
        "EQU" => IrBinaryOp.Equal,
        "NEQ" => IrBinaryOp.NotEqual,
        "LSS" => IrBinaryOp.Less,
        "LEQ" => IrBinaryOp.LessOrEqual,
        "GTR" => IrBinaryOp.Greater,
        _ => IrBinaryOp.GreaterOrEqual
      }, parser.Operand(branch.Groups[1].Value, line), parser.Operand(branch.Groups[3].Value, line));
    }

  }

  private static IrConst Constant(string text, int line) {
    if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
        !text.Equals(value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
      throw new Refusal(line, $"expected a canonical decimal Int32 literal, found {text}", malformed: true);
    return new(IrType.I32, value);
  }

}
