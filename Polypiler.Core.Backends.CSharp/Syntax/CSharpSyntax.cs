using System.Collections.Generic;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp.Syntax;

// The syntax tree of the C# a module is written as (#1336). The emitter builds it from the IR, the
// target passes rewrite it and CSharpPrinter is the only place that turns it into text. A node says
// what the code is; the few properties that say how it is laid out - a block on one line, a line
// break before an operator, a blank line before a statement, the spelling of a literal in a helper's
// source - change no meaning, are ignored where two trees are compared and are kept by a rewrite
// only on the nodes it does not replace. Parentheses are nodes of their own: the emitter writes them
// where it always has, and the printer adds those that a rewritten tree needs and lacks.

/// <summary>A node of the C# syntax tree.</summary>
public abstract record CsNode;

/// <summary>What stands in front of a statement or a member: a blank line and comments, each a line of its own.</summary>
/// <param name="BlankLine">Whether an empty line comes first.</param>
/// <param name="Comments">The text of each comment line, without its <c>//</c>.</param>
public sealed record CsTrivia(bool BlankLine, IReadOnlyList<string> Comments) {

  /// <summary>Nothing in front.</summary>
  public static CsTrivia None { get; } = new(false, []);

  /// <summary>A blank line and nothing else.</summary>
  public static CsTrivia Blank { get; } = new(true, []);

}

/// <summary>A type as C# spells it where a type stands: <c>int</c>, <c>uint[]</c>, <c>global::System.ReadOnlySpan&lt;byte&gt;</c>.</summary>
/// <param name="Name">The spelling.</param>
public sealed record CsType(string Name) : CsNode {

  /// <summary>The word <c>var</c>, which leaves the type of a local to the compiler.</summary>
  public static CsType Var { get; } = new("var");

  /// <summary>The type <c>void</c>.</summary>
  public static CsType Void { get; } = new("void");

}

#region Expressions

/// <summary>An expression.</summary>
public abstract record CsExpression : CsNode {

  /// <summary>The IR type the expression was written for, or <see langword="null"/> where none is known, as in a helper's source.</summary>
  public IrType? Type { get; init; }

}

/// <summary>The suffix of an integer literal, which gives it its type.</summary>
public enum CsIntegerSuffix {

  /// <summary>No suffix: the first of <c>int</c>, <c>uint</c>, <c>long</c> and <c>ulong</c> that holds the value.</summary>
  None,

  /// <summary><c>u</c>.</summary>
  U,

  /// <summary><c>L</c>.</summary>
  L,

  /// <summary><c>UL</c>.</summary>
  UL

}

/// <summary>An integer literal; a negative value is written with its minus sign, which binds as a unary operator does.</summary>
/// <param name="Value">The value.</param>
/// <param name="Suffix">The suffix.</param>
public sealed record CsIntegerLiteral(BigInteger Value, CsIntegerSuffix Suffix = CsIntegerSuffix.None) : CsExpression {

  /// <summary>The spelling of a helper's source, such as <c>0xFFu</c>; the printer uses it while it still denotes <see cref="Value"/>.</summary>
  public string? Raw { get; init; }

}

/// <summary>A floating-point literal of a finite value; a negative value is written with its minus sign.</summary>
/// <param name="Value">The value.</param>
/// <param name="IsSingle">Whether it is a <c>float</c>, written with the suffix <c>f</c>.</param>
public sealed record CsRealLiteral(double Value, bool IsSingle = false) : CsExpression {

  /// <summary>The spelling of a helper's source; the printer uses it while it still denotes <see cref="Value"/>.</summary>
  public string? Raw { get; init; }

}

/// <summary><c>true</c> or <c>false</c>.</summary>
/// <param name="Value">The value.</param>
public sealed record CsBooleanLiteral(bool Value) : CsExpression;

/// <summary>A string literal.</summary>
/// <param name="Value">The characters.</param>
public sealed record CsStringLiteral(string Value) : CsExpression;

/// <summary>The literal <c>default</c>, which takes the type of the place it goes to.</summary>
public sealed record CsDefaultLiteral : CsExpression;

/// <summary>
/// A name: of a variable, a member, a type in an expression (<c>int</c>, <c>EqualityComparer&lt;T&gt;</c>),
/// the alias <c>global::System</c>, <c>this</c> or the discard <c>_</c>; spelled with its <c>@</c> where it needs one.
/// </summary>
/// <param name="Identifier">The spelling.</param>
public sealed record CsName(string Identifier) : CsExpression;

/// <summary><c>target.Member</c>.</summary>
/// <param name="Target">What the member is read from.</param>
/// <param name="Member">The name of the member.</param>
public sealed record CsMemberAccess(CsExpression Target, string Member) : CsExpression;

/// <summary><c>target(arguments)</c>.</summary>
/// <param name="Target">What is called.</param>
/// <param name="Arguments">The arguments, in their order.</param>
public sealed record CsInvocation(CsExpression Target, IReadOnlyList<CsExpression> Arguments) : CsExpression;

/// <summary>An argument passed by reference, <c>out value</c>; it stands only in an argument list.</summary>
/// <param name="Value">The variable, or the discard.</param>
public sealed record CsOutArgument(CsExpression Value) : CsExpression;

/// <summary><c>target[index]</c>.</summary>
/// <param name="Target">The array.</param>
/// <param name="Index">The index.</param>
public sealed record CsElementAccess(CsExpression Target, CsExpression Index) : CsExpression;

/// <summary><c>(T)operand</c>.</summary>
/// <param name="TargetType">The type cast to.</param>
/// <param name="Operand">The operand.</param>
public sealed record CsCast(CsType TargetType, CsExpression Operand) : CsExpression;

/// <summary>A prefix operator: <c>-</c>, <c>+</c>, <c>!</c>, <c>~</c>, <c>++</c> or <c>--</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record CsPrefixUnary(string Operator, CsExpression Operand) : CsExpression;

/// <summary>A postfix operator: <c>++</c> or <c>--</c>.</summary>
/// <param name="Operand">The operand.</param>
/// <param name="Operator">The operator.</param>
public sealed record CsPostfixUnary(CsExpression Operand, string Operator) : CsExpression;

/// <summary>A binary operator other than an assignment.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">The operator.</param>
/// <param name="Right">The right operand.</param>
public sealed record CsBinary(CsExpression Left, string Operator, CsExpression Right) : CsExpression {

  /// <summary>Layout: the operator starts a line of its own, indented one step beyond the statement.</summary>
  public bool BreakBefore { get; init; }

}

/// <summary><c>operand is T</c>.</summary>
/// <param name="Operand">What is tested.</param>
/// <param name="TestedType">The type.</param>
public sealed record CsIsType(CsExpression Operand, CsType TestedType) : CsExpression;

/// <summary>An assignment, simple or compound: <c>=</c>, <c>+=</c>, <c>&lt;&lt;=</c> and so on.</summary>
/// <param name="Left">The variable assigned to.</param>
/// <param name="Operator">The operator.</param>
/// <param name="Right">The value.</param>
public sealed record CsAssignment(CsExpression Left, string Operator, CsExpression Right) : CsExpression;

/// <summary><c>condition ? whenTrue : whenFalse</c>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="WhenTrue">The value when it holds.</param>
/// <param name="WhenFalse">The value when it does not.</param>
public sealed record CsConditional(CsExpression Condition, CsExpression WhenTrue, CsExpression WhenFalse) : CsExpression {

  /// <summary>Layout: the <c>?</c> starts a line of its own.</summary>
  public bool BreakBeforeQuestion { get; init; }

  /// <summary>Layout: the <c>:</c> starts a line of its own.</summary>
  public bool BreakBeforeColon { get; init; }

}

/// <summary>An expression in parentheses.</summary>
/// <param name="Inner">The expression.</param>
public sealed record CsParenthesized(CsExpression Inner) : CsExpression;

/// <summary><c>checked(inner)</c> or <c>unchecked(inner)</c>.</summary>
/// <param name="Checked">Whether overflow is checked.</param>
/// <param name="Inner">The expression.</param>
public sealed record CsCheckedExpression(bool Checked, CsExpression Inner) : CsExpression;

/// <summary><c>new T(arguments)</c>.</summary>
/// <param name="CreatedType">The type.</param>
/// <param name="Arguments">The arguments of the constructor.</param>
public sealed record CsObjectCreation(CsType CreatedType, IReadOnlyList<CsExpression> Arguments) : CsExpression;

/// <summary><c>new T[size]</c>, or <c>new T[] { elements }</c> with an initializer.</summary>
/// <param name="ElementType">The type of the elements.</param>
/// <param name="Size">The number of elements, or <see langword="null"/> when the initializer says it.</param>
/// <param name="Initializer">The elements, or <see langword="null"/>.</param>
public sealed record CsArrayCreation(CsType ElementType, CsExpression? Size, CsArrayInitializer? Initializer) : CsExpression;

/// <summary><c>{ elements }</c>: the elements of an array.</summary>
/// <param name="Elements">The elements.</param>
public sealed record CsArrayInitializer(IReadOnlyList<CsExpression> Elements) : CsExpression {

  /// <summary>Layout: more than a row of elements are written a row of eight to a line.</summary>
  public bool Tabulated { get; init; }

}

/// <summary><c>default(T)</c>.</summary>
/// <param name="DefaultType">The type.</param>
public sealed record CsDefaultExpression(CsType DefaultType) : CsExpression;

#endregion

#region Statements

/// <summary>A statement.</summary>
public abstract record CsStatement : CsNode {

  /// <summary>Layout: what stands in front of the statement.</summary>
  public CsTrivia Leading { get; init; } = CsTrivia.None;

}

/// <summary><c>{ statements }</c>.</summary>
/// <param name="Statements">The statements.</param>
public sealed record CsBlock(IReadOnlyList<CsStatement> Statements) : CsStatement {

  /// <summary>Layout: the block stands on one line.</summary>
  public bool Inline { get; init; }

}

/// <summary>The declaration of one local, <c>T name = initializer;</c>.</summary>
/// <param name="LocalType">The type, or <see cref="CsType.Var"/>.</param>
/// <param name="Name">The name.</param>
/// <param name="Initializer">The initial value, or <see langword="null"/>.</param>
public sealed record CsLocalDeclaration(CsType LocalType, string Name, CsExpression? Initializer) : CsStatement;

/// <summary>An expression evaluated as a statement.</summary>
/// <param name="Expression">The expression.</param>
public sealed record CsExpressionStatement(CsExpression Expression) : CsStatement;

/// <summary><c>if (condition) then else otherwise</c>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The statement when it holds.</param>
/// <param name="Else">The statement when it does not, or <see langword="null"/>.</param>
public sealed record CsIf(CsExpression Condition, CsStatement Then, CsStatement? Else) : CsStatement;

/// <summary><c>while (condition) body</c>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Body">The body.</param>
public sealed record CsWhile(CsExpression Condition, CsStatement Body) : CsStatement;

/// <summary><c>do body while (condition);</c>.</summary>
/// <param name="Body">The body.</param>
/// <param name="Condition">The condition.</param>
public sealed record CsDo(CsStatement Body, CsExpression Condition) : CsStatement;

/// <summary><c>for (declaration; condition; incrementors) body</c>.</summary>
/// <param name="Declaration">The local the loop declares, or <see langword="null"/>.</param>
/// <param name="Condition">The condition, or <see langword="null"/>.</param>
/// <param name="Incrementors">What runs after each pass.</param>
/// <param name="Body">The body.</param>
public sealed record CsFor(CsLocalDeclaration? Declaration, CsExpression? Condition, IReadOnlyList<CsExpression> Incrementors, CsStatement Body) : CsStatement;

/// <summary><c>switch (value) { sections }</c>.</summary>
/// <param name="Value">The value.</param>
/// <param name="Sections">The sections.</param>
public sealed record CsSwitch(CsExpression Value, IReadOnlyList<CsSwitchSection> Sections) : CsStatement;

/// <summary>A section of a switch: its labels, each on a line, and its statements.</summary>
/// <param name="Labels">The value of each <c>case</c>, <see langword="null"/> for <c>default</c>.</param>
/// <param name="Statements">The statements.</param>
public sealed record CsSwitchSection(IReadOnlyList<CsExpression?> Labels, IReadOnlyList<CsStatement> Statements) : CsNode;

/// <summary><c>break;</c>.</summary>
public sealed record CsBreak : CsStatement;

/// <summary><c>continue;</c>.</summary>
public sealed record CsContinue : CsStatement;

/// <summary><c>return value;</c>.</summary>
/// <param name="Value">The value, or <see langword="null"/>.</param>
public sealed record CsReturn(CsExpression? Value) : CsStatement;

/// <summary><c>throw value;</c>.</summary>
/// <param name="Value">The exception.</param>
public sealed record CsThrow(CsExpression Value) : CsStatement;

/// <summary><c>checked { }</c> or <c>unchecked { }</c>.</summary>
/// <param name="Checked">Whether overflow is checked.</param>
/// <param name="Block">The block.</param>
public sealed record CsCheckedStatement(bool Checked, CsBlock Block) : CsStatement;

#endregion

#region Declarations

/// <summary>A declaration of a namespace, a type or a member of a type.</summary>
public abstract record CsMember : CsNode {

  /// <summary>Layout: what stands in front of the declaration.</summary>
  public CsTrivia Leading { get; init; } = CsTrivia.None;

}

/// <summary>A parameter, <c>T name</c> or <c>out T name</c>.</summary>
/// <param name="ParameterType">The type.</param>
/// <param name="Name">The name.</param>
/// <param name="Modifier">The modifier, such as <c>out</c>, or <see langword="null"/>.</param>
public sealed record CsParameter(CsType ParameterType, string Name, string? Modifier = null) : CsNode;

/// <summary>A field: <c>modifiers T name = initializer;</c>.</summary>
/// <param name="Modifiers">The modifiers, in their order.</param>
/// <param name="FieldType">The type.</param>
/// <param name="Name">The name.</param>
/// <param name="Initializer">The initial value, or <see langword="null"/>.</param>
public sealed record CsField(IReadOnlyList<string> Modifiers, CsType FieldType, string Name, CsExpression? Initializer) : CsMember;

/// <summary>
/// A method or an operator (whose name is <c>operator ==</c>): a block body, or an expression body
/// <c>=&gt; expression;</c>.
/// </summary>
/// <param name="Modifiers">The modifiers, in their order.</param>
/// <param name="ReturnType">The return type.</param>
/// <param name="Name">The name.</param>
/// <param name="TypeParameters">The type parameters, such as <c>T</c>; empty for none.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Body">The body, or <see langword="null"/> for an expression body.</param>
/// <param name="ExpressionBody">The expression body, or <see langword="null"/>.</param>
public sealed record CsMethod(
  IReadOnlyList<string> Modifiers, CsType ReturnType, string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<CsParameter> Parameters,
  CsBlock? Body, CsExpression? ExpressionBody) : CsMember;

/// <summary>A constructor.</summary>
/// <param name="Modifiers">The modifiers.</param>
/// <param name="Name">The name of the type.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Body">The body.</param>
public sealed record CsConstructor(IReadOnlyList<string> Modifiers, string Name, IReadOnlyList<CsParameter> Parameters, CsBlock Body) : CsMember;

/// <summary>
/// A type: a <c>class</c>, a <c>struct</c> or a <c>record struct</c>, with the parameters of a record
/// and its base types; a record without members ends in a semicolon.
/// </summary>
/// <param name="Modifiers">The modifiers.</param>
/// <param name="Keyword">The kind: <c>class</c>, <c>struct</c> or <c>record struct</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Parameters">The parameters of a record, or <see langword="null"/>.</param>
/// <param name="BaseTypes">The base types.</param>
/// <param name="Members">The members, or <see langword="null"/> for none and a semicolon.</param>
public sealed record CsTypeDeclaration(
  IReadOnlyList<string> Modifiers, string Keyword, string Name, IReadOnlyList<CsParameter>? Parameters, IReadOnlyList<CsType> BaseTypes,
  IReadOnlyList<CsMember>? Members) : CsMember {

  /// <summary>Layout: an empty line follows the opening brace whatever the first member asks for.</summary>
  public bool OpenBlank { get; init; }

}

/// <summary>A namespace: one that holds the rest of the file, <c>namespace N;</c>, or a block.</summary>
/// <param name="Name">The name.</param>
/// <param name="FileScoped">Whether it is file-scoped.</param>
/// <param name="Members">The types in it.</param>
public sealed record CsNamespace(string Name, bool FileScoped, IReadOnlyList<CsMember> Members) : CsMember;

/// <summary>A file: the lines of its header comment and its declarations.</summary>
/// <param name="Header">The header, each line without its <c>//</c>.</param>
/// <param name="Members">The declarations.</param>
public sealed record CsCompilationUnit(IReadOnlyList<string> Header, IReadOnlyList<CsMember> Members) : CsNode;

#endregion
