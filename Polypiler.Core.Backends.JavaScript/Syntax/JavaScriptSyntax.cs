using System.Collections.Generic;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript.Syntax;

// The syntax tree of the JavaScript a module is written as (#1336), shaped after ESTree. The emitter
// builds it from the IR, the target passes rewrite it and JavaScriptPrinter is the only place that
// turns it into text. As in the C# tree, the properties that say how a node is laid out change no
// meaning and are ignored where two trees are compared, and parentheses are nodes of their own.

/// <summary>A node of the JavaScript syntax tree.</summary>
public abstract record JsNode;

/// <summary>What stands in front of a statement: empty lines and comments, each a line of its own.</summary>
/// <param name="BlankLines">How many empty lines come first.</param>
/// <param name="Comments">The text of each comment line, without its <c>//</c>.</param>
public sealed record JsTrivia(int BlankLines, IReadOnlyList<string> Comments) {

  /// <summary>Nothing in front.</summary>
  public static JsTrivia None { get; } = new(0, []);

  /// <summary>A blank line and nothing else.</summary>
  public static JsTrivia Blank { get; } = new(1, []);

}

#region Expressions

/// <summary>An expression.</summary>
public abstract record JsExpression : JsNode {

  /// <summary>The IR type the expression was written for, or <see langword="null"/> where none is known, as in a helper's source.</summary>
  public IrType? Type { get; init; }

}

/// <summary>
/// A number: a numeric literal, <c>NaN</c>, <c>Infinity</c>, or a negative one written with its minus
/// sign, which binds as a unary operator does.
/// </summary>
/// <param name="Value">The value.</param>
public sealed record JsNumber(double Value) : JsExpression {

  /// <summary>The spelling, such as <c>0xFF</c> or the digits of 2^63; the printer uses it while it still denotes <see cref="Value"/>.</summary>
  public string? Raw { get; init; }

}

/// <summary>A <c>BigInt</c> literal, <c>5n</c>; a negative one is written with its minus sign.</summary>
/// <param name="Value">The value.</param>
public sealed record JsBigInt(BigInteger Value) : JsExpression {

  /// <summary>The spelling, such as <c>0xFFFFFFFFn</c>; the printer uses it while it still denotes <see cref="Value"/>.</summary>
  public string? Raw { get; init; }

}

/// <summary>A string literal, written in double quotes.</summary>
/// <param name="Value">The characters.</param>
public sealed record JsString(string Value) : JsExpression;

/// <summary><c>true</c> or <c>false</c>.</summary>
/// <param name="Value">The value.</param>
public sealed record JsBoolean(bool Value) : JsExpression;

/// <summary>A name: of a variable, a function or a global.</summary>
/// <param name="Name">The name.</param>
public sealed record JsIdentifier(string Name) : JsExpression;

/// <summary><c>target.property</c>.</summary>
/// <param name="Target">The object.</param>
/// <param name="Property">The name of the property.</param>
public sealed record JsMember(JsExpression Target, string Property) : JsExpression;

/// <summary><c>target[index]</c>.</summary>
/// <param name="Target">The object.</param>
/// <param name="Index">The key.</param>
public sealed record JsIndex(JsExpression Target, JsExpression Index) : JsExpression;

/// <summary><c>callee(arguments)</c>.</summary>
/// <param name="Callee">What is called.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record JsCall(JsExpression Callee, IReadOnlyList<JsExpression> Arguments) : JsExpression;

/// <summary><c>new callee(arguments)</c>.</summary>
/// <param name="Callee">The constructor.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record JsNew(JsExpression Callee, IReadOnlyList<JsExpression> Arguments) : JsExpression;

/// <summary>A prefix operator other than a step: <c>-</c>, <c>+</c>, <c>!</c>, <c>~</c>, <c>typeof</c>, <c>void</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record JsUnary(string Operator, JsExpression Operand) : JsExpression;

/// <summary><c>++x</c>, <c>--x</c>, <c>x++</c> or <c>x--</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Prefix">Whether the operator stands in front.</param>
/// <param name="Operand">The variable.</param>
public sealed record JsUpdate(string Operator, bool Prefix, JsExpression Operand) : JsExpression;

/// <summary>A binary or a logical operator.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">The operator.</param>
/// <param name="Right">The right operand.</param>
public sealed record JsBinary(JsExpression Left, string Operator, JsExpression Right) : JsExpression;

/// <summary>An assignment, simple or compound.</summary>
/// <param name="Left">The place assigned to.</param>
/// <param name="Operator">The operator: <c>=</c>, <c>+=</c>, <c>&gt;&gt;&gt;=</c> and so on.</param>
/// <param name="Right">The value.</param>
public sealed record JsAssignment(JsExpression Left, string Operator, JsExpression Right) : JsExpression;

/// <summary><c>test ? whenTrue : whenFalse</c>.</summary>
/// <param name="Test">The condition.</param>
/// <param name="WhenTrue">The value when it holds.</param>
/// <param name="WhenFalse">The value when it does not.</param>
public sealed record JsConditional(JsExpression Test, JsExpression WhenTrue, JsExpression WhenFalse) : JsExpression {

  /// <summary>Layout: the <c>?</c> starts a line of its own, indented one step beyond the statement.</summary>
  public bool BreakBeforeQuestion { get; init; }

  /// <summary>Layout: the <c>:</c> starts a line of its own, indented one step beyond the statement.</summary>
  public bool BreakBeforeColon { get; init; }

}

/// <summary>Expressions separated by commas, evaluated in order: the value is the last one's.</summary>
/// <param name="Expressions">The expressions.</param>
public sealed record JsSequence(IReadOnlyList<JsExpression> Expressions) : JsExpression;

/// <summary>An expression in parentheses.</summary>
/// <param name="Inner">The expression.</param>
public sealed record JsParenthesized(JsExpression Inner) : JsExpression;

/// <summary><c>[elements]</c>.</summary>
/// <param name="Elements">The elements.</param>
public sealed record JsArray(IReadOnlyList<JsExpression> Elements) : JsExpression {

  /// <summary>Layout: more than a row of elements are written a row of eight to a line.</summary>
  public bool Tabulated { get; init; }

}

/// <summary>A property of an object literal: <c>key: value</c>, or the key alone where it is the name of its value.</summary>
/// <param name="Key">The key.</param>
/// <param name="Value">The value.</param>
/// <param name="Shorthand">Whether only the key is written.</param>
public sealed record JsProperty(string Key, JsExpression Value, bool Shorthand = false) : JsNode;

/// <summary><c>{ properties }</c>.</summary>
/// <param name="Properties">The properties.</param>
public sealed record JsObject(IReadOnlyList<JsProperty> Properties) : JsExpression {

  /// <summary>Layout: each property stands on a line of its own.</summary>
  public bool Multiline { get; init; }

}

/// <summary><c>function name(parameters) { body }</c> as an expression.</summary>
/// <param name="Name">The name, or <see langword="null"/>.</param>
/// <param name="Parameters">The names of the parameters.</param>
/// <param name="Body">The body.</param>
public sealed record JsFunctionExpression(string? Name, IReadOnlyList<string> Parameters, JsBlock Body) : JsExpression;

#endregion

#region Statements

/// <summary>A statement.</summary>
public abstract record JsStatement : JsNode {

  /// <summary>Layout: what stands in front of the statement.</summary>
  public JsTrivia Leading { get; init; } = JsTrivia.None;

}

/// <summary><c>{ statements }</c>.</summary>
/// <param name="Statements">The statements.</param>
public sealed record JsBlock(IReadOnlyList<JsStatement> Statements) : JsStatement {

  /// <summary>Layout: the block stands on one line.</summary>
  public bool Inline { get; init; }

}

/// <summary>One variable of a declaration, with its initial value.</summary>
/// <param name="Name">The name.</param>
/// <param name="Initializer">The initial value, or <see langword="null"/>.</param>
public sealed record JsDeclarator(string Name, JsExpression? Initializer) : JsNode;

/// <summary><c>var</c>, <c>let</c> or <c>const</c> with its variables.</summary>
/// <param name="Kind">The keyword.</param>
/// <param name="Declarators">The variables.</param>
public sealed record JsVariableDeclaration(string Kind, IReadOnlyList<JsDeclarator> Declarators) : JsStatement {

  /// <summary>Whether the backend declares the variable in support of the module's code, as a helper; a target pass may remove it.</summary>
  public bool IsHelper { get; init; }

}

/// <summary>An expression evaluated as a statement.</summary>
/// <param name="Expression">The expression.</param>
public sealed record JsExpressionStatement(JsExpression Expression) : JsStatement;

/// <summary><c>if (test) then else otherwise</c>.</summary>
/// <param name="Test">The condition.</param>
/// <param name="Then">The statement when it holds.</param>
/// <param name="Else">The statement when it does not, or <see langword="null"/>.</param>
public sealed record JsIf(JsExpression Test, JsStatement Then, JsStatement? Else) : JsStatement {

  /// <summary>Layout: an <c>else</c> after a statement that is no block stays on its line.</summary>
  public bool ElseInline { get; init; }

}

/// <summary><c>while (test) body</c>.</summary>
/// <param name="Test">The condition.</param>
/// <param name="Body">The body.</param>
public sealed record JsWhile(JsExpression Test, JsStatement Body) : JsStatement;

/// <summary><c>do body while (test);</c>.</summary>
/// <param name="Body">The body.</param>
/// <param name="Test">The condition.</param>
public sealed record JsDoWhile(JsStatement Body, JsExpression Test) : JsStatement;

/// <summary><c>for (init; test; update) body</c>.</summary>
/// <param name="Declaration">The variables the loop declares, or <see langword="null"/>.</param>
/// <param name="Test">The condition, or <see langword="null"/>.</param>
/// <param name="Update">What runs after each pass, or <see langword="null"/>.</param>
/// <param name="Body">The body.</param>
public sealed record JsFor(JsVariableDeclaration? Declaration, JsExpression? Test, JsExpression? Update, JsStatement Body) : JsStatement;

/// <summary>A case of a switch: its label and its statements.</summary>
/// <param name="Test">The value of the <c>case</c>, or <see langword="null"/> for <c>default</c>.</param>
/// <param name="Consequent">The statements.</param>
public sealed record JsSwitchCase(JsExpression? Test, IReadOnlyList<JsStatement> Consequent) : JsNode;

/// <summary><c>switch (discriminant) { cases }</c>.</summary>
/// <param name="Discriminant">The value.</param>
/// <param name="Cases">The cases.</param>
public sealed record JsSwitch(JsExpression Discriminant, IReadOnlyList<JsSwitchCase> Cases) : JsStatement;

/// <summary><c>break;</c>.</summary>
public sealed record JsBreak : JsStatement;

/// <summary><c>continue;</c>.</summary>
public sealed record JsContinue : JsStatement;

/// <summary><c>return value;</c>.</summary>
/// <param name="Value">The value, or <see langword="null"/>.</param>
public sealed record JsReturn(JsExpression? Value) : JsStatement;

/// <summary><c>throw value;</c>.</summary>
/// <param name="Value">The exception.</param>
public sealed record JsThrow(JsExpression Value) : JsStatement;

/// <summary><c>function name(parameters) { body }</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Parameters">The names of the parameters.</param>
/// <param name="Body">The body.</param>
public sealed record JsFunctionDeclaration(string Name, IReadOnlyList<string> Parameters, JsBlock Body) : JsStatement {

  /// <summary>Whether the backend declares the function in support of the module's code: a helper, which a target pass may inline or remove.</summary>
  public bool IsHelper { get; init; }

}

/// <summary><c>export</c> in front of a declaration.</summary>
/// <param name="Declaration">The declaration.</param>
public sealed record JsExport(JsStatement Declaration) : JsStatement;

/// <summary>A file: the lines of its header comment and its statements.</summary>
/// <param name="Header">The header, each line without its <c>//</c>.</param>
/// <param name="Body">The statements.</param>
/// <param name="IsModule">Whether the file is an ES module rather than a script.</param>
public sealed record JsProgram(IReadOnlyList<string> Header, IReadOnlyList<JsStatement> Body, bool IsModule) : JsNode;

#endregion
