using System.Collections.Generic;

namespace Polypiler.Core.Backends.VisualBasic.Syntax;

// The syntax tree of the classic Visual Basic (VB6 / VBA) standard module a module is written as, for
// the subset the backend writes. The emitter builds it from the IR, the target passes rewrite it and
// VbPrinter is the only place that turns it into text; VbSyntaxReader reads that text back. As in the
// Python tree there is no node for parentheses: the printer puts them exactly where the precedence of
// [MS-VBAL] section 5.6.9 needs them, so a tree has one text. Keywords are written in the case the
// Visual Basic editor gives them, names as the emitter chose them.

/// <summary>A node of the Visual Basic syntax tree.</summary>
public abstract record VbNode;

/// <summary>What stands in front of a statement or declaration: empty lines and comments, each a line of its own.</summary>
/// <param name="BlankLines">How many empty lines come first.</param>
/// <param name="Comments">The text of each comment line, without its apostrophe.</param>
public sealed record VbTrivia(int BlankLines, IReadOnlyList<string> Comments) {

  /// <summary>Nothing in front.</summary>
  public static VbTrivia None { get; } = new(0, []);

  /// <summary>One blank line.</summary>
  public static VbTrivia Blank { get; } = new(1, []);

}

#region Expressions

/// <summary>An expression.</summary>
public abstract record VbExpression : VbNode;

/// <summary>
/// An integer literal, written in decimal unless <see cref="Hex"/> is set; a negative one is written
/// with its minus sign. <see cref="Suffix"/> is the type character that fixes the literal's type:
/// <c>%</c> Integer, <c>&amp;</c> Long, <c>^</c> LongLong, or empty for the type the value gives.
/// </summary>
/// <param name="Value">The value.</param>
public sealed record VbInteger(long Value) : VbExpression {

  /// <summary>The type character after the digits, or empty.</summary>
  public string Suffix { get; init; } = "";

  /// <summary>Whether the literal is written <c>&amp;H</c> with eight (Long) or sixteen (LongLong) hexadecimal digits.</summary>
  public bool Hex { get; init; }

}

/// <summary>
/// A finite floating-point literal: a <c>Double</c>, written with <c>#</c> where its digits alone would
/// read as an integer, or a <c>Single</c>, written with <c>!</c>.
/// </summary>
/// <param name="Value">The value.</param>
/// <param name="IsSingle">Whether the literal is a <c>Single</c>.</param>
public sealed record VbFloat(double Value, bool IsSingle) : VbExpression;

/// <summary>A string literal in double quotes; it holds no control character, which the emitter writes as <c>ChrW(n)</c>.</summary>
/// <param name="Value">The characters.</param>
public sealed record VbString(string Value) : VbExpression;

/// <summary><c>True</c> or <c>False</c>.</summary>
/// <param name="Value">The value.</param>
public sealed record VbBoolean(bool Value) : VbExpression;

/// <summary>A name: of a variable, a constant, a procedure or a built-in function.</summary>
/// <param name="Name">The name.</param>
public sealed record VbName(string Name) : VbExpression;

/// <summary><c>target.Name</c>: a field of a user-defined type, or a property such as <c>Err.Number</c>.</summary>
/// <param name="Target">The value.</param>
/// <param name="Name">The member.</param>
public sealed record VbMember(VbExpression Target, string Name) : VbExpression;

/// <summary><c>target(arguments)</c>: the call of a function or the element of an array, which Visual Basic writes alike.</summary>
/// <param name="Target">What is called or indexed.</param>
/// <param name="Arguments">The arguments or indexes.</param>
public sealed record VbCall(VbExpression Target, IReadOnlyList<VbExpression> Arguments) : VbExpression;

/// <summary>A unary operator: <c>-</c> or <c>Not</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record VbUnary(string Operator, VbExpression Operand) : VbExpression;

/// <summary>
/// A binary operator: <c>^ * / \ Mod + - &amp; = &lt;&gt; &lt; &lt;= &gt; &gt;= And Or Xor Eqv Imp</c>.
/// Visual Basic evaluates both operands of every operator, <c>And</c> and <c>Or</c> included.
/// </summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">The operator.</param>
/// <param name="Right">The right operand.</param>
public sealed record VbBinary(VbExpression Left, string Operator, VbExpression Right) : VbExpression;

#endregion

#region Types

/// <summary>A type as a declaration writes it after <c>As</c>: a built-in type or the name of a user-defined type.</summary>
/// <param name="Name">The name of the type, such as <c>Long</c>.</param>
public sealed record VbTypeName(string Name) : VbNode;

/// <summary>The bounds of one dimension of an array declaration: <c>lower To upper</c>.</summary>
/// <param name="Lower">The lower bound.</param>
/// <param name="Upper">The upper bound.</param>
public sealed record VbBounds(VbExpression Lower, VbExpression Upper) : VbNode;

/// <summary>
/// A variable as <c>Dim</c>, a field of a <c>Type</c> or a module variable declares it: a scalar
/// (<c>x As Long</c>), a dynamic array (<c>a() As Long</c>) or a fixed one (<c>t(0 To 3) As Long</c>).
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type of the variable or of its elements.</param>
public sealed record VbVariable(string Name, VbTypeName Type) : VbNode {

  /// <summary>Whether the variable is a dynamic array, written with empty parentheses.</summary>
  public bool IsArray { get; init; }

  /// <summary>The bounds of a fixed array; empty for a scalar or a dynamic array.</summary>
  public IReadOnlyList<VbBounds> Bounds { get; init; } = [];

}

#endregion

#region Statements

/// <summary>A statement of a procedure.</summary>
public abstract record VbStatement : VbNode {

  /// <summary>Layout: the blank lines and comments in front.</summary>
  public VbTrivia Leading { get; init; } = VbTrivia.None;

}

/// <summary><c>Dim a As T, b() As U</c>.</summary>
/// <param name="Variables">The variables.</param>
public sealed record VbDim(IReadOnlyList<VbVariable> Variables) : VbStatement;

/// <summary><c>Static a As T</c>: a variable that keeps its value between calls.</summary>
/// <param name="Variables">The variables.</param>
public sealed record VbStatic(IReadOnlyList<VbVariable> Variables) : VbStatement;

/// <summary><c>target = value</c>, where the target is a variable, an element or a field.</summary>
/// <param name="Target">What is assigned.</param>
/// <param name="Value">The value.</param>
public sealed record VbAssign(VbExpression Target, VbExpression Value) : VbStatement;

/// <summary><c>Name a, b</c>: the call of a procedure whose result, if any, is discarded.</summary>
/// <param name="Target">The procedure, a name or a member such as <c>Err.Raise</c>.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record VbCallStatement(VbExpression Target, IReadOnlyList<VbExpression> Arguments) : VbStatement;

/// <summary>One condition of an <c>If</c> block with what it guards: the <c>If</c> or an <c>ElseIf</c>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Body">The statements.</param>
public sealed record VbIfBranch(VbExpression Condition, IReadOnlyList<VbStatement> Body) : VbNode;

/// <summary>A block <c>If … ElseIf … Else … End If</c>.</summary>
/// <param name="Branches">The <c>If</c> and every <c>ElseIf</c>, in order; at least one.</param>
/// <param name="Else">The statements after <c>Else</c>, or <see langword="null"/> for none.</param>
public sealed record VbIf(IReadOnlyList<VbIfBranch> Branches, IReadOnlyList<VbStatement>? Else) : VbStatement;

/// <summary>A clause of a <c>Case</c>: one value, a range <c>a To b</c>, or <c>Is op value</c>.</summary>
/// <param name="Value">The value, or the lower bound of a range.</param>
public sealed record VbCaseClause(VbExpression Value) : VbNode {

  /// <summary>The upper bound of a range, or <see langword="null"/>.</summary>
  public VbExpression? Upper { get; init; }

  /// <summary>The comparison of an <c>Is</c> clause, such as <c>&gt;</c>, or <see langword="null"/>.</summary>
  public string? Is { get; init; }

}

/// <summary>A <c>Case</c> of a <c>Select Case</c>.</summary>
/// <param name="Clauses">The clauses, separated by commas.</param>
/// <param name="Body">The statements.</param>
public sealed record VbCase(IReadOnlyList<VbCaseClause> Clauses, IReadOnlyList<VbStatement> Body) : VbNode;

/// <summary><c>Select Case value … Case Else … End Select</c>.</summary>
/// <param name="Value">The value tested.</param>
/// <param name="Cases">The cases in order.</param>
/// <param name="Else">The statements of <c>Case Else</c>, or <see langword="null"/>.</param>
public sealed record VbSelect(VbExpression Value, IReadOnlyList<VbCase> Cases, IReadOnlyList<VbStatement>? Else) : VbStatement;

/// <summary><c>For v = start To end [Step step] … Next v</c>; the end and the step are evaluated once.</summary>
/// <param name="Variable">The counter.</param>
/// <param name="Start">The first value.</param>
/// <param name="End">The last value.</param>
/// <param name="Step">The step, or <see langword="null"/> for one.</param>
/// <param name="Body">The statements.</param>
public sealed record VbFor(string Variable, VbExpression Start, VbExpression End, VbExpression? Step, IReadOnlyList<VbStatement> Body) : VbStatement;

/// <summary>The test of a <c>Do</c> loop: <c>While c</c> or <c>Until c</c>.</summary>
/// <param name="Until">Whether the loop ends when the condition holds; otherwise it runs while it holds.</param>
/// <param name="Condition">The condition.</param>
public sealed record VbLoopTest(bool Until, VbExpression Condition) : VbNode;

/// <summary><c>Do [While|Until c] … Loop [While|Until c]</c>; at most one of the tests is given.</summary>
/// <param name="Pre">The test after <c>Do</c>, or <see langword="null"/>.</param>
/// <param name="Post">The test after <c>Loop</c>, or <see langword="null"/>.</param>
/// <param name="Body">The statements.</param>
public sealed record VbDoLoop(VbLoopTest? Pre, VbLoopTest? Post, IReadOnlyList<VbStatement> Body) : VbStatement;

/// <summary><c>Exit Do</c>, <c>Exit For</c>, <c>Exit Function</c> or <c>Exit Sub</c>.</summary>
/// <param name="Kind">What is left: <c>Do</c>, <c>For</c>, <c>Function</c> or <c>Sub</c>.</param>
public sealed record VbExit(string Kind) : VbStatement;

/// <summary><c>GoTo label</c>.</summary>
/// <param name="Label">The label.</param>
public sealed record VbGoTo(string Label) : VbStatement;

/// <summary>A line label, <c>name:</c>, written at the start of a line of its own.</summary>
/// <param name="Name">The label.</param>
public sealed record VbLabel(string Name) : VbStatement;

/// <summary><c>On Error GoTo label</c>, <c>On Error GoTo 0</c> (<see cref="Label"/> is <c>0</c>) or <c>On Error Resume Next</c> (<see cref="Label"/> is <see langword="null"/>).</summary>
/// <param name="Label">The handler, <c>0</c>, or <see langword="null"/> for <c>Resume Next</c>.</param>
public sealed record VbOnError(string? Label) : VbStatement;

/// <summary><c>Resume</c>, <c>Resume Next</c> (<see cref="Target"/> <c>Next</c>) or <c>Resume label</c>.</summary>
/// <param name="Target">Empty, <c>Next</c> or a label.</param>
public sealed record VbResume(string Target) : VbStatement;

/// <summary><c>ReDim [Preserve] name(lower To upper)</c> of a dynamic array.</summary>
/// <param name="Preserve">Whether the elements are kept.</param>
/// <param name="Target">The array: a variable or a field.</param>
/// <param name="Bounds">The bounds of its one dimension.</param>
public sealed record VbReDim(bool Preserve, VbExpression Target, VbBounds Bounds) : VbStatement;

/// <summary><c>Erase name</c>: a dynamic array without elements again.</summary>
/// <param name="Target">The array.</param>
public sealed record VbErase(VbExpression Target) : VbStatement;

#endregion

#region Declarations

/// <summary>A declaration at the level of the module.</summary>
public abstract record VbDeclaration : VbNode {

  /// <summary>Layout: the blank lines and comments in front.</summary>
  public VbTrivia Leading { get; init; } = VbTrivia.None;

}

/// <summary><c>Option Explicit</c>, <c>Option Base 0</c> and the like.</summary>
/// <param name="Text">What follows <c>Option</c>.</param>
public sealed record VbOption(string Text) : VbDeclaration;

/// <summary><c>Public Type Name … End Type</c>, a user-defined type.</summary>
/// <param name="IsPublic">Whether it is <c>Public</c>; otherwise <c>Private</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Fields">The fields in order.</param>
public sealed record VbTypeDeclaration(bool IsPublic, string Name, IReadOnlyList<VbVariable> Fields) : VbDeclaration;

/// <summary><c>Private Const NAME As T = value</c>.</summary>
/// <param name="IsPublic">Whether it is <c>Public</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Type">The type.</param>
/// <param name="Value">The value, a constant expression.</param>
public sealed record VbConst(bool IsPublic, string Name, VbTypeName Type, VbExpression Value) : VbDeclaration;

/// <summary><c>Private x As T</c> at the level of the module.</summary>
/// <param name="IsPublic">Whether it is <c>Public</c>.</param>
/// <param name="Variable">The variable.</param>
public sealed record VbModuleVariable(bool IsPublic, VbVariable Variable) : VbDeclaration;

/// <summary>A parameter: <c>ByVal x As Long</c>, or <c>a() As Long</c>, which an array is, passed by reference.</summary>
/// <param name="ByVal">Whether it is passed by value; otherwise <c>ByRef</c>, which is written only where it is not the default.</param>
/// <param name="Name">The name.</param>
/// <param name="Type">The type of the parameter or of its elements.</param>
public sealed record VbParameter(bool ByVal, string Name, VbTypeName Type) : VbNode {

  /// <summary>Whether the parameter is an array.</summary>
  public bool IsArray { get; init; }

}

/// <summary>
/// <c>Public Function Name(…) As T … End Function</c> or <c>Sub</c>; the result of a function is what
/// was last assigned to its name.
/// </summary>
/// <param name="IsPublic">Whether it is <c>Public</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Returns">The result type of a function, or <see langword="null"/> for a <c>Sub</c>.</param>
/// <param name="Body">The statements.</param>
public sealed record VbProcedure(bool IsPublic, string Name, IReadOnlyList<VbParameter> Parameters, VbTypeName? Returns, IReadOnlyList<VbStatement> Body) : VbDeclaration {

  /// <summary>Whether the result is an array, written <c>As T()</c>.</summary>
  public bool ReturnsArray { get; init; }

  /// <summary>Layout: whether the procedure is a helper of the backend or copied in from the libraries, which a pass may remove when nothing calls it.</summary>
  public bool IsHelper { get; init; }

  /// <summary>Whether the procedure is a <c>Function</c>.</summary>
  public bool IsFunction => this.Returns is not null;

}

/// <summary>A standard module: <c>Attribute VB_Name</c>, a comment header and the declarations.</summary>
/// <param name="Name">The module name.</param>
/// <param name="Header">The comment lines at the top, each without its apostrophe.</param>
/// <param name="Declarations">The options and declarations in order.</param>
public sealed record VbModule(string Name, IReadOnlyList<string> Header, IReadOnlyList<VbDeclaration> Declarations) : VbNode;

#endregion
