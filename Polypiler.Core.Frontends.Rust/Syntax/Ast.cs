using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Core.Frontends.Rust.Syntax;

// The syntax tree of the imported subset of Rust. Nodes are compared by reference: the type checker
// keeps the type of every expression in a table keyed by the node itself.

/// <summary>What a type of the source is made of.</summary>
internal enum TypeShape {

  /// <summary>A path, <c>u32</c>, <c>Vec&lt;u8&gt;</c>, <c>Self</c>, <c>crate::m::S</c>, with its generic arguments.</summary>
  Path,

  /// <summary><c>()</c> or <c>(A, B)</c>.</summary>
  Tuple,

  /// <summary><c>[T; N]</c>.</summary>
  Array,

  /// <summary><c>[T]</c>, behind a reference.</summary>
  Slice,

  /// <summary><c>&amp;T</c> or <c>&amp;mut T</c>.</summary>
  Reference,

  /// <summary><c>*const T</c> or <c>*mut T</c>.</summary>
  Pointer,

  /// <summary><c>!</c>.</summary>
  Never,

  /// <summary><c>fn(A) -&gt; R</c>.</summary>
  Function,

  /// <summary><c>impl Trait</c>, <c>dyn Trait</c> or the parenthesised form of a closure trait.</summary>
  Trait

}

/// <summary>A type as the source spells it.</summary>
/// <param name="Name">The text of the type, as a diagnostic names it; of a path its segments without generic arguments.</param>
/// <param name="At">Where the type starts.</param>
internal sealed record TypeRef(string Name, Span At) {

  /// <summary>What the type is made of.</summary>
  public TypeShape Shape { get; init; } = TypeShape.Path;

  /// <summary>The generic arguments of a path, the elements of a tuple, the parameters of a function type.</summary>
  public IReadOnlyList<TypeRef> Arguments { get; init; } = [];

  /// <summary>The associated type an argument of a bound binds, <c>Output</c> of <c>Add&lt;Output = T&gt;</c>; the type is <see cref="Element"/>.</summary>
  public string? Binding { get; init; }

  /// <summary>The element of an array or a slice, the target of a reference or a pointer, the result of a function type.</summary>
  public TypeRef? Element { get; init; }

  /// <summary>The length of an array type.</summary>
  public Expr? Length { get; init; }

  /// <summary>Whether a reference or a pointer is <c>mut</c>.</summary>
  public bool Mutable { get; init; }

  /// <summary>The lifetime of a reference, without the quote, or <see langword="null"/>.</summary>
  public string? Lifetime { get; init; }

  /// <summary>The whole text of the type, generic arguments included.</summary>
  public string Text { get; init; } = "";

}

/// <summary>A construct the parser recognised and the frontend refuses: the diagnostic to report and what it names.</summary>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Construct">The construct, as the message names it.</param>
/// <param name="At">Where it starts.</param>
internal sealed record Refusal(string Code, string Construct, Span At);

/// <summary>An attribute: its name and the text of its arguments, <c>derive(Clone, Copy)</c> gives <c>derive</c> and <c>Clone, Copy</c>.</summary>
internal sealed record Attribute(string Name, IReadOnlyList<string> Arguments, Span At);

/// <summary>An item of the source file.</summary>
internal abstract class Item(Span at) {

  /// <summary>Where the item starts.</summary>
  public Span At { get; } = at;

  /// <summary>Whether the item is <c>pub</c> (any <c>pub(...)</c> counts as not public outside the crate).</summary>
  public bool IsPublic { get; init; }

  /// <summary>Whether the item is visible to other modules of the crate: <c>pub</c> or <c>pub(crate)</c>, <c>pub(super)</c>, ...</summary>
  public bool IsVisible { get; init; }

}

/// <summary>How a method takes its receiver.</summary>
internal enum ReceiverKind {

  /// <summary>No receiver: an associated function or a free function.</summary>
  None,

  /// <summary><c>self</c> or <c>mut self</c>: by value.</summary>
  Value,

  /// <summary><c>&amp;self</c>.</summary>
  Shared,

  /// <summary><c>&amp;mut self</c>.</summary>
  Exclusive

}

/// <summary>A generic type parameter and the traits it is bounded by, its own and those of a <c>where</c> clause.</summary>
internal sealed record GenericParameter(string Name, Span At) {

  public IReadOnlyList<TypeRef> Bounds { get; init; } = [];

}

/// <summary>A function: free, nested, or a method of an <c>impl</c>.</summary>
internal sealed class FunctionItem(Span at, string name, IReadOnlyList<Parameter> parameters, TypeRef? returnType, Block body) : Item(at) {

  public string Name { get; } = name;

  public IReadOnlyList<Parameter> Parameters { get; } = parameters;

  public TypeRef? ReturnType { get; } = returnType;

  public Block Body { get; } = body;

  /// <summary>How a method takes <c>self</c>.</summary>
  public ReceiverKind Receiver { get; init; }

  /// <summary>Whether the receiver binding is <c>mut self</c>.</summary>
  public bool MutableReceiver { get; init; }

  /// <summary>Where the receiver is written.</summary>
  public Span ReceiverAt { get; init; }

  /// <summary>Whether the function is a <c>#[test]</c>.</summary>
  public bool IsTest { get; init; }

  /// <summary>The generic type parameters.</summary>
  public IReadOnlyList<GenericParameter> Generics { get; init; } = [];

  /// <summary>Whether the function is a method of a trait without a body, which every impl provides.</summary>
  public bool IsRequired { get; init; }

}

/// <summary>A constant, as an item or as a statement.</summary>
internal sealed class ConstItem(Span at, string name, TypeRef type, Expr value) : Item(at) {

  public string Name { get; } = name;

  public TypeRef Type { get; } = type;

  public Expr Value { get; } = value;

  /// <summary>Whether it is an immutable <c>static</c>, a value of its own address rather than a constant folded at its uses.</summary>
  public bool IsStatic { get; init; }

}

/// <summary>A field of a struct: named, or numbered for a tuple struct.</summary>
internal sealed record FieldDeclaration(string Name, TypeRef Type, bool IsVisible, Span At);

/// <summary>The forms of a struct.</summary>
internal enum StructForm {

  /// <summary><c>struct S { a: T }</c>.</summary>
  Named,

  /// <summary><c>struct S(T);</c>.</summary>
  Tuple,

  /// <summary><c>struct S;</c>.</summary>
  Unit

}

/// <summary>A <c>struct</c>.</summary>
internal sealed class StructItem(Span at, string name, StructForm form, IReadOnlyList<FieldDeclaration> fields, IReadOnlyList<Attribute> attributes) : Item(at) {

  public string Name { get; } = name;

  public StructForm Form { get; } = form;

  public IReadOnlyList<FieldDeclaration> Fields { get; } = fields;

  public IReadOnlyList<Attribute> Attributes { get; } = attributes;

  /// <summary>The generic type parameters.</summary>
  public IReadOnlyList<GenericParameter> Generics { get; init; } = [];

}

/// <summary>A variant of an enum without data: its name and its explicit discriminant, if any.</summary>
internal sealed record VariantDeclaration(string Name, Expr? Discriminant, bool IsDefault, Span At);

/// <summary>An <c>enum</c> whose variants carry no data.</summary>
internal sealed class EnumItem(Span at, string name, IReadOnlyList<VariantDeclaration> variants, IReadOnlyList<Attribute> attributes) : Item(at) {

  public string Name { get; } = name;

  public IReadOnlyList<VariantDeclaration> Variants { get; } = variants;

  public IReadOnlyList<Attribute> Attributes { get; } = attributes;

}

/// <summary>An <c>impl</c> block, inherent or of a trait: its type and its functions, constants and associated types.</summary>
internal sealed class ImplItem(Span at, TypeRef type, IReadOnlyList<Item> items) : Item(at) {

  public TypeRef Type { get; } = type;

  public IReadOnlyList<Item> Items { get; } = items;

  /// <summary>The trait of <c>impl Trait for Type</c>; <see langword="null"/> for an inherent impl.</summary>
  public TypeRef? Trait { get; init; }

  /// <summary>The generic type parameters of <c>impl&lt;T&gt;</c>.</summary>
  public IReadOnlyList<GenericParameter> Generics { get; init; } = [];

}

/// <summary>A <c>trait</c>: its methods, required and provided, and its associated types.</summary>
internal sealed class TraitItem(Span at, string name, IReadOnlyList<Item> items) : Item(at) {

  public string Name { get; } = name;

  public IReadOnlyList<Item> Items { get; } = items;

  /// <summary>The traits every implementer implements too, <c>trait A: B</c>.</summary>
  public IReadOnlyList<TypeRef> Supertraits { get; init; } = [];

  /// <summary>The type parameters of a generic trait, <c>trait Into&lt;T&gt;</c>, which each impl gives its own arguments.</summary>
  public IReadOnlyList<GenericParameter> Generics { get; init; } = [];

}

/// <summary><c>type Output = T;</c> of an impl, or <c>type Output;</c> of a trait.</summary>
internal sealed class AssociatedTypeItem(Span at, string name, TypeRef? type) : Item(at) {

  public string Name { get; } = name;

  public TypeRef? Type { get; } = type;

}

/// <summary>A module: inline with its items, or a file module whose items the import reads from its file.</summary>
internal sealed class ModuleItem(Span at, string name, IReadOnlyList<Item>? items) : Item(at) {

  public string Name { get; } = name;

  /// <summary>The items of an inline module; <see langword="null"/> for <c>mod m;</c>.</summary>
  public IReadOnlyList<Item>? Items { get; set; } = items;

  /// <summary>Whether the module is compiled only for tests: <c>#[cfg(test)]</c>.</summary>
  public bool TestOnly { get; init; }

}

/// <summary>One path a <c>use</c> declaration brings into scope: a name, renamed or not, or every public name of a module.</summary>
internal sealed record UsePath(IReadOnlyList<string> Segments, string? Alias, bool Glob, Span At);

/// <summary>A <c>use</c> declaration, its tree flattened into paths.</summary>
internal sealed class UseItem(Span at, IReadOnlyList<UsePath> paths) : Item(at) {

  public IReadOnlyList<UsePath> Paths { get; } = paths;

}

/// <summary>An item the frontend refuses; the refusal says why.</summary>
internal sealed class RefusedItem(Refusal refusal) : Item(refusal.At) {

  public Refusal Refusal { get; } = refusal;

}

/// <summary>A parameter of a function or a closure: a pattern and its type, which a closure may leave out.</summary>
internal sealed record Parameter(Pattern Pattern, TypeRef? Type, Span At) {

  /// <summary>The name of a parameter that is a plain binding, otherwise <see langword="null"/>.</summary>
  public string? Name => this.Pattern is IdentifierPattern identifier ? identifier.Name : null;

  /// <summary>Whether a plain binding is <c>mut</c>.</summary>
  public bool Mutable => this.Pattern is IdentifierPattern { Mutable: true };

}

/// <summary>A pattern of a <c>let</c>, a <c>for</c>, a parameter or a <c>match</c> arm.</summary>
internal abstract class Pattern(Span at) {

  public Span At { get; } = at;

}

/// <summary><c>x</c> or <c>mut x</c>: binds the value, unless the name is a constant, a unit struct or a variant, which it then compares with.</summary>
internal sealed class IdentifierPattern(Span at, string name, bool mutable) : Pattern(at) {

  public string Name { get; } = name;

  public bool Mutable { get; } = mutable;

}

/// <summary><c>_</c>.</summary>
internal sealed class WildcardPattern(Span at) : Pattern(at);

/// <summary><c>..</c> inside a tuple, a tuple struct or a slice pattern.</summary>
internal sealed class RestPattern(Span at) : Pattern(at);

/// <summary>A literal or a path to a constant, a unit struct or a variant, compared with the value.</summary>
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

/// <summary><c>Some(p)</c>, <c>Ok(p)</c>, <c>S(p, q)</c>: a path and the patterns of its fields.</summary>
internal sealed class TupleStructPattern(Span at, PathExpr path, IReadOnlyList<Pattern> elements) : Pattern(at) {

  public PathExpr Path { get; } = path;

  public IReadOnlyList<Pattern> Elements { get; } = elements;

}

/// <summary><c>S { a, b: p, .. }</c>.</summary>
internal sealed class StructPattern(Span at, PathExpr path, IReadOnlyList<(string Name, Pattern Pattern)> fields, bool rest) : Pattern(at) {

  public PathExpr Path { get; } = path;

  public IReadOnlyList<(string Name, Pattern Pattern)> Fields { get; } = fields;

  /// <summary>Whether the pattern ends with <c>..</c>, which leaves the other fields out.</summary>
  public bool Rest { get; } = rest;

}

/// <summary><c>&amp;p</c> or <c>&amp;mut p</c>: the value a reference points to.</summary>
internal sealed class ReferencePattern(Span at, Pattern inner, bool mutable) : Pattern(at) {

  public Pattern Inner { get; } = inner;

  public bool Mutable { get; } = mutable;

}

/// <summary>A pattern the frontend refuses.</summary>
internal sealed class RefusedPattern(Refusal refusal) : Pattern(refusal.At) {

  public Refusal Refusal { get; } = refusal;

}

/// <summary>A statement of a block.</summary>
internal abstract class Statement(Span at) {

  public Span At { get; } = at;

}

/// <summary><c>let pattern: type = init else { ... };</c>, each part but the pattern optional.</summary>
internal sealed class LetStatement(Span at, Pattern pattern, TypeRef? type, Expr? initializer, Block? otherwise) : Statement(at) {

  public Pattern Pattern { get; } = pattern;

  public TypeRef? Type { get; } = type;

  public Expr? Initializer { get; } = initializer;

  /// <summary>The block of <c>let ... else</c>, which never ends.</summary>
  public Block? Else { get; } = otherwise;

}

/// <summary>An expression evaluated for its effect.</summary>
internal sealed class ExpressionStatement(Span at, Expr expression) : Statement(at) {

  public Expr Expression { get; } = expression;

}

/// <summary>A constant declared in a block.</summary>
internal sealed class ConstStatement(ConstItem item) : Statement(item.At) {

  public ConstItem Item { get; } = item;

}

/// <summary>A function declared in a block: an item of its own, visible in the block only.</summary>
internal sealed class FunctionStatement(FunctionItem item) : Statement(item.At) {

  public FunctionItem Item { get; } = item;

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

/// <summary>A string literal: the message of <c>expect</c> and of the panicking macros, or a <c>&amp;str</c>.</summary>
internal sealed class StringLiteral(Span at, string value) : Expr(at) {

  public string Value { get; } = value;

}

/// <summary>A <c>char</c> literal: its Unicode scalar value.</summary>
internal sealed class CharLiteral(Span at, int value) : Expr(at) {

  public int Value { get; } = value;

}

/// <summary><c>b"..."</c>: a reference to an array of bytes.</summary>
internal sealed class ByteStringLiteral(Span at, IReadOnlyList<byte> bytes) : Expr(at) {

  public IReadOnlyList<byte> Bytes { get; } = bytes;

}

/// <summary>A path: a variable, a constant, a function, <c>i32::MAX</c>, <c>E::A</c>, <c>S::new</c>.</summary>
internal sealed class PathExpr(Span at, IReadOnlyList<string> segments) : Expr(at) {

  public IReadOnlyList<string> Segments { get; } = segments;

  /// <summary>The generic arguments a segment gave with <c>::&lt;...&gt;</c>, by the index of the segment they follow.</summary>
  public IReadOnlyDictionary<int, IReadOnlyList<TypeRef>> Generics { get; init; } = new Dictionary<int, IReadOnlyList<TypeRef>>();

  public string Text => string.Join("::", this.Segments);

}

/// <summary><c>-x</c> or <c>!x</c>.</summary>
internal sealed class UnaryExpr(Span at, string op, Expr operand) : Expr(at) {

  public string Op { get; } = op;

  public Expr Operand { get; } = operand;

}

/// <summary><c>&amp;e</c> or <c>&amp;mut e</c>.</summary>
internal sealed class ReferenceExpr(Span at, bool mutable, Expr operand) : Expr(at) {

  public bool Mutable { get; } = mutable;

  public Expr Operand { get; } = operand;

}

/// <summary><c>*e</c>.</summary>
internal sealed class DerefExpr(Span at, Expr operand) : Expr(at) {

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

/// <summary><c>receiver.name::&lt;T&gt;(arguments)</c>.</summary>
internal sealed class MethodCallExpr(Span at, Expr receiver, string name, IReadOnlyList<Expr> arguments) : Expr(at) {

  public Expr Receiver { get; } = receiver;

  public string Name { get; } = name;

  public IReadOnlyList<Expr> Arguments { get; } = arguments;

  /// <summary>The generic arguments of a turbofish, <c>sum::&lt;u8&gt;()</c>.</summary>
  public IReadOnlyList<TypeRef> Generics { get; init; } = [];

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

/// <summary><c>target[index]</c>.</summary>
internal sealed class IndexExpr(Span at, Expr target, Expr index) : Expr(at) {

  public Expr Target { get; } = target;

  public Expr Index { get; } = index;

}

/// <summary><c>(a, b)</c>; <c>()</c> has no elements.</summary>
internal sealed class TupleExpr(Span at, IReadOnlyList<Expr> elements) : Expr(at) {

  public IReadOnlyList<Expr> Elements { get; } = elements;

}

/// <summary><c>[a, b, c]</c>.</summary>
internal sealed class ArrayExpr(Span at, IReadOnlyList<Expr> elements) : Expr(at) {

  public IReadOnlyList<Expr> Elements { get; } = elements;

}

/// <summary><c>[value; count]</c>.</summary>
internal sealed class ArrayRepeatExpr(Span at, Expr value, Expr count) : Expr(at) {

  public Expr Value { get; } = value;

  public Expr Count { get; } = count;

}

/// <summary><c>S { a, b: e, ..base }</c>, the fields in source order.</summary>
internal sealed class StructExpr(Span at, PathExpr path, IReadOnlyList<(string Name, Expr Value, Span At)> fields, Expr? @base) : Expr(at) {

  public PathExpr Path { get; } = path;

  public IReadOnlyList<(string Name, Expr Value, Span At)> Fields { get; } = fields;

  public Expr? Base { get; } = @base;

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

/// <summary><c>if c { } else ...</c> or <c>if let p = e { } else ...</c>; the else part is a block expression or another <c>if</c>.</summary>
internal sealed class IfExpr(Span at, Expr condition, Block then, Expr? otherwise) : Expr(at) {

  public Expr Condition { get; } = condition;

  public Block Then { get; } = then;

  public Expr? Else { get; } = otherwise;

  /// <summary>The pattern of <c>if let</c>, tested against <see cref="Condition"/>; <see langword="null"/> for a plain <c>if</c>.</summary>
  public Pattern? Pattern { get; init; }

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

/// <summary><c>while c { }</c> or <c>while let p = e { }</c>.</summary>
internal sealed class WhileExpr(Span at, string? label, Expr condition, Block body) : Expr(at) {

  public string? Label { get; } = label;

  public Expr Condition { get; } = condition;

  public Block Body { get; } = body;

  /// <summary>The pattern of <c>while let</c>; <see langword="null"/> for a plain <c>while</c>.</summary>
  public Pattern? Pattern { get; init; }

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

/// <summary><c>e?</c>.</summary>
internal sealed class TryExpr(Span at, Expr operand) : Expr(at) {

  public Expr Operand { get; } = operand;

}

/// <summary><c>|a, b: T| body</c> or <c>move |a| body</c>.</summary>
internal sealed class ClosureExpr(Span at, bool move, IReadOnlyList<Parameter> parameters, TypeRef? returnType, Expr body) : Expr(at) {

  public bool Move { get; } = move;

  public IReadOnlyList<Parameter> Parameters { get; } = parameters;

  public TypeRef? ReturnType { get; } = returnType;

  public Expr Body { get; } = body;

}

/// <summary>
/// A call of one of the macros the frontend imports: <c>assert!</c> and its relatives, <c>panic!</c>,
/// <c>unreachable!</c>, <c>todo!</c>, <c>unimplemented!</c> and <c>vec!</c>, with its arguments parsed
/// as expressions; <c>vec![value; count]</c> has the value and the count.
/// </summary>
internal sealed class MacroExpr(Span at, string name, IReadOnlyList<Expr> arguments, bool repeat) : Expr(at) {

  public string Name { get; } = name;

  public IReadOnlyList<Expr> Arguments { get; } = arguments;

  /// <summary>Whether the arguments are <c>value; count</c>, as <c>vec![0; n]</c> writes them.</summary>
  public bool Repeat { get; } = repeat;

}

/// <summary>An expression the frontend refuses.</summary>
internal sealed class RefusedExpr(Refusal refusal) : Expr(refusal.At) {

  public Refusal Refusal { get; } = refusal;

}

/// <summary>A source file: its items in order.</summary>
internal sealed record SourceFile(IReadOnlyList<Item> Items);
