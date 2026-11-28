using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Go.Syntax;

/// <summary>The lexical tokens of Go (The Go Programming Language Specification, "Lexical elements"), in the order of <c>go/token</c>.</summary>
internal enum GoTokenKind {
  Illegal,
  Eof,
  Comment,

  Ident,
  Int,
  Float,
  Imag,
  Char,
  String,

  Add,
  Sub,
  Mul,
  Quo,
  Rem,
  And,
  Or,
  Xor,
  Shl,
  Shr,
  AndNot,
  AddAssign,
  SubAssign,
  MulAssign,
  QuoAssign,
  RemAssign,
  AndAssign,
  OrAssign,
  XorAssign,
  ShlAssign,
  ShrAssign,
  AndNotAssign,
  LAnd,
  LOr,
  Arrow,
  Inc,
  Dec,
  Eql,
  Lss,
  Gtr,
  Assign,
  Not,
  Neq,
  Leq,
  Geq,
  Define,
  Ellipsis,
  LParen,
  LBrack,
  LBrace,
  Comma,
  Period,
  RParen,
  RBrack,
  RBrace,
  Semicolon,
  Colon,
  Tilde,

  Break,
  Case,
  Chan,
  Const,
  Continue,
  Default,
  Defer,
  Else,
  Fallthrough,
  For,
  Func,
  Go,
  Goto,
  If,
  Import,
  Interface,
  Map,
  Package,
  Range,
  Return,
  Select,
  Struct,
  Switch,
  Type,
  Var
}

/// <summary>The spelling, precedence and classes of the tokens, as <c>go/token</c> gives them.</summary>
internal static class GoTokens {

  private static readonly string[] _spelling = [
    "ILLEGAL", "EOF", "COMMENT", "IDENT", "INT", "FLOAT", "IMAG", "CHAR", "STRING",
    "+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>", "&^",
    "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "&^=",
    "&&", "||", "<-", "++", "--", "==", "<", ">", "=", "!", "!=", "<=", ">=", ":=", "...",
    "(", "[", "{", ",", ".", ")", "]", "}", ";", ":", "~",
    "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto",
    "if", "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var"
  ];

  private static readonly Dictionary<string, GoTokenKind> _keywords = Keywords();

  private static Dictionary<string, GoTokenKind> Keywords() {
    var keywords = new Dictionary<string, GoTokenKind>(System.StringComparer.Ordinal);
    for (var kind = GoTokenKind.Break; kind <= GoTokenKind.Var; ++kind)
      keywords[_spelling[(int)kind]] = kind;

    return keywords;
  }

  /// <summary>How a token is written: an operator or keyword as it is spelled, any other by its class.</summary>
  public static string Spelling(this GoTokenKind kind) => _spelling[(int)kind];

  /// <summary>The keyword an identifier spells, or <see cref="GoTokenKind.Ident"/>.</summary>
  public static GoTokenKind Lookup(string identifier) => _keywords.TryGetValue(identifier, out var keyword) ? keyword : GoTokenKind.Ident;

  /// <summary>Whether a token is a literal: an identifier or a basic literal.</summary>
  public static bool IsLiteral(this GoTokenKind kind) => kind is >= GoTokenKind.Ident and <= GoTokenKind.String;

  /// <summary>The precedence of a binary operator, 1 to 5; 0 for any other token.</summary>
  public static int Precedence(this GoTokenKind kind) => kind switch {
    GoTokenKind.LOr => 1,
    GoTokenKind.LAnd => 2,
    GoTokenKind.Eql or GoTokenKind.Neq or GoTokenKind.Lss or GoTokenKind.Leq or GoTokenKind.Gtr or GoTokenKind.Geq => 3,
    GoTokenKind.Add or GoTokenKind.Sub or GoTokenKind.Or or GoTokenKind.Xor => 4,
    GoTokenKind.Mul or GoTokenKind.Quo or GoTokenKind.Rem or GoTokenKind.Shl or GoTokenKind.Shr or GoTokenKind.And or GoTokenKind.AndNot => 5,
    _ => 0
  };

}

/// <summary>A token: its kind, the byte offset it starts at and its text - the literal of a literal, <c>"\n"</c> or <c>";"</c> of a semicolon.</summary>
internal readonly record struct GoToken(GoTokenKind Kind, int Offset, string Literal);
