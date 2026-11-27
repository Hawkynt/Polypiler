using System.Collections.Generic;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Go.Syntax;

// The syntax tree of the Go a package is written as, shaped after the nodes of the standard library's
// go/ast for the subset the backend writes. The emitter builds it from the IR, the target passes
// rewrite it and GoPrinter is the only place that turns it into text, in the layout gofmt gives it.
// As in the Python tree there is no node for parentheses: the printer puts them exactly where Go's
// precedence needs them, so a tree has one text. The properties that say how a node is laid out - a
// blank line or comment in front of a statement, the elements of a table per line - change no meaning
// and are ignored where two trees are compared.

/// <summary>A node of the Go syntax tree.</summary>
public abstract record GoNode;

/// <summary>What stands in front of a statement or a declaration: empty lines and comments, each a line of its own.</summary>
/// <param name="BlankLines">How many empty lines come first: gofmt keeps at most one.</param>
/// <param name="Comments">The text of each comment line, without its <c>//</c>.</param>
public sealed record GoTrivia(int BlankLines, IReadOnlyList<string> Comments) {

  /// <summary>Nothing in front.</summary>
  public static GoTrivia None { get; } = new(0, []);

  /// <summary>One blank line, which gofmt keeps between declarations.</summary>
  public static GoTrivia Blank { get; } = new(1, []);

}

#region Expressions

/// <summary>An expression, or a type, which Go writes where an expression stands.</summary>
public abstract record GoExpr : GoNode {

  /// <summary>The IR type the expression was written for, or <see langword="null"/> where none is known, as in a helper's source.</summary>
  public IrType? Type { get; init; }

}

/// <summary>A name: of a variable, a function, a type, a package or a predeclared identifier.</summary>
/// <param name="Name">The name.</param>
public sealed record GoIdent(string Name) : GoExpr;

/// <summary>An integer literal; never negative, a negative constant is a unary minus in front of one.</summary>
/// <param name="Value">The value.</param>
/// <param name="Hex">Whether it is written in hexadecimal, <c>0x</c> and upper-case digits.</param>
public sealed record GoInt(BigInteger Value, bool Hex = false) : GoExpr;

/// <summary>
/// A floating-point literal as it is spelled, such as <c>0.1</c> or <c>1e+20</c>; never negative.
/// Infinities, NaNs and the negative zero are no literals in Go: the emitter writes them as calls of
/// <c>math</c>.
/// </summary>
/// <param name="Text">The spelling.</param>
public sealed record GoFloat(string Text) : GoExpr;

/// <summary>An interpreted string literal; the printer escapes what Go's <c>strconv.Quote</c> escapes.</summary>
/// <param name="Value">The characters.</param>
public sealed record GoString(string Value) : GoExpr;

/// <summary><c>left op right</c> with one of Go's binary operators.</summary>
/// <param name="Left">The operand evaluated first.</param>
/// <param name="Op">The operator, such as <c>+</c>, <c>&amp;^</c> or <c>&lt;&lt;</c>.</param>
/// <param name="Right">The operand evaluated second.</param>
public sealed record GoBinary(GoExpr Left, string Op, GoExpr Right) : GoExpr;

/// <summary><c>op operand</c> with one of Go's unary operators <c>-</c>, <c>+</c>, <c>^</c>, <c>!</c> or <c>&amp;</c>.</summary>
/// <param name="Op">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record GoUnary(string Op, GoExpr Operand) : GoExpr;

/// <summary><c>*operand</c>: the value a pointer points to, or a pointer type.</summary>
/// <param name="Operand">The pointer, or the type pointed to.</param>
public sealed record GoStar(GoExpr Operand) : GoExpr;

/// <summary><c>callee(arguments)</c>: a call, or a conversion when the callee is a type.</summary>
/// <param name="Callee">What is called.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record GoCall(GoExpr Callee, IReadOnlyList<GoExpr> Arguments) : GoExpr {

  /// <summary>Whether the last argument is spread over a variadic parameter, <c>f(xs...)</c>.</summary>
  public bool Spread { get; init; }

}

/// <summary><c>target.name</c>: a member of a package, a field or a method.</summary>
/// <param name="Target">The package, value or type.</param>
/// <param name="Name">The member.</param>
public sealed record GoSelector(GoExpr Target, string Name) : GoExpr;

/// <summary><c>target[index]</c>.</summary>
/// <param name="Target">The slice, array, map or generic function.</param>
/// <param name="Index">The index.</param>
public sealed record GoIndex(GoExpr Target, GoExpr Index) : GoExpr;

/// <summary><c>target[low:high]</c> or <c>target[low:high:max]</c>; either bound may be left out.</summary>
/// <param name="Target">The slice or array.</param>
/// <param name="Low">The first index, or <see langword="null"/>.</param>
/// <param name="High">The index after the last, or <see langword="null"/>.</param>
/// <param name="Max">The capacity limit of a full slice expression, or <see langword="null"/>.</param>
public sealed record GoSliceExpr(GoExpr Target, GoExpr? Low, GoExpr? High, GoExpr? Max = null) : GoExpr;

/// <summary><c>Type{elements}</c>, a composite literal; the type is left out for an element of an outer one.</summary>
/// <param name="LiteralType">The type, or <see langword="null"/>.</param>
/// <param name="Elements">The elements, each a value or a <see cref="GoKeyValue"/>.</param>
public sealed record GoComposite(GoExpr? LiteralType, IReadOnlyList<GoExpr> Elements) : GoExpr {

  /// <summary>Layout: 0 writes the elements on the line of the braces, more writes that many to a line between them.</summary>
  public int PerLine { get; init; }

}

/// <summary><c>key: value</c>, an element of a composite literal.</summary>
/// <param name="Key">The field name or index.</param>
/// <param name="Value">The value.</param>
public sealed record GoKeyValue(GoExpr Key, GoExpr Value) : GoExpr;

/// <summary><c>func(parameters) results { body }</c>, a function literal.</summary>
/// <param name="Signature">The parameters and results.</param>
/// <param name="Body">The statements.</param>
public sealed record GoFuncLit(GoFuncType Signature, GoBlock Body) : GoExpr;

/// <summary><c>[]Element</c> or <c>[Length]Element</c>.</summary>
/// <param name="Length">The length of an array type, or <see langword="null"/> for a slice type.</param>
/// <param name="Element">The element type.</param>
public sealed record GoArrayType(GoExpr? Length, GoExpr Element) : GoExpr;

/// <summary><c>map[Key]Value</c>.</summary>
/// <param name="Key">The key type.</param>
/// <param name="Value">The value type.</param>
public sealed record GoMapType(GoExpr Key, GoExpr Value) : GoExpr;

/// <summary><c>func(parameters) results</c>, a function type or the signature of a function.</summary>
/// <param name="Parameters">The parameters, consecutive ones of a type grouped under it.</param>
/// <param name="Results">The results.</param>
public sealed record GoFuncType(IReadOnlyList<GoField> Parameters, IReadOnlyList<GoField> Results) : GoExpr;

/// <summary><c>struct { fields }</c>.</summary>
/// <param name="Fields">The fields, in declaration order.</param>
public sealed record GoStructType(IReadOnlyList<GoField> Fields) : GoExpr;

/// <summary>Names of one type: a group of parameters, results or fields; no names for an unnamed parameter or result.</summary>
/// <param name="Names">The names; empty for a type alone.</param>
/// <param name="FieldType">The type.</param>
public sealed record GoField(IReadOnlyList<string> Names, GoExpr FieldType) : GoNode;

#endregion

#region Statements

/// <summary>A statement.</summary>
public abstract record GoStmt : GoNode {

  /// <summary>Layout: the empty lines and comments in front of the statement.</summary>
  public GoTrivia Leading { get; init; } = GoTrivia.None;

}

/// <summary>A sequence of statements in braces.</summary>
/// <param name="Statements">The statements.</param>
public sealed record GoBlock(IReadOnlyList<GoStmt> Statements) : GoNode;

/// <summary>An expression evaluated for its effect: a call.</summary>
/// <param name="Expression">The expression.</param>
public sealed record GoExprStmt(GoExpr Expression) : GoStmt;

/// <summary><c>left op right</c>: an assignment with <c>=</c>, an operator assignment such as <c>+=</c>, or a short variable declaration with <c>:=</c>.</summary>
/// <param name="Left">The places, or the names declared.</param>
/// <param name="Op">The token.</param>
/// <param name="Right">The values.</param>
public sealed record GoAssign(IReadOnlyList<GoExpr> Left, string Op, IReadOnlyList<GoExpr> Right) : GoStmt;

/// <summary><c>target++</c> or <c>target--</c>.</summary>
/// <param name="Target">The place.</param>
/// <param name="Increment">Whether one is added; otherwise subtracted.</param>
public sealed record GoIncDec(GoExpr Target, bool Increment) : GoStmt;

/// <summary><c>names Type = values</c>: the one specification of a <c>var</c> or <c>const</c> declaration.</summary>
/// <param name="Names">The names.</param>
/// <param name="SpecType">The type, or <see langword="null"/> where the values give it.</param>
/// <param name="Values">The values; empty for the zero value.</param>
public sealed record GoValueSpec(IReadOnlyList<string> Names, GoExpr? SpecType, IReadOnlyList<GoExpr> Values) : GoNode;

/// <summary><c>var names Type = values</c> inside a function.</summary>
/// <param name="Spec">The specification.</param>
public sealed record GoVarStmt(GoValueSpec Spec) : GoStmt;

/// <summary><c>return results</c>.</summary>
/// <param name="Results">The values; empty for none.</param>
public sealed record GoReturn(IReadOnlyList<GoExpr> Results) : GoStmt;

/// <summary>A block as a statement of its own, a scope.</summary>
/// <param name="Body">The block.</param>
public sealed record GoBlockStmt(GoBlock Body) : GoStmt;

/// <summary><c>if init; condition { then } else …</c>.</summary>
/// <param name="Init">The simple statement before the condition, or <see langword="null"/>.</param>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The block run when it holds.</param>
/// <param name="Else">A <see cref="GoIf"/> or a <see cref="GoBlockStmt"/>, or <see langword="null"/>.</param>
public sealed record GoIf(GoStmt? Init, GoExpr Condition, GoBlock Then, GoStmt? Else) : GoStmt;

/// <summary><c>for init; condition; post { body }</c>, <c>for condition { body }</c> or <c>for { body }</c>.</summary>
/// <param name="Init">The simple statement before the first test, or <see langword="null"/>.</param>
/// <param name="Condition">The condition, or <see langword="null"/> for none.</param>
/// <param name="Post">The simple statement after every pass, or <see langword="null"/>.</param>
/// <param name="Body">The block of one pass.</param>
public sealed record GoFor(GoStmt? Init, GoExpr? Condition, GoStmt? Post, GoBlock Body) : GoStmt;

/// <summary><c>for key, value := range over { body }</c>, or <c>for range over</c>.</summary>
/// <param name="Key">The key, or <see langword="null"/>.</param>
/// <param name="Value">The value, or <see langword="null"/>.</param>
/// <param name="Define">Whether key and value are declared (<c>:=</c>) rather than assigned.</param>
/// <param name="Over">What is ranged over: an integer, a slice, a string or a function.</param>
/// <param name="Body">The block of one pass.</param>
public sealed record GoRange(GoExpr? Key, GoExpr? Value, bool Define, GoExpr Over, GoBlock Body) : GoStmt;

/// <summary><c>switch init; tag { clauses }</c>; without a tag every clause lists conditions.</summary>
/// <param name="Init">The simple statement first, or <see langword="null"/>.</param>
/// <param name="Tag">The value compared, or <see langword="null"/>.</param>
/// <param name="Clauses">The clauses.</param>
public sealed record GoSwitch(GoStmt? Init, GoExpr? Tag, IReadOnlyList<GoCaseClause> Clauses) : GoStmt;

/// <summary><c>case values:</c> or <c>default:</c> with its statements.</summary>
/// <param name="Values">The values, or <see langword="null"/> for <c>default</c>.</param>
/// <param name="Body">The statements.</param>
public sealed record GoCaseClause(IReadOnlyList<GoExpr>? Values, IReadOnlyList<GoStmt> Body) : GoNode;

/// <summary><c>break</c>, <c>continue</c>, <c>goto</c> or <c>fallthrough</c>, with a label or without.</summary>
/// <param name="Keyword">The keyword.</param>
/// <param name="Label">The label, or <see langword="null"/>.</param>
public sealed record GoBranch(string Keyword, string? Label = null) : GoStmt;

/// <summary><c>label: statement</c>; gofmt writes the label one level to the left.</summary>
/// <param name="Label">The label.</param>
/// <param name="Statement">The statement labelled.</param>
public sealed record GoLabeled(string Label, GoStmt Statement) : GoStmt;

/// <summary><c>defer call</c>.</summary>
/// <param name="Call">The call deferred.</param>
public sealed record GoDefer(GoExpr Call) : GoStmt;

#endregion

#region Declarations

/// <summary>A declaration at the level of the package.</summary>
public abstract record GoDecl : GoNode {

  /// <summary>Layout: the empty lines in front and the doc comment, each line without its <c>//</c>.</summary>
  public GoTrivia Leading { get; init; } = GoTrivia.Blank;

}

/// <summary><c>func (receiver) Name(parameters) results { body }</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Signature">The parameters and results.</param>
/// <param name="Body">The statements.</param>
public sealed record GoFuncDecl(string Name, GoFuncType Signature, GoBlock Body) : GoDecl {

  /// <summary>The receiver of a method, or <see langword="null"/> for a function.</summary>
  public GoField? Receiver { get; init; }

  /// <summary>Whether the function is a helper of the backend or of the library, which O0501 removes when nothing calls it.</summary>
  public bool IsHelper { get; init; }

}

/// <summary><c>const name Type = value</c>.</summary>
/// <param name="Spec">The specification.</param>
public sealed record GoConstDecl(GoValueSpec Spec) : GoDecl;

/// <summary><c>var name Type = value</c> at the level of the package.</summary>
/// <param name="Spec">The specification.</param>
public sealed record GoVarDecl(GoValueSpec Spec) : GoDecl;

/// <summary><c>type Name Type</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Declared">The type it names.</param>
public sealed record GoTypeDecl(string Name, GoExpr Declared) : GoDecl;

/// <summary>A source file: the header comment, the package clause, the imports and the declarations.</summary>
/// <param name="Header">The lines of the comment above the package clause, without <c>//</c>.</param>
/// <param name="Package">The name of the package.</param>
/// <param name="Imports">The import paths, which the printer sorts as gofmt does.</param>
/// <param name="Declarations">The declarations, in order.</param>
public sealed record GoFile(IReadOnlyList<string> Header, string Package, IReadOnlyList<string> Imports, IReadOnlyList<GoDecl> Declarations) : GoNode;

#endregion
