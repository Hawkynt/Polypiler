using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Perl.Ast;

/// <summary>A node of the syntax tree of a Perl file, at the line it starts on.</summary>
internal abstract record PerlNode {

  /// <summary>The 1-based line the node starts on.</summary>
  public int Line { get; init; }

}

/// <summary>How a lexical variable is declared.</summary>
internal enum PerlDeclarator {
  My, Our, State
}

/// <summary>
/// A compilation unit with a pad of its own: the file's main program, a named sub or an anonymous
/// sub. Its lexicals are listed in the order perl introduces them; its anonymous subs in the order
/// they appear, so the outline mirrors perl's pads.
/// </summary>
internal sealed class PerlUnit(string name, PerlUnit? outer) {

  /// <summary>The qualified name of a named sub, <c>main</c> for the program, empty for an anonymous sub.</summary>
  public string Name { get; } = name;

  /// <summary>The unit lexically enclosing this one; <see langword="null"/> for the program.</summary>
  public PerlUnit? Outer { get; } = outer;

  /// <summary>The lexicals declared in the unit, in order of declaration.</summary>
  public List<PerlLexical> Lexicals { get; } = [];

  /// <summary>The entries of the pad in order: a lexical, or a nested anonymous sub.</summary>
  public List<object> Pad { get; } = [];

}

/// <summary>A lexical declaration made by <c>my</c>, <c>our</c> or <c>state</c>.</summary>
internal sealed class PerlLexical(char sigil, string name, PerlDeclarator kind, PerlUnit unit, string package) {

  /// <summary>The sigil: <c>$</c>, <c>@</c>, <c>%</c> or <c>&amp;</c> (a lexical sub).</summary>
  public char Sigil { get; } = sigil;

  /// <summary>The name without the sigil.</summary>
  public string Name { get; } = name;

  /// <summary>The declarator.</summary>
  public PerlDeclarator Kind { get; } = kind;

  /// <summary>The unit whose pad holds the lexical.</summary>
  public PerlUnit Unit { get; } = unit;

  /// <summary>The package current at the declaration, which an <c>our</c> variable belongs to.</summary>
  public string Package { get; } = package;

  /// <summary>The name with its sigil, as perl's pad names it.</summary>
  public string PadName => this.Sigil + this.Name;

}

/// <summary>An expression.</summary>
internal abstract record PerlExpr : PerlNode;

/// <summary>A numeric literal or a folded constant: an integer (signed or unsigned 64-bit) or a binary64.</summary>
internal sealed record PerlNumber(PerlNumeric Value) : PerlExpr;

/// <summary>A constant string: a single-quoted string, an interpolating one without variables, a quoted bareword.</summary>
internal sealed record PerlString(string Value) : PerlExpr {

  /// <summary>Whether the string came from a bareword (autoquoted or a class name).</summary>
  public bool Bareword { get; init; }

}

/// <summary>An interpolating string: constant parts (<see cref="PerlString"/>) and interpolated expressions, concatenated.</summary>
internal sealed record PerlInterpolation(IReadOnlyList<PerlExpr> Parts) : PerlExpr;

/// <summary>A list of words, <c>qw(...)</c>.</summary>
internal sealed record PerlWords(IReadOnlyList<string> Words) : PerlExpr;

/// <summary>
/// A variable: <c>$x</c>, <c>@x</c>, <c>%x</c>, <c>&amp;x</c>, <c>*x</c>. The name may be qualified
/// (<c>Foo::x</c>) or special (<c>_</c>, <c>0</c>, <c>^W</c>, <c>@</c>). A lexical names its declaration.
/// </summary>
internal sealed record PerlVariable(char Sigil, string Name, PerlLexical? Lexical) : PerlExpr;

/// <summary>An element <c>$a[i]</c> or <c>$h{k}</c> of the aggregate <see cref="Aggregate"/> (a variable or a dereference).</summary>
internal sealed record PerlElement(PerlExpr Aggregate, PerlExpr Index, bool Hash) : PerlExpr;

/// <summary>A slice <c>@a[...]</c>, <c>@h{...}</c>, <c>%h{...}</c> or <c>%a[...]</c>.</summary>
internal sealed record PerlSlice(PerlExpr Aggregate, PerlExpr Indexes, bool Hash, bool KeyValue) : PerlExpr;

/// <summary>A dereference <c>@$r</c>, <c>%{...}</c>, <c>$$r</c>, <c>&amp;$f</c>, <c>*$g</c>, or <c>$r-&gt;@*</c>.</summary>
internal sealed record PerlDeref(char Sigil, PerlExpr Reference) : PerlExpr;

/// <summary>The last index of an array, <c>$#a</c> or <c>$#{...}</c>.</summary>
internal sealed record PerlLastIndex(PerlExpr Array) : PerlExpr;

/// <summary>A declaration <c>my $x</c>, <c>our ($a, @b)</c>, <c>state $n</c>.</summary>
internal sealed record PerlDeclaration(PerlDeclarator Kind, IReadOnlyList<PerlVariable> Variables, bool Parenthesized) : PerlExpr;

/// <summary><c>local EXPR</c>.</summary>
internal sealed record PerlLocal(PerlExpr Target) : PerlExpr;

/// <summary>A prefix operator: <c>!</c>, <c>-</c>, <c>+</c>, <c>~</c>, <c>~.</c>, <c>\</c>, <c>not</c>, or a file test <c>-e</c>.</summary>
internal sealed record PerlUnary(string Operator, PerlExpr? Operand) : PerlExpr;

/// <summary><c>++</c> or <c>--</c>, prefix or postfix.</summary>
internal sealed record PerlStep(bool Increment, bool Prefix, PerlExpr Target) : PerlExpr;

/// <summary>A binary operator, including the logical, range, binding and comparison operators.</summary>
internal sealed record PerlBinary(string Operator, PerlExpr Left, PerlExpr Right) : PerlExpr;

/// <summary>A chain of comparisons of one precedence level, <c>$a &lt; $b &lt;= $c</c>, which perl evaluates as the comparisons of neighbours joined by <c>and</c>, each operand once.</summary>
internal sealed record PerlComparisonChain(IReadOnlyList<string> Operators, IReadOnlyList<PerlExpr> Operands) : PerlExpr;

/// <summary>An assignment, <c>=</c> or a compound one such as <c>+=</c>.</summary>
internal sealed record PerlAssign(string Operator, PerlExpr Target, PerlExpr Value) : PerlExpr;

/// <summary><c>c ? a : b</c>.</summary>
internal sealed record PerlTernary(PerlExpr Condition, PerlExpr Then, PerlExpr Else) : PerlExpr;

/// <summary>A parenthesized list or a comma expression; <c>()</c> is the empty one.</summary>
internal sealed record PerlList(IReadOnlyList<PerlExpr> Items) : PerlExpr {

  /// <summary>Whether parentheses enclose the list.</summary>
  public bool Parenthesized { get; init; }

}

/// <summary>An anonymous array <c>[...]</c>.</summary>
internal sealed record PerlAnonArray(IReadOnlyList<PerlExpr> Items) : PerlExpr;

/// <summary>An anonymous hash <c>{...}</c>.</summary>
internal sealed record PerlAnonHash(IReadOnlyList<PerlExpr> Items) : PerlExpr;

/// <summary>An anonymous sub <c>sub { ... }</c>.</summary>
internal sealed record PerlAnonSub(PerlSub Sub) : PerlExpr;

/// <summary>
/// A call of a named sub or a builtin. <see cref="Arguments"/> is <see langword="null"/> for
/// <c>&amp;foo;</c>, which passes the caller's <c>@_</c>. A block argument (<c>map</c>, <c>grep</c>,
/// <c>sort</c>, an <c>&amp;</c> prototype) is <see cref="Block"/>; a file handle (<c>print FH</c>) is <see cref="Handle"/>.
/// </summary>
internal sealed record PerlCall(string Name, IReadOnlyList<PerlExpr>? Arguments) : PerlExpr {

  /// <summary>Whether the arguments are in parentheses.</summary>
  public bool Parenthesized { get; init; }

  /// <summary>Whether the call is spelled with <c>&amp;</c>.</summary>
  public bool Ampersand { get; init; }

  /// <summary>Whether the name is a builtin of perl (rather than a sub).</summary>
  public bool Builtin { get; init; }

  /// <summary>The block argument, if any.</summary>
  public PerlBlock? Block { get; init; }

  /// <summary>The file handle or the sort sub, if any.</summary>
  public PerlExpr? Handle { get; init; }

}

/// <summary>A call through a code reference: <c>$f-&gt;(...)</c>, <c>&amp;$f(...)</c>, <c>&amp;{...}(...)</c>.</summary>
internal sealed record PerlCallReference(PerlExpr Code, IReadOnlyList<PerlExpr>? Arguments) : PerlExpr;

/// <summary>A method call <c>$o-&gt;name(...)</c>, <c>Class-&gt;name</c>, <c>$o-&gt;$m(...)</c>; also the indirect <c>new Class(...)</c>.</summary>
internal sealed record PerlMethodCall(PerlExpr Invocant, string? Name, PerlExpr? DynamicName, IReadOnlyList<PerlExpr> Arguments) : PerlExpr {

  /// <summary>Whether the arguments are in parentheses.</summary>
  public bool Parenthesized { get; init; }

}

/// <summary>A pattern match <c>m/.../</c>, <c>qr/.../</c>, a substitution <c>s/.../.../</c> or a transliteration <c>tr/.../.../</c>.</summary>
internal sealed record PerlPattern(string Kind, PerlExpr Pattern, PerlExpr? Replacement, string Flags) : PerlExpr {

  /// <summary>The target bound with <c>=~</c> or <c>!~</c>; <see langword="null"/> for <c>$_</c>.</summary>
  public PerlExpr? Target { get; init; }

  /// <summary>Whether the binding is <c>!~</c>.</summary>
  public bool Negated { get; init; }

}

/// <summary><c>&lt;FH&gt;</c>, <c>&lt;$fh&gt;</c>, <c>&lt;&gt;</c>, <c>&lt;&lt;&gt;&gt;</c> or a glob <c>&lt;*.c&gt;</c>.</summary>
internal sealed record PerlReadLine(string Handle, bool Glob) : PerlExpr;

/// <summary>A command <c>`...`</c> or <c>qx(...)</c>.</summary>
internal sealed record PerlCommand(PerlExpr Command) : PerlExpr;

/// <summary><c>do BLOCK</c>.</summary>
internal sealed record PerlDoBlock(PerlBlock Body) : PerlExpr;

/// <summary><c>eval BLOCK</c>.</summary>
internal sealed record PerlEvalBlock(PerlBlock Body) : PerlExpr;

/// <summary><c>return LIST</c>, <c>last LABEL</c>, <c>next</c>, <c>redo</c>, <c>goto</c>, <c>dump</c>.</summary>
internal sealed record PerlJump(string Keyword, string? Label, PerlExpr? Value) : PerlExpr;

/// <summary>A statement.</summary>
internal abstract record PerlStatement : PerlNode {

  /// <summary>The label of the statement, if any.</summary>
  public string? Label { get; init; }

  /// <summary>The package current at the statement.</summary>
  public string Package { get; init; } = "main";

  /// <summary>Whether <c>use integer</c> is in effect for the statement.</summary>
  public bool Integer { get; init; }

}

/// <summary>An expression statement, optionally with a modifier (<c>EXPR if COND</c>).</summary>
internal sealed record PerlExpressionStatement(PerlExpr Expression) : PerlStatement {

  /// <summary>The modifier keyword: <c>if</c>, <c>unless</c>, <c>while</c>, <c>until</c>, <c>for</c>, <c>foreach</c>; <see langword="null"/> without one.</summary>
  public string? Modifier { get; init; }

  /// <summary>The condition or list of the modifier.</summary>
  public PerlExpr? ModifierExpression { get; init; }

}

/// <summary>A block <c>{ ... }</c>.</summary>
internal sealed record PerlBlock(IReadOnlyList<PerlStatement> Statements) : PerlNode;

/// <summary><c>if (C) {...} elsif (C) {...} else {...}</c> or <c>unless ...</c>.</summary>
internal sealed record PerlIf(IReadOnlyList<(PerlExpr Condition, PerlBlock Body)> Clauses, PerlBlock? Else, bool Unless) : PerlStatement;

/// <summary><c>while (C) {...} continue {...}</c> or <c>until</c>; a missing condition loops forever.</summary>
internal sealed record PerlWhile(PerlExpr? Condition, PerlBlock Body, PerlBlock? Continue, bool Until) : PerlStatement;

/// <summary>A C-style <c>for (init; cond; step) {...}</c>.</summary>
internal sealed record PerlCFor(PerlExpr? Init, PerlExpr? Condition, PerlExpr? Step, PerlBlock Body) : PerlStatement;

/// <summary><c>foreach my $x (LIST) {...}</c>; without a variable the loop aliases <c>$_</c>.</summary>
internal sealed record PerlForeach(IReadOnlyList<PerlVariable> Variables, PerlDeclarator? Declarator, PerlExpr List, PerlBlock Body, PerlBlock? Continue) : PerlStatement;

/// <summary>A bare block, which is a loop that runs once.</summary>
internal sealed record PerlBareBlock(PerlBlock Body, PerlBlock? Continue) : PerlStatement;

/// <summary>A named sub declaration or definition.</summary>
internal sealed record PerlSubDeclaration(string Name, PerlSub Sub) : PerlStatement;

/// <summary><c>package NAME VERSION;</c> or <c>package NAME VERSION BLOCK</c>.</summary>
internal sealed record PerlPackage(string Name, string? Version, PerlBlock? Body) : PerlStatement;

/// <summary><c>use</c>/<c>no</c> with a module or a version, a version and the import list.</summary>
internal sealed record PerlUse(bool No, string? Module, string? Version, PerlExpr? Imports) : PerlStatement {

  /// <summary>Whether the import list is the empty <c>()</c>, which calls no import.</summary>
  public bool EmptyImports { get; init; }

}

/// <summary>A <c>BEGIN</c>, <c>END</c>, <c>INIT</c>, <c>CHECK</c> or <c>UNITCHECK</c> block.</summary>
internal sealed record PerlPhaseBlock(string Phase, PerlBlock Body) : PerlStatement;

/// <summary><c>try {...} catch ($e) {...} finally {...}</c>.</summary>
internal sealed record PerlTry(PerlBlock Body, PerlVariable? CatchVariable, PerlBlock? Catch, PerlBlock? Finally) : PerlStatement;

/// <summary><c>defer {...}</c>.</summary>
internal sealed record PerlDefer(PerlBlock Body) : PerlStatement;

/// <summary>An empty statement (<c>;</c>).</summary>
internal sealed record PerlEmpty : PerlStatement;

/// <summary>A sub: its prototype or signature, attributes, body and pad.</summary>
internal sealed record PerlSub(string? Prototype, IReadOnlyList<PerlParameter>? Signature, IReadOnlyList<string> Attributes, PerlBlock? Body, PerlUnit Unit) : PerlNode;

/// <summary>A parameter of a signature: a lexical, an optional default (<c>$x = 1</c>, <c>//=</c>, <c>||=</c>); a slurpy <c>@</c>/<c>%</c>.</summary>
internal sealed record PerlParameter(PerlVariable? Variable, char Sigil, string? DefaultOperator, PerlExpr? Default) : PerlNode;

/// <summary>A parsed file: its statements and the program unit.</summary>
internal sealed record PerlDocument(IReadOnlyList<PerlStatement> Statements, PerlUnit Program) : PerlNode {

  /// <summary>Every named sub defined with a body, by qualified name, in order of definition.</summary>
  public IReadOnlyList<PerlSubDeclaration> Subs { get; init; } = [];

}
