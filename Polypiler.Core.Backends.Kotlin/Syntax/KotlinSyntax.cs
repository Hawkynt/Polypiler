using System.Collections.Generic;

namespace Polypiler.Core.Backends.Kotlin.Syntax;

// The syntax tree of the Kotlin a module is written as, shaped after the grammar of the Kotlin
// language specification (kotlinlang.org/spec, "Syntax and grammar") for the subset the backend
// writes. The emitter builds it from the IR, the target passes rewrite it and KotlinPrinter is the only
// place that turns it into text; KotlinSyntaxReader reads that text back into the same tree. There is
// no node for parentheses: the printer puts them where Kotlin's precedence needs them, and around a
// mixed operand of an infix function, so a tree has one text.

/// <summary>A node of the Kotlin syntax tree.</summary>
public abstract record KtNode;

#region Types

/// <summary>A type reference.</summary>
public abstract record KtType : KtNode {

  /// <summary>Whether the type admits <c>null</c>, written with a trailing <c>?</c>.</summary>
  public bool Nullable { get; init; }

}

/// <summary>A named type with its type arguments: <c>Int</c>, <c>IntArray</c>, <c>Array&lt;String&gt;</c>, <c>MutableList&lt;Int&gt;</c>.</summary>
/// <param name="Name">The name, possibly qualified.</param>
/// <param name="Arguments">The type arguments; empty for none.</param>
public sealed record KtNamedType(string Name, IReadOnlyList<KtType> Arguments) : KtType {

  /// <summary>A type without arguments.</summary>
  /// <param name="name">The name.</param>
  public KtNamedType(string name) : this(name, []) { }

}

/// <summary>A function type, <c>(Int, Int) -&gt; Int</c>.</summary>
/// <param name="Parameters">The parameter types.</param>
/// <param name="Result">The result type.</param>
public sealed record KtFunctionType(IReadOnlyList<KtType> Parameters, KtType Result) : KtType;

#endregion

#region Expressions

/// <summary>An expression.</summary>
public abstract record KtExpression : KtNode;

/// <summary>
/// A literal as Kotlin spells it: an integer with its suffix (<c>5</c>, <c>5L</c>, <c>5u</c>,
/// <c>5uL</c>), a floating-point number (<c>1.5</c>, <c>1.5f</c>), <c>true</c>, <c>false</c>,
/// <c>null</c> or a character (<c>'a'</c>). Never negative: a minus sign is a <see cref="KtPrefix"/>.
/// </summary>
/// <param name="Text">The spelling.</param>
public sealed record KtLiteral(string Text) : KtExpression;

/// <summary>A string literal in double quotes; the printer escapes what Kotlin needs escaped, <c>$</c> among it.</summary>
/// <param name="Value">The characters.</param>
public sealed record KtString(string Value) : KtExpression;

/// <summary>A simple name: of a variable, a function, a type or a package.</summary>
/// <param name="Name">The name.</param>
public sealed record KtName(string Name) : KtExpression;

/// <summary><c>this</c>.</summary>
public sealed record KtThis : KtExpression;

/// <summary><c>receiver.name</c>, or <c>receiver?.name</c>.</summary>
/// <param name="Receiver">The receiver.</param>
/// <param name="Name">The member.</param>
public sealed record KtMember(KtExpression Receiver, string Name) : KtExpression {

  /// <summary>Whether the access is safe, <c>?.</c>.</summary>
  public bool Safe { get; init; }

}

/// <summary><c>callee&lt;T&gt;(arguments)</c>.</summary>
/// <param name="Callee">What is called.</param>
/// <param name="Arguments">The arguments, in order.</param>
public sealed record KtCall(KtExpression Callee, IReadOnlyList<KtExpression> Arguments) : KtExpression {

  /// <summary>The explicit type arguments; empty for none.</summary>
  public IReadOnlyList<KtType> TypeArguments { get; init; } = [];

  /// <summary>A lambda written after the parentheses, <c>f(a) { x -&gt; … }</c>; <see langword="null"/> for none.</summary>
  public KtLambda? Trailing { get; init; }

}

/// <summary><c>receiver[index]</c>.</summary>
/// <param name="Receiver">The indexed value.</param>
/// <param name="Index">The index.</param>
public sealed record KtIndex(KtExpression Receiver, KtExpression Index) : KtExpression;

/// <summary>A prefix operator: <c>-a</c>, <c>+a</c>, <c>!a</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record KtPrefix(string Operator, KtExpression Operand) : KtExpression;

/// <summary>A postfix operator: <c>a!!</c>, <c>a++</c>, <c>a--</c>.</summary>
/// <param name="Operand">The operand.</param>
/// <param name="Operator">The operator.</param>
public sealed record KtPostfix(KtExpression Operand, string Operator) : KtExpression;

/// <summary>
/// A binary operator: the arithmetic, comparison, equality and logical operators, the range operators
/// <c>..</c> and <c>..&lt;</c>, the elvis <c>?:</c>, <c>in</c> and <c>!in</c>, and the infix functions
/// the backend writes (<c>shl</c>, <c>shr</c>, <c>ushr</c>, <c>and</c>, <c>or</c>, <c>xor</c>,
/// <c>until</c>, <c>downTo</c>, <c>step</c>).
/// </summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">The operator or the name of the infix function.</param>
/// <param name="Right">The right operand.</param>
public sealed record KtBinary(KtExpression Left, string Operator, KtExpression Right) : KtExpression;

/// <summary><c>value is T</c>, or <c>value !is T</c>.</summary>
/// <param name="Value">The tested value.</param>
/// <param name="Type">The type.</param>
/// <param name="Negated">Whether it is <c>!is</c>.</param>
public sealed record KtIs(KtExpression Value, KtType Type, bool Negated) : KtExpression;

/// <summary><c>value as T</c>, or <c>value as? T</c>.</summary>
/// <param name="Value">The converted value.</param>
/// <param name="Type">The type.</param>
/// <param name="Safe">Whether it is <c>as?</c>.</param>
public sealed record KtAs(KtExpression Value, KtType Type, bool Safe) : KtExpression;

/// <summary><c>if (condition) a else b</c> as an expression; each branch one expression.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The value when it holds.</param>
/// <param name="Else">The value when it does not.</param>
public sealed record KtIfExpression(KtExpression Condition, KtExpression Then, KtExpression Else) : KtExpression;

/// <summary><c>throw value</c>, an expression of the type <c>Nothing</c>.</summary>
/// <param name="Value">The exception.</param>
public sealed record KtThrowExpression(KtExpression Value) : KtExpression;

/// <summary>A lambda, <c>{ a: Int, b: Int -&gt; statements }</c>; its value is that of its last statement.</summary>
/// <param name="Parameters">The parameters; empty for none, and then no arrow is written.</param>
/// <param name="Body">The statements.</param>
public sealed record KtLambda(IReadOnlyList<KtParameter> Parameters, IReadOnlyList<KtStatement> Body) : KtExpression;

/// <summary>A callable reference, <c>::name</c>.</summary>
/// <param name="Name">The function.</param>
public sealed record KtReference(string Name) : KtExpression;

#endregion

#region Statements

/// <summary>A statement of a block.</summary>
public abstract record KtStatement : KtNode;

/// <summary>A block, <c>{ statements }</c>.</summary>
/// <param name="Statements">The statements, in order.</param>
public sealed record KtBlock(IReadOnlyList<KtStatement> Statements) : KtStatement;

/// <summary><c>val name: T = value</c> or <c>var name: T = value</c>.</summary>
/// <param name="Mutable">Whether it is a <c>var</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Type">The declared type, or <see langword="null"/> where it is inferred.</param>
/// <param name="Initializer">The first value, or <see langword="null"/> for none.</param>
public sealed record KtVariable(bool Mutable, string Name, KtType? Type, KtExpression? Initializer) : KtStatement;

/// <summary>An assignment, <c>target = value</c>, or a compound one, <c>target += value</c>.</summary>
/// <param name="Target">A name, a member or an indexed element.</param>
/// <param name="Operator">The operator: <c>=</c>, <c>+=</c>, <c>-=</c>, <c>*=</c>, <c>/=</c>, <c>%=</c>.</param>
/// <param name="Value">The value.</param>
public sealed record KtAssignment(KtExpression Target, string Operator, KtExpression Value) : KtStatement;

/// <summary>An expression evaluated for its effect.</summary>
/// <param name="Expression">The expression.</param>
public sealed record KtExpressionStatement(KtExpression Expression) : KtStatement;

/// <summary><c>if (condition) { … } else …</c>; the else branch is a block or another <see cref="KtIf"/>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The block run when it holds.</param>
/// <param name="Else">The block or <see cref="KtIf"/> run when it does not; <see langword="null"/> for none.</param>
public sealed record KtIf(KtExpression Condition, KtBlock Then, KtStatement? Else) : KtStatement;

/// <summary>A loop, <c>label@ while (condition) { … }</c>.</summary>
/// <param name="Label">The label, or <see langword="null"/>.</param>
/// <param name="Condition">The condition tested before every pass.</param>
/// <param name="Body">The body.</param>
public sealed record KtWhile(string? Label, KtExpression Condition, KtBlock Body) : KtStatement;

/// <summary>A loop, <c>label@ do { … } while (condition)</c>; the condition sees the body's variables.</summary>
/// <param name="Label">The label, or <see langword="null"/>.</param>
/// <param name="Body">The body.</param>
/// <param name="Condition">The condition tested after every pass.</param>
public sealed record KtDoWhile(string? Label, KtBlock Body, KtExpression Condition) : KtStatement;

/// <summary>A loop over a range or a collection, <c>label@ for (name in range) { … }</c>.</summary>
/// <param name="Label">The label, or <see langword="null"/>.</param>
/// <param name="Variable">The loop variable, a <c>val</c> of each pass.</param>
/// <param name="Range">What it runs over.</param>
/// <param name="Body">The body.</param>
public sealed record KtFor(string? Label, string Variable, KtExpression Range, KtBlock Body) : KtStatement;

/// <summary>One branch of a <see cref="KtWhen"/>: its conditions, or none for <c>else</c>, and its block.</summary>
/// <param name="Conditions">The values the subject is compared with, or the conditions without a subject; empty for <c>else</c>.</param>
/// <param name="Body">The block.</param>
public sealed record KtWhenEntry(IReadOnlyList<KtExpression> Conditions, KtBlock Body) : KtNode;

/// <summary><c>when (subject) { a, b -&gt; { … } else -&gt; { … } }</c> as a statement.</summary>
/// <param name="Subject">The value compared, or <see langword="null"/> for a <c>when</c> of conditions.</param>
/// <param name="Entries">The branches, the <c>else</c> one last.</param>
public sealed record KtWhen(KtExpression? Subject, IReadOnlyList<KtWhenEntry> Entries) : KtStatement;

/// <summary><c>break</c> or <c>break@label</c>.</summary>
/// <param name="Label">The label, or <see langword="null"/> for the innermost loop.</param>
public sealed record KtBreak(string? Label) : KtStatement;

/// <summary><c>continue</c> or <c>continue@label</c>.</summary>
/// <param name="Label">The label, or <see langword="null"/> for the innermost loop.</param>
public sealed record KtContinue(string? Label) : KtStatement;

/// <summary><c>return</c> or <c>return value</c>.</summary>
/// <param name="Value">The value, or <see langword="null"/>.</param>
public sealed record KtReturn(KtExpression? Value) : KtStatement;

/// <summary><c>throw value</c> as a statement.</summary>
/// <param name="Value">The exception.</param>
public sealed record KtThrow(KtExpression Value) : KtStatement;

/// <summary>One <c>catch (name: Type) { … }</c> of a <see cref="KtTry"/>.</summary>
/// <param name="Name">The name of the caught exception.</param>
/// <param name="Type">Its type.</param>
/// <param name="Body">The handler.</param>
public sealed record KtCatch(string Name, KtType Type, KtBlock Body) : KtNode;

/// <summary><c>try { … } catch … finally { … }</c>.</summary>
/// <param name="Body">The protected block.</param>
/// <param name="Catches">The handlers, in order.</param>
/// <param name="Finally">The block run on every way out, or <see langword="null"/>.</param>
public sealed record KtTry(KtBlock Body, IReadOnlyList<KtCatch> Catches, KtBlock? Finally) : KtStatement;

/// <summary>A function declared in a block, a local function.</summary>
/// <param name="Function">The function.</param>
public sealed record KtLocalFunction(KtFunction Function) : KtStatement;

#endregion

#region Declarations

/// <summary>A declaration of a file or a class, with the comment lines written in front of it.</summary>
public abstract record KtDeclaration : KtNode {

  /// <summary>The modifiers, in the order Kotlin's coding conventions give: <c>private</c>, <c>const</c>, <c>data</c>, …</summary>
  public IReadOnlyList<string> Modifiers { get; init; } = [];

  /// <summary>The lines of the KDoc comment in front, without the comment markers; empty for none.</summary>
  public IReadOnlyList<string> Documentation { get; init; } = [];

  /// <summary>Whether a helper of the backend or a function of the IR library is declared, which O0501 may remove when nothing names it.</summary>
  public bool IsHelper { get; init; }

}

/// <summary>A parameter, <c>name: Type</c>; of a primary constructor also <c>val name: Type</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type; <see langword="null"/> only for a lambda parameter whose type is inferred.</param>
public sealed record KtParameter(string Name, KtType? Type) : KtNode {

  /// <summary>The property a primary constructor declares with it: <c>val</c>, <c>var</c>, or <see langword="null"/> for none.</summary>
  public string? Property { get; init; }

  /// <summary>Its default value, or <see langword="null"/>.</summary>
  public KtExpression? Default { get; init; }

}

/// <summary>
/// A function: <c>fun name(parameters): Result { … }</c>, or with an expression body,
/// <c>fun name(parameters): Result = value</c>.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Result">The result type, or <see langword="null"/> for <c>Unit</c>.</param>
/// <param name="Body">The block body, or <see langword="null"/> where <see cref="Expression"/> is the body.</param>
public sealed record KtFunction(string Name, IReadOnlyList<KtParameter> Parameters, KtType? Result, KtBlock? Body) : KtDeclaration {

  /// <summary>The expression body, or <see langword="null"/> where <see cref="Body"/> is the body.</summary>
  public KtExpression? Expression { get; init; }

}

/// <summary><c>val name: Type = value</c>, <c>var …</c> or <c>const val …</c> of a file, a class or an object.</summary>
/// <param name="Mutable">Whether it is a <c>var</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Type">The type, or <see langword="null"/> where it is inferred.</param>
/// <param name="Initializer">The first value, or <see langword="null"/>.</param>
public sealed record KtProperty(bool Mutable, string Name, KtType? Type, KtExpression? Initializer) : KtDeclaration;

/// <summary>A supertype of a class: a class with the arguments of its constructor, or an interface without.</summary>
/// <param name="Type">The type.</param>
/// <param name="Arguments">The arguments of the constructor called, or <see langword="null"/> for an interface.</param>
public sealed record KtSuperType(KtType Type, IReadOnlyList<KtExpression>? Arguments) : KtNode;

/// <summary>One entry of an <c>enum class</c>: <c>NAME(arguments)</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Arguments">The arguments of the constructor; empty for none.</param>
public sealed record KtEnumEntry(string Name, IReadOnlyList<KtExpression> Arguments) : KtNode;

/// <summary>A secondary constructor, <c>constructor(parameters) : this(arguments) { … }</c>.</summary>
/// <param name="Parameters">The parameters.</param>
/// <param name="Delegation">The constructor called first, <c>this</c> or <c>super</c>, or <see langword="null"/> for none.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Body">The body.</param>
public sealed record KtConstructor(IReadOnlyList<KtParameter> Parameters, string? Delegation, IReadOnlyList<KtExpression> Arguments, KtBlock Body) : KtDeclaration;

/// <summary>An <c>init { … }</c> block of a class.</summary>
/// <param name="Body">The statements.</param>
public sealed record KtInitializer(KtBlock Body) : KtDeclaration;

/// <summary>
/// A class, an interface or an object: <c>class</c>, <c>interface</c>, <c>object</c>,
/// <c>enum class</c>, with the <c>data</c>, <c>open</c> or <c>abstract</c> modifier where it has one.
/// </summary>
/// <param name="Kind">The keyword: <c>class</c>, <c>interface</c>, <c>object</c> or <c>enum class</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Parameters">The parameters of the primary constructor, or <see langword="null"/> for no primary constructor.</param>
/// <param name="SuperTypes">The supertypes.</param>
/// <param name="Members">The members, in order.</param>
public sealed record KtClass(string Kind, string Name, IReadOnlyList<KtParameter>? Parameters, IReadOnlyList<KtSuperType> SuperTypes, IReadOnlyList<KtDeclaration> Members) : KtDeclaration {

  /// <summary>The entries of an <c>enum class</c>; empty for any other.</summary>
  public IReadOnlyList<KtEnumEntry> Entries { get; init; } = [];

}

/// <summary>
/// A file: the header comment lines, the file annotations, the package, the imports and the
/// declarations.
/// </summary>
/// <param name="Header">The lines of the header comment, each written as <c>// line</c>.</param>
/// <param name="Package">The package, or <see langword="null"/> for the default package.</param>
/// <param name="Imports">The imported names, in order.</param>
/// <param name="Declarations">The declarations.</param>
public sealed record KtFile(IReadOnlyList<string> Header, string? Package, IReadOnlyList<string> Imports, IReadOnlyList<KtDeclaration> Declarations) : KtNode {

  /// <summary>The warnings suppressed for the file, <c>@file:Suppress("…")</c>; empty for none.</summary>
  public IReadOnlyList<string> Suppressions { get; init; } = [];

}

#endregion
