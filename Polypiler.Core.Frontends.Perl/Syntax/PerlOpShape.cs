using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>
/// The shape of the op tree perl builds for an expression, as the parser sees the expression: every
/// operator as <c>(name operand ...)</c> under the name of perl's op for it (<c>add</c>, <c>seq</c>,
/// <c>cond_expr</c>, <c>ftis</c>), package variables as <c>$name</c>, constants as their value. The
/// layer test writes perl's own op tree the same way, which checks precedence, associativity and the
/// operands named unary and list operators take.
/// </summary>
internal static class PerlOpShape {

  private static readonly Dictionary<string, string> _binary = new(StringComparer.Ordinal) {
    ["+"] = "add", ["-"] = "subtract", ["*"] = "multiply", ["/"] = "divide", ["%"] = "modulo", ["**"] = "pow", ["x"] = "repeat",
    ["."] = "concat", ["<<"] = "left_shift", [">>"] = "right_shift", ["<"] = "lt", [">"] = "gt", ["<="] = "le", [">="] = "ge",
    ["=="] = "eq", ["!="] = "ne", ["<=>"] = "ncmp", ["lt"] = "slt", ["gt"] = "sgt", ["le"] = "sle", ["ge"] = "sge", ["eq"] = "seq",
    ["ne"] = "sne", ["cmp"] = "scmp", ["&"] = "bit_and", ["|"] = "bit_or", ["^"] = "bit_xor", ["&&"] = "and", ["and"] = "and",
    ["||"] = "or", ["or"] = "or", ["//"] = "dor", ["xor"] = "xor", ["isa"] = "isa"
  };

  private static readonly Dictionary<char, string> _fileTests = new() {
    ['e'] = "ftis", ['d'] = "ftdir", ['f'] = "ftfile", ['r'] = "ftrread", ['w'] = "ftrwrite", ['x'] = "ftrexec", ['s'] = "ftsize",
    ['z'] = "ftzero", ['l'] = "ftlink", ['p'] = "ftpipe", ['S'] = "ftsock", ['b'] = "ftblk", ['c'] = "ftchr", ['u'] = "ftsuid",
    ['g'] = "ftsgid", ['k'] = "ftsvtx", ['t'] = "fttty", ['T'] = "fttext", ['B'] = "ftbinary", ['M'] = "ftmtime", ['A'] = "ftatime",
    ['C'] = "ftctime", ['o'] = "fteowned", ['O'] = "ftrowned", ['R'] = "fteread", ['W'] = "ftewrite", ['X'] = "fteexec"
  };

  /// <summary>The shape of an expression.</summary>
  public static string Of(PerlExpr expression) {
    switch (expression) {
      case PerlVariable v:
        return v.Sigil + v.Name;
      case PerlNumber n:
        return n.Value.ToString();
      case PerlString s:
        return "'" + s.Value + "'";
      case PerlList { Items: [var only] }:
        return Of(only);
      case PerlList list:
        return Node("list", list.Items);
      case PerlBinary { Operator: "!~", Right: PerlPattern } negated:
        return $"(not {Of(((PerlPattern)negated.Right) with { Target = negated.Left })})";
      case PerlComparisonChain chain:
        return "(cmpchain_and " + string.Join(" ", chain.Operators.Select((op, i) => "(" + _binary[op] + (i == 0 ? " " + Of(chain.Operands[0]) : "") + " "
          + (i + 1 < chain.Operators.Count ? "(cmpchain_dup " + Of(chain.Operands[i + 1]) + ")" : Of(chain.Operands[i + 1])) + ")")) + ")";
      case PerlBinary b when _binary.TryGetValue(b.Operator, out var name):
        return Node(name, [b.Left, b.Right]);
      case PerlBinary b:
        return Node(b.Operator, [b.Left, b.Right]);
      case PerlUnary { Operator: "+", Operand: { } operand }:
        return Of(operand);
      case PerlUnary { Operator: "!" or "not" } u:
        return u.Operand is null ? "not" : Node("not", [u.Operand]);
      case PerlUnary { Operator: "-" } u:
        return Node("negate", [u.Operand!]);
      case PerlUnary { Operator: "~" } u:
        return Node("complement", [u.Operand!]);
      case PerlUnary { Operator: "\\" } u:
        return Node("srefgen", [u.Operand!]);
      case PerlUnary u when u.Operator.Length == 2 && u.Operator[0] == '-' && _fileTests.TryGetValue(u.Operator[1], out var test):
        return u.Operand is null or PerlString { Value: "_", Bareword: true } ? test : Node(test, [u.Operand]);
      case PerlStep step:
        return Node((step.Prefix ? "pre" : "post") + (step.Increment ? "inc" : "dec"), [step.Target]);
      case PerlTernary t:
        return Node("cond_expr", [t.Condition, t.Then, t.Else]);
      case PerlAssign { Operator: "=" } a:
        return Node("sassign", [a.Value, a.Target]);
      case PerlAssign a when _binary.TryGetValue(a.Operator[..^1], out var compound):
        return Node(compound, [a.Target, a.Value]);
      case PerlPattern { Negated: true } negatedPattern:
        return $"(not {Of(negatedPattern with { Negated = false })})";
      case PerlPattern { Kind: "m" } match:
        return match.Target is null ? "match" : Node("match", [match.Target]);
      case PerlPattern { Kind: "s" } subst:
        return Node("subst", new[] { subst.Target, subst.Replacement as PerlString }.OfType<PerlExpr>());
      case PerlCall { Builtin: true, Name: "scalar", Arguments: [var argument] }:
        return Of(argument);
      case PerlCall { Builtin: true, Name: "split" } split:
        return Node("split", [.. (split.Arguments ?? []).Skip(1).Take(1), (split.Arguments ?? []).Count > 2 ? split.Arguments![2] : new PerlNumber(PerlNumeric.Of(0L))]);
      case PerlCall call:
        return Node(call.Name, call.Arguments ?? []);
      case PerlElement element:
        return Node(element.Hash ? "helem" : "aelem", [element.Aggregate, element.Index]);
      case PerlSlice slice:
        return Node((slice.KeyValue ? "kv" : "") + (slice.Hash ? "hslice" : "aslice"), [slice.Aggregate, slice.Indexes]);
      case PerlDeref deref:
        return Node(deref.Sigil switch { '$' => "rv2sv", '@' => "rv2av", '%' => "rv2hv", '&' => "rv2cv", _ => "rv2gv" }, [deref.Reference]);
      case PerlLastIndex last:
        return Node("av2arylen", [last.Array]);
      case PerlInterpolation interpolation:
        return Node("interp", interpolation.Parts);
      case PerlWords words:
        return "(qw" + string.Concat(words.Words.Select(w => " '" + w + "'")) + ")";
      case PerlAnonArray array:
        return Node("anonlist", array.Items);
      case PerlAnonHash hash:
        return Node("anonhash", hash.Items);
      case PerlAnonSub:
        return "anoncode";
      case PerlDeclaration declaration:
        return Node(declaration.Kind.ToString().ToLowerInvariant(), declaration.Variables);
      case PerlLocal local:
        return Node("local", [local.Target]);
      case PerlCallReference reference:
        return Node("call", [reference.Code, .. reference.Arguments ?? []]);
      case PerlMethodCall method:
        return Node("method " + (method.Name ?? "$"), [method.Invocant, .. method.DynamicName is null ? [] : new[] { method.DynamicName }, .. method.Arguments]);
      case PerlDoBlock:
        return "do";
      case PerlEvalBlock:
        return "eval";
      case PerlReadLine readLine:
        return (readLine.Glob ? "(glob '" : "(readline '") + readLine.Handle + "')";
      case PerlCommand command:
        return Node("backtick", [command.Command]);
      case PerlJump jump:
        return jump.Label is not null ? $"({jump.Keyword} {jump.Label})" : jump.Value is null ? jump.Keyword : Node(jump.Keyword, [jump.Value]);
      case PerlPattern pattern:
        return pattern.Target is null ? pattern.Kind : Node(pattern.Kind, [pattern.Target]);
      default:
        return Node("unary " + ((PerlUnary)expression).Operator, ((PerlUnary)expression).Operand is { } other ? [other] : []);
    }
  }

  private static string Node(string name, IEnumerable<PerlExpr> operands) {
    var parts = operands.Select(Of).ToList();
    return parts.Count == 0 ? name : "(" + name + " " + string.Join(" ", parts) + ")";
  }

}
