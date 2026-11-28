"""Spike generator: a PEG grammar (go.pgram dialect) to a C# packrat parser over a token array.

usage: pgen.py <grammar> <out.cs>
"""
import re
import sys

SPELLING = ["ILLEGAL", "EOF", "COMMENT", "IDENT", "INT", "FLOAT", "IMAG", "CHAR", "STRING",
            "+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>", "&^",
            "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "&^=",
            "&&", "||", "<-", "++", "--", "==", "<", ">", "=", "!", "!=", "<=", ">=", ":=", "...",
            "(", "[", "{", ",", ".", ")", "]", "}", ";", ":", "~",
            "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto",
            "if", "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var"]
KINDS = ["Illegal", "Eof", "Comment", "Ident", "Int", "Float", "Imag", "Char", "String",
         "Add", "Sub", "Mul", "Quo", "Rem", "And", "Or", "Xor", "Shl", "Shr", "AndNot",
         "AddAssign", "SubAssign", "MulAssign", "QuoAssign", "RemAssign", "AndAssign", "OrAssign", "XorAssign", "ShlAssign", "ShrAssign", "AndNotAssign",
         "LAnd", "LOr", "Arrow", "Inc", "Dec", "Eql", "Lss", "Gtr", "Assign", "Not", "Neq", "Leq", "Geq", "Define", "Ellipsis",
         "LParen", "LBrack", "LBrace", "Comma", "Period", "RParen", "RBrack", "RBrace", "Semicolon", "Colon", "Tilde",
         "Break", "Case", "Chan", "Const", "Continue", "Default", "Defer", "Else", "Fallthrough", "For", "Func", "Go", "Goto",
         "If", "Import", "Interface", "Map", "Package", "Range", "Return", "Select", "Struct", "Switch", "Type", "Var"]
LIT = {s: k for s, k in zip(SPELLING, KINDS) if not s.isupper()}
TOK = {s: k for s, k in zip(SPELLING, KINDS) if s.isupper()}

TOKEN_RE = re.compile(r"\s*(?:('(?:[^'\\]|\\.)*')|(\?[A-Za-z_]\w*)|(nolit\(|lit\()|([A-Za-z_]\w*)|([()|?*+&!]))")


def tokenize(text):
    pos, out = 0, []
    text = text.rstrip()
    while pos < len(text):
        m = TOKEN_RE.match(text, pos)
        if not m:
            raise SyntaxError(f"at {text[pos:pos + 20]!r}")
        pos = m.end()
        lit, pred, hook, name, op = m.groups()
        if lit: out.append(("lit", lit[1:-1]))
        elif pred: out.append(("pred", pred[1:]))
        elif hook: out.append(("hook", hook[:-1]))
        elif name: out.append(("name", name))
        else: out.append(("op", op))
    return out


class P:
    def __init__(self, toks): self.t, self.i = toks, 0
    def peek(self): return self.t[self.i] if self.i < len(self.t) else (None, None)
    def take(self): self.i += 1; return self.t[self.i - 1]

    def choice(self):
        alts = [self.seq()]
        while self.peek() == ("op", "|"):
            self.take(); alts.append(self.seq())
        return ("choice", alts) if len(alts) > 1 else alts[0]

    def seq(self):
        items = []
        while self.peek()[0] is not None and self.peek() not in (("op", "|"), ("op", ")")):
            items.append(self.postfix())
        return ("seq", items) if len(items) != 1 else items[0]

    def postfix(self):
        e = self.prefix()
        while self.peek() in (("op", "?"), ("op", "*"), ("op", "+")):
            e = ({"?": "opt", "*": "star", "+": "plus"}[self.take()[1]], e)
        return e

    def prefix(self):
        k, v = self.peek()
        if (k, v) in (("op", "&"), ("op", "!")):
            self.take()
            return ("and" if v == "&" else "not", self.prefix())
        return self.atom()

    def atom(self):
        k, v = self.take()
        if k == "lit": return ("tok", LIT[v])
        if k == "pred": return ("pred", v)
        if k == "hook":
            e = self.choice(); assert self.take() == ("op", ")"); return (v, e)
        if k == "name": return ("tok", TOK[v]) if v in TOK else ("rule", v)
        if (k, v) == ("op", "("):
            e = self.choice(); assert self.take() == ("op", ")"); return e
        raise SyntaxError((k, v))


def main(src, dst):
    rules = []
    for line in open(src, encoding="utf-8"):
        line = line.split("#", 1)[0].rstrip()
        if not line.strip(): continue
        memo = line.startswith("(memo)")
        if memo: line = line[6:].strip()
        name, body = line.split(":", 1)
        rules.append((name.strip(), memo, P(tokenize(body)).choice()))
    ids = {n: i for i, (n, _, _) in enumerate(rules)}
    out, helpers, counter = [], [], [0]

    def fresh():
        counter[0] += 1; return f"G{counter[0]}"

    def method(body_lines):
        n = fresh()
        helpers.append(f"  private bool {n}() {{\n" + "\n".join("    " + l for l in body_lines) + "\n  }\n")
        return f"this.{n}()"

    def gen(e):
        kind = e[0]
        if kind == "tok": return f"this.Is(GoTokenKind.{e[1]})"
        if kind == "rule": assert e[1] in ids, e[1]; return f"(this.R{ids[e[1]]}_{e[1]}() is not null)"
        if kind == "pred": return f"this.{e[1]}()"
        if kind == "opt": return f"({gen(e[1])} || true)"
        if kind == "star":
            return method([f"while (true) {{ var m = this._p; if (!{gen(e[1])} || this._p == m) break; }}", "return true;"])
        if kind == "plus":
            inner = gen(e[1])
            return method([f"if (!{inner}) return false;", f"while (true) {{ var m = this._p; if (!{inner} || this._p == m) break; }}", "return true;"])
        if kind in ("and", "not"):
            neg = "!" if kind == "not" else ""
            return method(["var m = this._p; var s = this._stack.Count;", f"var r = {gen(e[1])};", "this._p = m; this.Truncate(s);", f"return {neg}r;"])
        if kind in ("nolit", "lit"):
            val = "true" if kind == "nolit" else "false"
            return method(["var save = this._noLit;", f"this._noLit = {val};", f"var r = {gen(e[1])};", "this._noLit = save;", "return r;"])
        if kind == "choice":
            return "(" + " || ".join(gen(a) for a in e[1]) + ")"
        if kind == "seq":
            lines = ["var m = this._p; var s = this._stack.Count;"]
            for it in e[1]:
                lines.append(f"if (!{gen(it)}) {{ this._p = m; this.Truncate(s); return false; }}")
            lines.append("return true;")
            return method(lines)
        raise ValueError(e)

    for name, memo, body in rules:
        i = ids[name]
        expr = gen(body)
        lines = [f"  private GoPegNode? R{i}_{name}() {{", "    var start = this._p; var s = this._stack.Count;"]
        if memo:
            lines += [f"    var memo = this._noLit ? (this._memo{i}b ??= new Memo[this._n]) : (this._memo{i}a ??= new Memo[this._n]);",
                      "    var hit = memo[start];",
                      "    if (hit.End > 0) { this._p = hit.End - 1; if (hit.Node is { } k) this._stack.Add(k); return hit.Node; }",
                      "    if (hit.End < 0) return null;"]
        lines += [f"    if ({expr}) {{",
                  f"      var node = new GoPegNode({i}, start, this._p, this.Slice(s));",
                  "      this._stack.Add(node);"]
        if memo: lines.append("      memo[start] = new(this._p + 1, node);")
        lines += ["      return node;", "    }", "    this._p = start; this.Truncate(s);"]
        if memo: lines.append("    memo[start] = new(-1, null);")
        lines += ["    return null;", "  }", ""]
        out.append("\n".join(lines))

    memos = [(ids[n]) for n, m, _ in rules if m]
    fields = "\n".join(f"  private Memo[]? _memo{i}a, _memo{i}b;" for i in memos) + "\n  private int _n;"
    init = "    this._n = n;"
    names = ", ".join(f'"{n}"' for n, _, _ in rules)
    with open(dst, "w", encoding="utf-8") as f:
        f.write(f"""// <auto-generated> from {src.split('/')[-1]} by pgen.py </auto-generated>
#nullable enable
using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Go.Syntax;

internal sealed partial class GoPegParser {{

  public static readonly string[] RuleNames = [{names}];

  private readonly record struct Memo(int End, GoPegNode? Node);

{fields}

  private void InitMemo(int n) {{
{init}
  }}

  public GoPegNode? Parse() => this.R0_{rules[0][0]}();

{''.join(out)}
{''.join(helpers)}
}}
""")
    print(f"{len(rules)} rules, {counter[0]} helper methods, {len(memos)} memoized")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
