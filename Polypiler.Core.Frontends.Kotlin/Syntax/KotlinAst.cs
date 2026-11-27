using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Kotlin.Syntax;

// The syntax tree of the Kotlin the frontend imports, after the grammar of the Kotlin language
// specification (kotlinlang.org/spec, "Syntax and grammar") for the subset it reads. Every node
// knows where it starts, line and column, so that a diagnostic names the place.

/// <summary>A position in the source: 1-based line and column.</summary>
/// <param name="Line">The line.</param>
/// <param name="Column">The column, in UTF-16 code units.</param>
public readonly record struct KPosition(int Line, int Column) {

  /// <summary>The position as a diagnostic writes it, <c>line:column</c>.</summary>
  public override string ToString() => $"{this.Line}:{this.Column}";

}

/// <summary>A node of the tree.</summary>
public abstract record KNode {

  /// <summary>Where the node starts.</summary>
  public KPosition At { get; init; }

}

/// <summary>A type reference: <c>Int</c>, <c>IntArray</c>, <c>Array&lt;Int&gt;</c>, <c>Int?</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Arguments">The type arguments.</param>
/// <param name="Nullable">Whether it is written with <c>?</c>.</param>
public sealed record KTypeRef(string Name, IReadOnlyList<KTypeRef> Arguments, bool Nullable) : KNode;

#region Expressions

/// <summary>An expression.</summary>
public abstract record KExpression : KNode;

/// <summary>An integer literal, its digits read in their radix: the value and the suffixes <c>u</c> and <c>L</c>.</summary>
/// <param name="Value">The value, below 2^64.</param>
/// <param name="Unsigned">Whether it has the suffix <c>u</c>.</param>
/// <param name="Long">Whether it has the suffix <c>L</c>.</param>
public sealed record KInteger(ulong Value, bool Unsigned, bool Long) : KExpression;

/// <summary>A floating-point literal: its value and whether it is a <c>Float</c> (<c>f</c>).</summary>
/// <param name="Value">The value, rounded as the type rounds it.</param>
/// <param name="Single">Whether it has the suffix <c>f</c>.</param>
public sealed record KFloat(double Value, bool Single) : KExpression;

/// <summary><c>true</c> or <c>false</c>.</summary>
/// <param name="Value">The value.</param>
public sealed record KBoolean(bool Value) : KExpression;

/// <summary>A character literal: its code unit.</summary>
/// <param name="Value">The code unit.</param>
public sealed record KChar(char Value) : KExpression;

/// <summary>A string literal without templates.</summary>
/// <param name="Value">The characters.</param>
public sealed record KString(string Value) : KExpression;

/// <summary>A string template: literal parts and expressions, in order.</summary>
/// <param name="Parts">A <see cref="KString"/> for a literal part, any other expression for <c>$name</c> or <c>${…}</c>.</param>
public sealed record KTemplate(IReadOnlyList<KExpression> Parts) : KExpression;

/// <summary><c>null</c>.</summary>
public sealed record KNull : KExpression;

/// <summary>A simple name.</summary>
/// <param name="Name">The name.</param>
public sealed record KName(string Name) : KExpression;

/// <summary><c>receiver.name</c>, or <c>receiver?.name</c>.</summary>
/// <param name="Receiver">The receiver.</param>
/// <param name="Name">The member.</param>
/// <param name="Safe">Whether it is <c>?.</c>.</param>
public sealed record KMember(KExpression Receiver, string Name, bool Safe) : KExpression;

/// <summary><c>callee(arguments)</c>, with a trailing lambda where one follows.</summary>
/// <param name="Callee">What is called.</param>
/// <param name="Arguments">The arguments.</param>
/// <param name="Trailing">The lambda after the parentheses, or <see langword="null"/>.</param>
public sealed record KCall(KExpression Callee, IReadOnlyList<KExpression> Arguments, KLambda? Trailing) : KExpression;

/// <summary><c>receiver[index]</c>.</summary>
/// <param name="Receiver">The indexed value.</param>
/// <param name="Index">The index.</param>
public sealed record KIndex(KExpression Receiver, KExpression Index) : KExpression;

/// <summary>A prefix operator: <c>-</c>, <c>+</c>, <c>!</c>, <c>++</c>, <c>--</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record KPrefix(string Operator, KExpression Operand) : KExpression;

/// <summary>A postfix operator: <c>++</c>, <c>--</c>, <c>!!</c>.</summary>
/// <param name="Operand">The operand.</param>
/// <param name="Operator">The operator.</param>
public sealed record KPostfix(KExpression Operand, string Operator) : KExpression;

/// <summary>A binary operator, or an infix function call such as <c>a shl b</c>.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">The operator or the name of the infix function.</param>
/// <param name="Right">The right operand.</param>
public sealed record KBinary(KExpression Left, string Operator, KExpression Right) : KExpression;

/// <summary><c>value is T</c>, <c>value !is T</c>.</summary>
public sealed record KIs(KExpression Value, KTypeRef Type, bool Negated) : KExpression;

/// <summary><c>value as T</c>, <c>value as? T</c>.</summary>
public sealed record KAs(KExpression Value, KTypeRef Type, bool Safe) : KExpression;

/// <summary><c>if (condition) then else otherwise</c>; as a statement the else branch may be missing.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The branch taken when it holds: a block or one expression.</param>
/// <param name="Else">The other branch, or <see langword="null"/>.</param>
public sealed record KIf(KExpression Condition, KNode Then, KNode? Else) : KExpression;

/// <summary>One branch of a <c>when</c>.</summary>
/// <param name="Conditions">The conditions: values compared with the subject, <see cref="KWhenRange"/> tests, or truth values without a subject; empty for <c>else</c>.</param>
/// <param name="Body">The branch: a block or one expression.</param>
public sealed record KWhenEntry(IReadOnlyList<KExpression> Conditions, KNode Body) : KNode;

/// <summary>A condition <c>in range</c> or <c>!in range</c> of a <c>when</c> with a subject.</summary>
/// <param name="Range">The range.</param>
/// <param name="Negated">Whether it is <c>!in</c>.</param>
public sealed record KWhenRange(KExpression Range, bool Negated) : KExpression;

/// <summary><c>when (subject) { … }</c> or <c>when { … }</c>.</summary>
/// <param name="Subject">The subject, or <see langword="null"/>.</param>
/// <param name="Entries">The branches.</param>
public sealed record KWhen(KExpression? Subject, IReadOnlyList<KWhenEntry> Entries) : KExpression;

/// <summary>A lambda, <c>{ a, b -&gt; … }</c>.</summary>
/// <param name="Parameters">The parameters; empty where the lambda declares none.</param>
/// <param name="Body">The statements.</param>
public sealed record KLambda(IReadOnlyList<KParameter> Parameters, IReadOnlyList<KStatement> Body) : KExpression;

/// <summary><c>throw value</c>.</summary>
/// <param name="Value">The exception.</param>
public sealed record KThrowExpression(KExpression Value) : KExpression;

/// <summary><c>return value</c>, <c>break</c>, <c>continue</c> where they stand as an expression, after an elvis.</summary>
/// <param name="Jump">The jump.</param>
public sealed record KJumpExpression(KStatement Jump) : KExpression;

#endregion

#region Statements

/// <summary>A statement.</summary>
public abstract record KStatement : KNode;

/// <summary>A block of statements.</summary>
/// <param name="Statements">The statements.</param>
public sealed record KBlock(IReadOnlyList<KStatement> Statements) : KStatement;

/// <summary><c>val name: T = value</c> or <c>var …</c>.</summary>
public sealed record KVariable(bool Mutable, string Name, KTypeRef? Type, KExpression? Initializer) : KStatement;

/// <summary><c>target = value</c> or a compound assignment.</summary>
public sealed record KAssignment(KExpression Target, string Operator, KExpression Value) : KStatement;

/// <summary>An expression evaluated for its effect.</summary>
public sealed record KExpressionStatement(KExpression Expression) : KStatement;

/// <summary><c>label@ while (condition) { … }</c>.</summary>
public sealed record KWhile(string? Label, KExpression Condition, KNode Body) : KStatement;

/// <summary><c>label@ do { … } while (condition)</c>.</summary>
public sealed record KDoWhile(string? Label, KNode Body, KExpression Condition) : KStatement;

/// <summary><c>label@ for (name in range) { … }</c>.</summary>
public sealed record KFor(string? Label, string Variable, KExpression Range, KNode Body) : KStatement;

/// <summary><c>break</c>, <c>break@label</c>.</summary>
public sealed record KBreak(string? Label) : KStatement;

/// <summary><c>continue</c>, <c>continue@label</c>.</summary>
public sealed record KContinue(string? Label) : KStatement;

/// <summary><c>return</c>, <c>return value</c>.</summary>
public sealed record KReturn(KExpression? Value) : KStatement;

/// <summary>A local function.</summary>
public sealed record KLocalFunction(KFunction Function) : KStatement;

#endregion

#region Declarations

/// <summary>A declaration of a file.</summary>
public abstract record KDeclaration : KNode {

  /// <summary>The modifiers.</summary>
  public IReadOnlyList<string> Modifiers { get; init; } = [];

}

/// <summary>A parameter, <c>name: Type = default</c>.</summary>
public sealed record KParameter(string Name, KTypeRef? Type, KExpression? Default) : KNode;

/// <summary>A function.</summary>
/// <param name="Name">The name.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Result">The declared result type, or <see langword="null"/>.</param>
/// <param name="Body">The block body, or <see langword="null"/> where <see cref="Expression"/> is the body.</param>
/// <param name="Expression">The expression body, or <see langword="null"/>.</param>
public sealed record KFunction(string Name, IReadOnlyList<KParameter> Parameters, KTypeRef? Result, KBlock? Body, KExpression? Expression) : KDeclaration;

/// <summary>A property of the file: <c>val</c>, <c>var</c> or <c>const val</c>.</summary>
public sealed record KProperty(bool Mutable, string Name, KTypeRef? Type, KExpression? Initializer) : KDeclaration;

/// <summary>A declaration the frontend parses only to refuse it: a class, an interface, an object, a type alias.</summary>
/// <param name="Kind">The keyword.</param>
/// <param name="Name">The name.</param>
public sealed record KUnsupportedDeclaration(string Kind, string Name) : KDeclaration;

/// <summary>A file.</summary>
/// <param name="Package">The package, or <see langword="null"/>.</param>
/// <param name="Imports">The imported names.</param>
/// <param name="Declarations">The declarations.</param>
public sealed record KFile(string? Package, IReadOnlyList<string> Imports, IReadOnlyList<KDeclaration> Declarations) : KNode;

#endregion
