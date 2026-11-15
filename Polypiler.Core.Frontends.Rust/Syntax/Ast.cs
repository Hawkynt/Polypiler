using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Core.Frontends.Rust.Syntax;

// The syntax tree of the imported subset of Rust. Nodes are compared by reference: the type checker
// keeps the type of every expression in a table keyed by the node itself.

/// <summary>A type as the source spells it: a primitive type's name, <c>()</c>, <c>!</c>, or the text of any other type.</summary>
/// <param name="Name">The name or the text.</param>
/// <param name="At">Where the type starts.</param>
internal sealed record TypeRef(string Name, Span At);

/// <summary>A construct the parser recognised and the frontend refuses: the diagnostic to report and what it names.</summary>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Construct">The construct, as the message names it.</param>
/// <param name="At">Where it starts.</param>
internal sealed record Refusal(string Code, string Construct, Span At);

/// <summary>An item of the source file.</summary>
internal abstract class Item(Span at) {

  /// <summary>Where the item starts.</summary>
  public Span At { get; } = at;

}

/// <summary>A free function.</summary>
internal sealed class FunctionItem(Span at, string name, bool isPublic, IReadOnlyList<Parameter> parameters, TypeRef? returnType, Block body) : Item(at) {

  public string Name { get; } = name;

  public bool IsPublic { get; } = isPublic;

  public IReadOnlyList<Parameter> Parameters { get; } = parameters;

  public TypeRef? ReturnType { get; } = returnType;

  public Block Body { get; } = body;

}

/// <summary>A constant, as an item or as a statement.</summary>
internal sealed class ConstItem(Span at, string name, TypeRef type, Expr value) : Item(at) {

  public string Name { get; } = name;

  public TypeRef Type { get; } = type;

  public Expr Value { get; } = value;

}

/// <summary>An item the frontend refuses; the refusal says why.</summary>
internal sealed class RefusedItem(Refusal refusal) : Item(refusal.At) {

  public Refusal Refusal { get; } = refusal;

}

/// <summary>A parameter of a function: a name, whether it may be assigned, and its type.</summary>
internal sealed record Parameter(string Name, bool Mutable, TypeRef Type, Span At);

/// <summary>A pattern of a <c>let</c>, a <c>for</c> or a <c>match</c> arm.</summary>
internal abstract class Pattern(Span at) {

  public Span At { get; } = at;

}

/// <summary><c>x</c> or <c>mut x</c>: binds the value, unless the name is a constant, which it then compares with.</summary>
internal sealed class IdentifierPattern(Span at, string name, bool mutable) : Pattern(at) {

  public string Name { get; } = name;

  public bool Mutable { get; } = mutable;

}

/// <summary><c>_</c>.</summary>
internal sealed class WildcardPattern(Span at) : Pattern(at);

/// <summary>A literal or a path to a constant, compared with the value.</summary>
internal sealed class LiteralPattern(Span at, Expr value) : Pattern(at) {

  public Expr Value { get; } = value;

}

/// <summary><c>a..=b</c>, <c>a..b</c>, <c>a..</c> or <c>..=b</c>.</summary>
internal sealed class RangePattern(Span at, Expr? low, Expr? high, bool inclusive) : Pattern(at) {

  public Expr? Low { get; } = low;

  public Expr? High { get; } = high;

  public bool Inclusive { get; } = inclusive;

}

/// <summary><c>p | q</c>.</summary>
internal sealed class OrPattern(Span at, IReadOnlyList<Pattern> alternatives) : Pattern(at) {

  public IReadOnlyList<Pattern> Alternatives { get; } = alternatives;

}

/// <summary><c>(p, q)</c>.</summary>
internal sealed class TuplePattern(Span at, IReadOnlyList<Pattern> elements) : Pattern(at) {

  public IReadOnlyList<Pattern> Elements { get; } = elements;

}

/// <summary>A pattern the frontend refuses.</summary>
internal sealed class RefusedPattern(Refusal refusal) : Pattern(refusal.At) {

  public Refusal Refusal { get; } = refusal;

}

/// <summary>A statement of a block.</summary>
internal abstract class Statement(Span at) {

  public Span At { get; } = at;

}

/// <summary><c>let pattern: type = init;</c>, each part but the pattern optional.</summary>
internal sealed class LetStatement(Span at, Pattern pattern, TypeRef? type, Expr? initializer) : Statement(at) {

  public Pattern Pattern { get; } = pattern;

  public TypeRef? Type { get; } = type;

  public Expr? Initializer { get; } = initializer;

}

/// <summary>An expression evaluated for its effect.</summary>
internal sealed class ExpressionStatement(Span at, Expr expression) : Statement(at) {

  public Expr Expression { get; } = expression;

}

/// <summary>A constant declared in a block.</summary>
internal sealed class ConstStatement(ConstItem item) : Statement(item.At) {

  public ConstItem Item { get; } = item;

}

/// <summary>A statement the frontend refuses.</summary>
internal sealed class RefusedStatement(Refusal refusal) : Statement(refusal.At) {

  public Refusal Refusal { get; } = refusal;

}

/// <summary>A block: statements and an optional final expression, its value.</summary>
internal sealed class Block(Span at, IReadOnlyList<Statement> statements, Expr? tail) {

  public Span At { get; } = at;

  public IReadOnlyList<Statement> Statements { get; } = statements;

  public Expr? Tail { get; } = tail;

}

/// <summary>An expression.</summary>
internal abstract class Expr(Span at) {

  public Span At { get; } = at;

}

/// <summary>An integer literal; <see cref="Suffix"/> is its type when the source names one.</summary>
internal sealed class IntegerLiteral(Span at, BigInteger value, string? suffix) : Expr(at) {

  public BigInteger Value { get; } = value;

  public string? Suffix { get; } = suffix;

}

/// <summary>A floating-point literal: its digits and its suffix.</summary>
internal sealed class FloatLiteral(Span at, string text, string? suffix) : Expr(at) {

  public string Text { get; } = text;

  public string? Suffix { get; } = suffix;

}

/// <summary><c>true</c> or <c>false</c>.</summary>
internal sealed class BoolLiteral(Span at, bool value) : Expr(at) {

  public bool Value { get; } = value;

}

/// <summary>A string literal; only the message of <c>expect</c> takes one.</summary>
internal sealed class StringLiteral(Span at, string value) : Expr(at) {

  public string Value { get; } = value;

}

/// <summary>A path: a variable, a constant, <c>i32::MAX</c> or <c>f32::from_bits</c>.</summary>
internal sealed class PathExpr(Span at, IReadOnlyList<string> segments) : Expr(at) {

  public IReadOnlyList<string> Segments { get; } = segments;

  public string Text => string.Join("::", this.Segments);

}

/// <summary><c>-x</c> or <c>!x</c>.</summary>
internal sealed class UnaryExpr(Span at, string op, Expr operand) : Expr(at) {

  public string Op { get; } = op;

  public Expr Operand { get; } = operand;

}

/// <summary>A binary operator other than assignment.</summary>
internal sealed class BinaryExpr(Span at, string op, Expr left, Expr right) : Expr(at) {

  public string Op { get; } = op;

  public Expr Left { get; } = left;

  public Expr Right { get; } = right;

}

/// <summary><c>x = e</c> or a compound assignment <c>x op= e</c>; <see cref="Op"/> is <c>=</c> or the operator.</summary>
internal sealed class AssignExpr(Span at, string op, Expr target, Expr value) : Expr(at) {

  public string Op { get; } = op;

  public Expr Target { get; } = target;

  public Expr Value { get; } = value;

}

/// <summary><c>e as T</c>.</summary>
internal sealed class CastExpr(Span at, Expr operand, TypeRef type) : Expr(at) {

  public Expr Operand { get; } = operand;

  public TypeRef Type { get; } = type;

}

/// <summary><c>receiver.name(arguments)</c>.</summary>
internal sealed class MethodCallExpr(Span at, Expr receiver, string name, IReadOnlyList<Expr> arguments) : Expr(at) {

  public Expr Receiver { get; } = receiver;

  public string Name { get; } = name;

  public IReadOnlyList<Expr> Arguments { get; } = arguments;

}

/// <summary><c>callee(arguments)</c>.</summary>
internal sealed class CallExpr(Span at, Expr callee, IReadOnlyList<Expr> arguments) : Expr(at) {

  public Expr Callee { get; } = callee;

  public IReadOnlyList<Expr> Arguments { get; } = arguments;

}

/// <summary><c>target.0</c> or <c>target.name</c>.</summary>
internal sealed class FieldExpr(Span at, Expr target, string name) : Expr(at) {

  public Expr Target { get; } = target;

  public string Name { get; } = name;

}

/// <summary><c>(a, b)</c>.</summary>
internal sealed class TupleExpr(Span at, IReadOnlyList<Expr> elements) : Expr(at) {

  public IReadOnlyList<Expr> Elements { get; } = elements;

}

/// <summary><c>a..b</c>, <c>a..=b</c> and the open forms.</summary>
internal sealed class RangeExpr(Span at, Expr? start, Expr? end, bool inclusive) : Expr(at) {

  public Expr? Start { get; } = start;

  public Expr? End { get; } = end;

  public bool Inclusive { get; } = inclusive;

}

/// <summary>A block as an expression, with an optional label (<c>'a: { ... }</c>).</summary>
internal sealed class BlockExpr(Span at, Block block, string? label) : Expr(at) {

  public Block Block { get; } = block;

  public string? Label { get; } = label;

}

/// <summary><c>if c { } else ...</c>; the else part is a block expression or another <c>if</c>.</summary>
internal sealed class IfExpr(Span at, Expr condition, Block then, Expr? otherwise) : Expr(at) {

  public Expr Condition { get; } = condition;

  public Block Then { get; } = then;

  public Expr? Else { get; } = otherwise;

}

/// <summary>One arm of a <c>match</c>.</summary>
internal sealed class MatchArm(Span at, Pattern pattern, Expr? guard, Expr body) {

  public Span At { get; } = at;

  public Pattern Pattern { get; } = pattern;

  public Expr? Guard { get; } = guard;

  public Expr Body { get; } = body;

}

/// <summary><c>match scrutinee { arms }</c>.</summary>
internal sealed class MatchExpr(Span at, Expr scrutinee, IReadOnlyList<MatchArm> arms) : Expr(at) {

  public Expr Scrutinee { get; } = scrutinee;

  public IReadOnlyList<MatchArm> Arms { get; } = arms;

}

/// <summary><c>while c { }</c>.</summary>
internal sealed class WhileExpr(Span at, string? label, Expr condition, Block body) : Expr(at) {

  public string? Label { get; } = label;

  public Expr Condition { get; } = condition;

  public Block Body { get; } = body;

}

/// <summary><c>loop { }</c>.</summary>
internal sealed class LoopExpr(Span at, string? label, Block body) : Expr(at) {

  public string? Label { get; } = label;

  public Block Body { get; } = body;

}

/// <summary><c>for pattern in iterable { }</c>.</summary>
internal sealed class ForExpr(Span at, string? label, Pattern pattern, Expr iterable, Block body) : Expr(at) {

  public string? Label { get; } = label;

  public Pattern Pattern { get; } = pattern;

  public Expr Iterable { get; } = iterable;

  public Block Body { get; } = body;

}

/// <summary><c>break 'label value</c>, label and value optional.</summary>
internal sealed class BreakExpr(Span at, string? label, Expr? value) : Expr(at) {

  public string? Label { get; } = label;

  public Expr? Value { get; } = value;

}

/// <summary><c>continue 'label</c>, the label optional.</summary>
internal sealed class ContinueExpr(Span at, string? label) : Expr(at) {

  public string? Label { get; } = label;

}

/// <summary><c>return value</c>, the value optional.</summary>
internal sealed class ReturnExpr(Span at, Expr? value) : Expr(at) {

  public Expr? Value { get; } = value;

}

/// <summary>An expression the frontend refuses.</summary>
internal sealed class RefusedExpr(Refusal refusal) : Expr(refusal.At) {

  public Refusal Refusal { get; } = refusal;

}

/// <summary>A source file: its items in order.</summary>
internal sealed record SourceFile(IReadOnlyList<Item> Items);
