using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Syntax;

// The syntax tree of the Rust a module is written as. The emitter builds it from the IR, the target
// passes rewrite it and RustPrinter is the only place that turns it into text. As in the C# and the
// JavaScript tree, parentheses are nodes of their own, and the properties that say how a node is laid
// out change no meaning.

/// <summary>A node of the Rust syntax tree.</summary>
public abstract record RsNode;

#region Expressions

/// <summary>An expression.</summary>
public abstract record RsExpression : RsNode {

  /// <summary>The IR type the expression was written for, or <see langword="null"/> where none is known.</summary>
  public IrType? Type { get; init; }

}

/// <summary>A literal as it is spelled: <c>5</c>, <c>0xffu32</c>, <c>1.5</c>, <c>true</c>.</summary>
/// <param name="Text">The spelling.</param>
public sealed record RsLiteral(string Text) : RsExpression;

/// <summary>A string literal, written in double quotes; it stands only as the message of a macro.</summary>
/// <param name="Value">The characters.</param>
public sealed record RsString(string Value) : RsExpression;

/// <summary>A path: a variable, a function, or an item such as <c>i32::MIN</c>.</summary>
/// <param name="Name">The path as it is written.</param>
public sealed record RsPath(string Name) : RsExpression;

/// <summary>A prefix operator: <c>-</c> or <c>!</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record RsUnary(string Operator, RsExpression Operand) : RsExpression;

/// <summary>A binary operator, arithmetic, bitwise, comparing or lazy.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">The operator.</param>
/// <param name="Right">The right operand.</param>
public sealed record RsBinary(RsExpression Left, string Operator, RsExpression Right) : RsExpression;

/// <summary><c>operand as T</c>.</summary>
/// <param name="Operand">The value converted.</param>
/// <param name="Target">The type it is converted to.</param>
public sealed record RsCast(RsExpression Operand, string Target) : RsExpression;

/// <summary><c>receiver.name(arguments)</c>.</summary>
/// <param name="Receiver">The value the method is called on.</param>
/// <param name="Name">The method.</param>
/// <param name="Arguments">The arguments after the receiver.</param>
public sealed record RsMethodCall(RsExpression Receiver, string Name, IReadOnlyList<RsExpression> Arguments) : RsExpression;

/// <summary><c>target.name</c>: a field of a struct or of a range.</summary>
/// <param name="Target">The value whose field is read.</param>
/// <param name="Name">The field.</param>
public sealed record RsField(RsExpression Target, string Name) : RsExpression;

/// <summary><c>callee(arguments)</c>.</summary>
/// <param name="Callee">The function, a path.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record RsCall(RsExpression Callee, IReadOnlyList<RsExpression> Arguments) : RsExpression;

/// <summary><c>target[index]</c>.</summary>
/// <param name="Target">The array.</param>
/// <param name="Index">The index, a <c>usize</c>.</param>
public sealed record RsIndex(RsExpression Target, RsExpression Index) : RsExpression;

/// <summary>An expression in parentheses.</summary>
/// <param name="Inner">The expression.</param>
public sealed record RsParenthesized(RsExpression Inner) : RsExpression;

/// <summary>
/// A macro invocation with parentheses: <c>panic!("IntegerOverflow")</c>, <c>unreachable!()</c>,
/// <c>assert_eq!(a, b)</c>; or with brackets, <c>vec![…]</c>, whose one argument is the
/// <see cref="RsArray"/> it is written as.
/// </summary>
/// <param name="Name">The macro, without its <c>!</c>.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record RsMacro(string Name, IReadOnlyList<RsExpression> Arguments) : RsExpression {

  /// <summary>Whether the macro is written with brackets, <c>vec![1, 2]</c>, its one argument an array.</summary>
  public bool Bracketed { get; init; }

}

/// <summary><c>&amp;operand</c> or <c>&amp;mut operand</c>: a borrow.</summary>
/// <param name="Mutable">Whether the borrow is exclusive, <c>&amp;mut</c>.</param>
/// <param name="Operand">The place borrowed.</param>
public sealed record RsReference(bool Mutable, RsExpression Operand) : RsExpression;

/// <summary>An assignment, simple or compound: <c>x = v</c>, <c>x ^= v</c>.</summary>
/// <param name="Target">The place.</param>
/// <param name="Operator">The operator: <c>=</c>, <c>+=</c>, <c>^=</c> and so on.</param>
/// <param name="Value">The value.</param>
public sealed record RsAssign(RsExpression Target, string Operator, RsExpression Value) : RsExpression;

/// <summary>A block as an expression: <c>{ statements; tail }</c>.</summary>
/// <param name="Block">The block.</param>
public sealed record RsBlockExpression(RsBlock Block) : RsExpression;

/// <summary>
/// <c>unsafe { … }</c>: the one block of unsafe code the backend writes, inside the spelling function of a
/// mutable overlay view, where the profile allows unsafe code (#460).
/// </summary>
/// <param name="Block">The body.</param>
public sealed record RsUnsafe(RsBlock Block) : RsExpression;

/// <summary>A line comment among the statements of a block, <c>// text</c>: the <c>SAFETY:</c> reason above an unsafe block.</summary>
/// <param name="Text">The text after <c>// </c>.</param>
public sealed record RsComment(string Text) : RsStatement;

/// <summary><c>if condition { … } else …</c>, a statement or an expression.</summary>
/// <param name="Condition">The truth value.</param>
/// <param name="Then">The block when it holds.</param>
/// <param name="Else">A block (<see cref="RsBlockExpression"/>) or another <see cref="RsIf"/>, or <see langword="null"/>.</param>
public sealed record RsIf(RsExpression Condition, RsBlock Then, RsExpression? Else) : RsExpression;

/// <summary><c>'label: while condition { … }</c>.</summary>
/// <param name="Label">The label without its quote, or <see langword="null"/>.</param>
/// <param name="Condition">The truth value tested before every pass.</param>
/// <param name="Body">The body.</param>
public sealed record RsWhile(string? Label, RsExpression Condition, RsBlock Body) : RsExpression;

/// <summary><c>'label: loop { … }</c>.</summary>
/// <param name="Label">The label without its quote, or <see langword="null"/>.</param>
/// <param name="Body">The body.</param>
public sealed record RsLoop(string? Label, RsBlock Body) : RsExpression;

/// <summary><c>'label: for pattern in iterator { … }</c>.</summary>
/// <param name="Label">The label without its quote, or <see langword="null"/>.</param>
/// <param name="Pattern">The binding: a name or <c>_</c>.</param>
/// <param name="Iterator">The range iterated over.</param>
/// <param name="Body">The body.</param>
public sealed record RsFor(string? Label, string Pattern, RsExpression Iterator, RsBlock Body) : RsExpression;

/// <summary><c>start..end</c> or <c>start..=end</c>; <c>..end</c> and <c>start..</c> without one of them.</summary>
/// <param name="Start">The first value, or <see langword="null"/> from the start.</param>
/// <param name="End">The bound, or <see langword="null"/> to the end.</param>
/// <param name="Inclusive">Whether the bound is part of the range.</param>
public sealed record RsRange(RsExpression? Start, RsExpression? End, bool Inclusive) : RsExpression;

/// <summary>A labelled block, <c>'label: { … }</c>, which a <c>break 'label</c> leaves (Rust 1.65).</summary>
/// <param name="Label">The label without its quote.</param>
/// <param name="Body">The block.</param>
public sealed record RsLabeledBlock(string Label, RsBlock Body) : RsExpression;

/// <summary>One arm of a <see cref="RsMatch"/>: patterns joined by <c>|</c>, then the body.</summary>
/// <param name="Patterns">The patterns as they are written: <c>1</c>, <c>10..=19</c>, <c>_</c>.</param>
/// <param name="Body">The body: an expression, or a block (<see cref="RsBlockExpression"/>).</param>
public sealed record RsMatchArm(IReadOnlyList<string> Patterns, RsExpression Body) : RsNode;

/// <summary><c>match scrutinee { arms }</c>.</summary>
/// <param name="Scrutinee">The value matched.</param>
/// <param name="Arms">The arms, tried in order.</param>
public sealed record RsMatch(RsExpression Scrutinee, IReadOnlyList<RsMatchArm> Arms) : RsExpression;

/// <summary><c>break 'label value</c>.</summary>
/// <param name="Label">The label without its quote, or <see langword="null"/> for the innermost loop.</param>
/// <param name="Value">The value a <c>loop</c> yields, or <see langword="null"/>.</param>
public sealed record RsBreak(string? Label, RsExpression? Value) : RsExpression;

/// <summary><c>continue 'label</c>.</summary>
/// <param name="Label">The label without its quote, or <see langword="null"/> for the innermost loop.</param>
public sealed record RsContinue(string? Label) : RsExpression;

/// <summary><c>return value</c>.</summary>
/// <param name="Value">The value, or <see langword="null"/>.</param>
public sealed record RsReturn(RsExpression? Value) : RsExpression;

#endregion

#region Statements

/// <summary>A statement of a block.</summary>
public abstract record RsStatement : RsNode;

/// <summary><c>let mut name: T = value;</c>, with each part optional but the name.</summary>
/// <param name="Name">The binding.</param>
/// <param name="Mutable">Whether it is <c>mut</c>.</param>
/// <param name="TypeName">The declared type, or <see langword="null"/>.</param>
/// <param name="Initializer">The value, or <see langword="null"/> for a binding initialized later.</param>
public sealed record RsLet(string Name, bool Mutable, string? TypeName, RsExpression? Initializer) : RsStatement;

/// <summary>An expression as a statement; one ending in a block (<c>if</c>, <c>while</c>, <c>match</c>…) takes no semicolon.</summary>
/// <param name="Expression">The expression.</param>
public sealed record RsExpressionStatement(RsExpression Expression) : RsStatement;

/// <summary>Statements and an optional tail expression, which is the value of the block.</summary>
/// <param name="Statements">The statements.</param>
/// <param name="Tail">The value, or <see langword="null"/>.</param>
public sealed record RsBlock(IReadOnlyList<RsStatement> Statements, RsExpression? Tail) : RsNode {

  /// <summary>A block with neither statements nor a value.</summary>
  public static RsBlock Empty { get; } = new([], null);

  /// <summary>Whether the block stands for no body at all, the <c>;</c> of a required method of a trait.</summary>
  public bool IsDeclaration { get; init; }

}

#endregion

#region Items

/// <summary>An item of a file or a module.</summary>
public abstract record RsItem : RsNode {

  /// <summary>The documentation, one <c>///</c> line each.</summary>
  public IReadOnlyList<string> Docs { get; init; } = [];

  /// <summary>The outer attributes, each without <c>#[</c> and <c>]</c>.</summary>
  public IReadOnlyList<string> Attributes { get; init; } = [];

}

/// <summary>A parameter of a function: <c>name: T</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="TypeName">The type.</param>
public sealed record RsParameter(string Name, string TypeName) : RsNode;

/// <summary><c>pub fn name(parameters) -> R { body }</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Visibility">The visibility as written: <c>pub</c>, <c>pub(crate)</c> or empty.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Returns">The result type, or <see langword="null"/> for none.</param>
/// <param name="Body">The body.</param>
public sealed record RsFunction(string Name, string Visibility, IReadOnlyList<RsParameter> Parameters, string? Returns, RsBlock Body) : RsItem {

  /// <summary>Whether the function is <c>async fn</c>.</summary>
  public bool IsAsync { get; init; }

  /// <summary>The generic parameters as they are written after the name, <c>&lt;'a&gt;</c>, or empty.</summary>
  public string Generics { get; init; } = "";

  /// <summary>The clauses of its <c>where</c>, <c>T: Ord + Default + Copy</c>, each without its comma; empty for none.</summary>
  public IReadOnlyList<string> Where { get; init; } = [];

  /// <summary>Whether the function is a helper the backend put in, which the target passes may inline or remove.</summary>
  public bool IsHelper { get; init; }

}

/// <summary><c>extern "C" { items }</c>, or <c>unsafe extern "C" { items }</c> in Rust 2024.</summary>
/// <param name="Abi">The ABI string, such as <c>C</c>.</param>
/// <param name="IsUnsafe">Whether the block is marked <c>unsafe</c>.</param>
/// <param name="Items">The foreign function declarations.</param>
public sealed record RsExternBlock(string Abi, bool IsUnsafe, IReadOnlyList<RsFunction> Items) : RsItem;

/// <summary><c>const NAME: T = value;</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Visibility">The visibility as written, or empty.</param>
/// <param name="TypeName">The type, also an array type <c>[u32; 256]</c>.</param>
/// <param name="Value">The value.</param>
public sealed record RsConst(string Name, string Visibility, string TypeName, RsExpression Value) : RsItem {

  /// <summary>Whether the item is a <c>static</c>, one place in memory, rather than a <c>const</c> copied wherever it is named.</summary>
  public bool IsStatic { get; init; }

  /// <summary>
  /// Whether the item is a static of each thread, <c>thread_local! { static NAME: T = value; }</c>, made
  /// on its first use in a thread: module state of a type no <c>static</c> may hold, an <c>Rc</c>.
  /// </summary>
  public bool IsThreadLocal { get; init; }

  /// <summary>Whether the value of a static of each thread is a constant, <c>const { value }</c>, which needs no check of its first use (Rust 1.59).</summary>
  public bool IsConstInitialized { get; init; }

}

/// <summary><c>Name { a: x, b }</c>: a struct built from the values of its fields, a field named like its value's variable written once.</summary>
/// <param name="TypeName">The struct.</param>
/// <param name="Fields">Each field with its value, in the order they are evaluated.</param>
public sealed record RsStructLiteral(string TypeName, IReadOnlyList<RsFieldValue> Fields) : RsExpression;

/// <summary>A field of a <see cref="RsStructLiteral"/> and its value.</summary>
/// <param name="Name">The field.</param>
/// <param name="Value">The value.</param>
public sealed record RsFieldValue(string Name, RsExpression Value) : RsNode;

/// <summary><c>[a, b, c]</c>, the value of a table; with a <see cref="Count"/>, <c>[value; count]</c>.</summary>
/// <param name="Elements">The elements; the one value repeated where there is a count.</param>
public sealed record RsArray(IReadOnlyList<RsExpression> Elements) : RsExpression {

  /// <summary>How often the one element stands in the array, or <see langword="null"/> for a list of elements.</summary>
  public RsExpression? Count { get; init; }

}

/// <summary>A field of a struct: <c>pub name: T</c>, or of a tuple struct <c>pub T</c> where the name is empty.</summary>
/// <param name="Name">The name, empty for a field of a tuple struct.</param>
/// <param name="Visibility">The visibility as written, or empty.</param>
/// <param name="TypeName">The type.</param>
public sealed record RsFieldDeclaration(string Name, string Visibility, string TypeName) : RsNode;

/// <summary>
/// <c>pub struct Name { fields }</c>; a tuple struct <c>pub struct Name(pub T);</c> where its fields
/// have no names, a unit struct <c>pub struct Name;</c> where it has none.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Visibility">The visibility as written, or empty.</param>
/// <param name="Fields">The fields, in declaration order.</param>
public sealed record RsStruct(string Name, string Visibility, IReadOnlyList<RsFieldDeclaration> Fields) : RsItem {

  /// <summary>The generic parameters as they are written after the name, <c>&lt;S&gt;</c>, or empty.</summary>
  public string Generics { get; init; } = "";

}

/// <summary><c>impl Type { items }</c>, or <c>impl Trait for Type { items }</c>.</summary>
/// <param name="Trait">The trait implemented, or <see langword="null"/> for an inherent implementation.</param>
/// <param name="TypeName">The type the items belong to.</param>
/// <param name="Items">The items: functions and constants.</param>
public sealed record RsImpl(string? Trait, string TypeName, IReadOnlyList<RsItem> Items) : RsItem {

  /// <summary>The generic parameters as they are written after <c>impl</c>, <c>&lt;S: Iterator&lt;Item = i32&gt; + Clone&gt;</c>, or empty.</summary>
  public string Generics { get; init; } = "";

}

/// <summary><c>type Name = T;</c>, the associated type of a trait implementation, such as the <c>Item</c> of an <c>Iterator</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="TypeName">The type it stands for.</param>
public sealed record RsTypeAlias(string Name, string TypeName) : RsItem;

/// <summary><c>pub trait Name: Super + Other { items }</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Visibility">The visibility as written.</param>
/// <param name="Supertraits">The traits every type of this one has.</param>
/// <param name="Items">The methods, a required one with a declaration block (<see cref="RsBlock.IsDeclaration"/>).</param>
public sealed record RsTrait(string Name, string Visibility, IReadOnlyList<string> Supertraits, IReadOnlyList<RsItem> Items) : RsItem;

/// <summary><c>pub mod name;</c>, the declaration of a module whose items are in a file of its own.</summary>
/// <param name="Name">The module.</param>
/// <param name="Visibility">The visibility as written, or empty.</param>
public sealed record RsModuleDeclaration(string Name, string Visibility) : RsItem;

/// <summary><c>use path;</c>, a re-export <c>pub use path;</c>.</summary>
/// <param name="Path">The path.</param>
public sealed record RsUse(string Path) : RsItem {

  /// <summary>The visibility as written, <c>pub</c> for a re-export, or empty.</summary>
  public string Visibility { get; init; } = "";

}

/// <summary>A variant of a field-less enum: <c>Name</c>, or <c>Name = 2</c> with its discriminant.</summary>
/// <param name="Name">The name.</param>
/// <param name="Discriminant">The discriminant as written, or <see langword="null"/>.</param>
public sealed record RsVariant(string Name, string? Discriminant) : RsNode {

  /// <summary>Whether the variant is the one a derived <c>Default</c> gives, <c>#[default]</c>.</summary>
  public bool IsDefault { get; init; }

}

/// <summary><c>pub enum Name { A = 1, B = 2 }</c>, an enum without fields.</summary>
/// <param name="Name">The name.</param>
/// <param name="Visibility">The visibility as written, or empty.</param>
/// <param name="Variants">The variants, in declaration order.</param>
public sealed record RsEnum(string Name, string Visibility, IReadOnlyList<RsVariant> Variants) : RsItem;

/// <summary><c>mod name { items }</c>, a module written in place, such as the <c>#[cfg(test)]</c> module of the tests.</summary>
/// <param name="Name">The module.</param>
/// <param name="Items">Its items.</param>
public sealed record RsInlineModule(string Name, IReadOnlyList<RsItem> Items) : RsItem;

/// <summary>
/// A source file: the inner documentation (<c>//!</c>), the inner attributes (<c>#![…]</c>) and the
/// items, separated by blank lines.
/// </summary>
/// <param name="InnerDocs">The lines of the inner documentation.</param>
/// <param name="InnerAttributes">The inner attributes, each without <c>#![</c> and <c>]</c>.</param>
/// <param name="Items">The items.</param>
public sealed record RsFile(IReadOnlyList<string> InnerDocs, IReadOnlyList<string> InnerAttributes, IReadOnlyList<RsItem> Items) : RsNode;

#endregion
