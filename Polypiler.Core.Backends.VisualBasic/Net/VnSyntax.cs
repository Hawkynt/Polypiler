using System.Collections.Generic;

namespace Polypiler.Core.Backends.VisualBasic.Net;

// The syntax tree of the Visual Basic .NET source file a module is written as, for the subset the
// backend writes. The emitter builds it from the IR and VnPrinter is the only place that turns it into
// text. As in the classic tree there is no node for parentheses: the printer puts them exactly where
// the operator precedence of the Visual Basic language reference ("Operator Precedence in Visual
// Basic") needs them. Types are written as their names, such as Integer() or System.OverflowException.

/// <summary>A node of the Visual Basic .NET syntax tree.</summary>
public abstract record VnNode;

/// <summary>An expression.</summary>
public abstract record VnExpression : VnNode;

/// <summary>A literal or a name of the framework written as given, such as <c>5UI</c>, <c>&amp;H80000000</c> or <c>Double.NaN</c>.</summary>
/// <param name="Text">The text.</param>
/// <param name="IsConstant">Whether the text is a constant expression, which a <c>Const</c> may hold.</param>
public sealed record VnLiteral(string Text, bool IsConstant = true) : VnExpression;

/// <summary>A string literal in double quotes; it holds no control character, which the emitter writes through <c>PlyChr</c>.</summary>
/// <param name="Value">The characters.</param>
public sealed record VnString(string Value) : VnExpression;

/// <summary>A name: of a variable, a constant, a procedure or a type.</summary>
/// <param name="Name">The name.</param>
public sealed record VnName(string Name) : VnExpression;

/// <summary><c>target.Name</c>: a field, a property or a method of a value or a type.</summary>
/// <param name="Target">The value or type.</param>
/// <param name="Name">The member.</param>
public sealed record VnMember(VnExpression Target, string Name) : VnExpression;

/// <summary><c>target(arguments)</c>: the call of a procedure or the element of an array, which Visual Basic writes alike.</summary>
/// <param name="Target">What is called or indexed.</param>
/// <param name="Arguments">The arguments or the index.</param>
public sealed record VnInvoke(VnExpression Target, IReadOnlyList<VnExpression> Arguments) : VnExpression;

/// <summary>A unary operator: <c>-</c> or <c>Not</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record VnUnary(string Operator, VnExpression Operand) : VnExpression;

/// <summary>
/// A binary operator: <c>^ * / \ Mod + - &amp; &lt;&lt; &gt;&gt; = &lt;&gt; &lt; &lt;= &gt; &gt;= Is IsNot And AndAlso Or OrElse Xor</c>.
/// </summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">The operator.</param>
/// <param name="Right">The right operand.</param>
public sealed record VnBinary(VnExpression Left, string Operator, VnExpression Right) : VnExpression;

/// <summary><c>If(condition, whenTrue, whenFalse)</c>, which evaluates the condition and one of the two values.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="WhenTrue">The value when it holds.</param>
/// <param name="WhenFalse">The value when it does not.</param>
public sealed record VnTernary(VnExpression Condition, VnExpression WhenTrue, VnExpression WhenFalse) : VnExpression;

/// <summary><c>New Type(arguments)</c>.</summary>
/// <param name="Type">The type.</param>
/// <param name="Arguments">The arguments of the constructor.</param>
public sealed record VnNew(string Type, IReadOnlyList<VnExpression> Arguments) : VnExpression;

/// <summary><c>New Element(upper) {}</c> of an upper bound, or <c>New Element() {a, b}</c> of its elements.</summary>
/// <param name="Element">The type of the elements.</param>
/// <param name="Upper">The upper bound, or <see langword="null"/> for an array of the elements given.</param>
/// <param name="Elements">The elements, when there is no upper bound.</param>
public sealed record VnNewArray(string Element, VnExpression? Upper, IReadOnlyList<VnExpression> Elements) : VnExpression;

/// <summary>A statement of a procedure.</summary>
public abstract record VnStatement : VnNode;

/// <summary><c>Dim name As Type = value</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type.</param>
/// <param name="Value">The first value.</param>
public sealed record VnDim(string Name, string Type, VnExpression Value) : VnStatement;

/// <summary><c>target = value</c>, or a compound assignment such as <c>target += value</c>.</summary>
/// <param name="Target">A variable, an element or a field.</param>
/// <param name="Operator">The operator: <c>=</c>, <c>+=</c>, <c>-=</c>.</param>
/// <param name="Value">The value.</param>
public sealed record VnAssign(VnExpression Target, string Operator, VnExpression Value) : VnStatement;

/// <summary>The call of a procedure whose result, if any, is discarded.</summary>
/// <param name="Call">The call.</param>
public sealed record VnCallStatement(VnExpression Call) : VnStatement;

/// <summary><c>If c Then … Else … End If</c>; an <c>Else</c> that holds one <c>If</c> alone is written <c>ElseIf</c>.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="Then">The statements when it holds.</param>
/// <param name="Else">The statements when it does not, or <see langword="null"/>.</param>
public sealed record VnIf(VnExpression Condition, IReadOnlyList<VnStatement> Then, IReadOnlyList<VnStatement>? Else) : VnStatement;

/// <summary>A <c>Case</c> of a <c>Select Case</c>: its values, separated by commas.</summary>
/// <param name="Labels">The values.</param>
/// <param name="Body">The statements.</param>
public sealed record VnCase(IReadOnlyList<VnExpression> Labels, IReadOnlyList<VnStatement> Body) : VnNode;

/// <summary><c>Select Case value … Case Else … End Select</c>.</summary>
/// <param name="Value">The value tested.</param>
/// <param name="Cases">The cases in order.</param>
/// <param name="Else">The statements of <c>Case Else</c>, or <see langword="null"/>.</param>
public sealed record VnSelect(VnExpression Value, IReadOnlyList<VnCase> Cases, IReadOnlyList<VnStatement>? Else) : VnStatement;

/// <summary><c>Do While c … Loop</c>, <c>Do … Loop While c</c> (<see cref="Post"/>), or <c>Do … Loop</c> without a condition.</summary>
/// <param name="Condition">The condition that keeps the loop running, or <see langword="null"/>.</param>
/// <param name="Post">Whether the condition is tested after each pass.</param>
/// <param name="Body">The statements.</param>
public sealed record VnDo(VnExpression? Condition, bool Post, IReadOnlyList<VnStatement> Body) : VnStatement;

/// <summary><c>Exit Do</c>, <c>Exit Select</c>, <c>Exit Function</c> or <c>Exit Sub</c>.</summary>
/// <param name="Kind">What is left.</param>
public sealed record VnExit(string Kind) : VnStatement;

/// <summary><c>Continue Do</c>.</summary>
public sealed record VnContinue : VnStatement;

/// <summary><c>Return</c> or <c>Return value</c>.</summary>
/// <param name="Value">The value, or <see langword="null"/>.</param>
public sealed record VnReturn(VnExpression? Value) : VnStatement;

/// <summary><c>Throw value</c>, or <c>Throw</c> alone, which throws the exception caught again.</summary>
/// <param name="Value">The exception, or <see langword="null"/>.</param>
public sealed record VnThrow(VnExpression? Value) : VnStatement;

/// <summary><c>Catch name As Type When filter</c>.</summary>
/// <param name="Name">The variable.</param>
/// <param name="Type">The class caught.</param>
/// <param name="When">The filter, or <see langword="null"/>.</param>
/// <param name="Body">The statements.</param>
public sealed record VnCatch(string Name, string Type, VnExpression? When, IReadOnlyList<VnStatement> Body) : VnNode;

/// <summary><c>Try … Catch … Finally … End Try</c>.</summary>
/// <param name="Body">The protected statements.</param>
/// <param name="Catches">The catch clauses in order.</param>
/// <param name="Finally">The statements of <c>Finally</c>, or <see langword="null"/>.</param>
public sealed record VnTry(IReadOnlyList<VnStatement> Body, IReadOnlyList<VnCatch> Catches, IReadOnlyList<VnStatement>? Finally) : VnStatement;

/// <summary>A line label, <c>name:</c>.</summary>
/// <param name="Name">The label.</param>
public sealed record VnLabel(string Name) : VnStatement;

/// <summary><c>GoTo label</c>.</summary>
/// <param name="Label">The label.</param>
public sealed record VnGoTo(string Label) : VnStatement;

/// <summary>A declaration in a module or a structure.</summary>
public abstract record VnDeclaration : VnNode;

/// <summary><c>Private Const N As T = v</c>, <c>Private ReadOnly N As T = v</c>, <c>Public N As T</c> and the like.</summary>
/// <param name="Modifiers">The modifiers, such as <c>Private Const</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Type">The type.</param>
/// <param name="Value">The initial value, or <see langword="null"/>.</param>
public sealed record VnField(string Modifiers, string Name, string Type, VnExpression? Value) : VnDeclaration;

/// <summary>A parameter: <c>ByVal x As Integer</c> or <c>ByRef x As Integer</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type.</param>
/// <param name="ByRef">Whether it is passed by reference.</param>
public sealed record VnParameter(string Name, string Type, bool ByRef = false) : VnNode;

/// <summary>
/// <c>Public Function Name(…) As T … End Function</c>, a <c>Sub</c> when <see cref="Returns"/> is
/// <see langword="null"/>, or the constructor <c>Sub New</c>.
/// </summary>
/// <param name="Modifiers">The modifiers, such as <c>Public</c> or <c>Private</c>.</param>
/// <param name="Name">The name; <c>New</c> for a constructor.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Returns">The result type, or <see langword="null"/> for a <c>Sub</c>.</param>
/// <param name="Body">The statements.</param>
public sealed record VnMethod(string Modifiers, string Name, IReadOnlyList<VnParameter> Parameters, string? Returns, IReadOnlyList<VnStatement> Body) : VnDeclaration {

  /// <summary>The type parameter of a generic helper, such as <c>T</c>, or <see langword="null"/>.</summary>
  public string? TypeParameter { get; init; }

}

/// <summary><c>Public Structure Name … End Structure</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Members">The fields and the constructor.</param>
public sealed record VnStructure(string Name, IReadOnlyList<VnDeclaration> Members) : VnDeclaration;

/// <summary>A source file: the comment header, the options, the imports and one module.</summary>
/// <param name="Name">The module name.</param>
/// <param name="Header">The comment lines at the top, each without its apostrophe.</param>
/// <param name="Members">The declarations of the module in order.</param>
public sealed record VnModule(string Name, IReadOnlyList<string> Header, IReadOnlyList<VnDeclaration> Members) : VnNode;
