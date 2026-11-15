using System.Collections.Generic;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python.Syntax;

// The syntax tree of the Python a module is written as, shaped after the nodes of CPython's `ast`
// module for the subset the backend writes. The emitter builds it from the IR, the target passes
// rewrite it and PythonPrinter is the only place that turns it into text. As in the C# and the
// JavaScript tree, the properties that say how a node is laid out change no meaning and are ignored
// where two trees are compared. Unlike those trees it has no node for parentheses: the printer puts
// them exactly where Python's precedence needs them, so a tree has one text.

/// <summary>A node of the Python syntax tree.</summary>
public abstract record PyNode;

/// <summary>What stands in front of a statement: empty lines and comments, each a line of its own.</summary>
/// <param name="BlankLines">How many empty lines come first.</param>
/// <param name="Comments">The text of each comment line, without its <c>#</c>.</param>
public sealed record PyTrivia(int BlankLines, IReadOnlyList<string> Comments) {

  /// <summary>Nothing in front.</summary>
  public static PyTrivia None { get; } = new(0, []);

  /// <summary>One blank line.</summary>
  public static PyTrivia Blank { get; } = new(1, []);

  /// <summary>Two blank lines, which PEP 8 puts between definitions at the level of the module.</summary>
  public static PyTrivia TwoBlank { get; } = new(2, []);

}

#region Expressions

/// <summary>An expression.</summary>
public abstract record PyExpression : PyNode {

  /// <summary>The IR type the expression was written for, or <see langword="null"/> where none is known, as in a helper's source.</summary>
  public IrType? Type { get; init; }

}

/// <summary>An integer literal; a negative one is written with its minus sign, which binds as a unary operator does.</summary>
/// <param name="Value">The value.</param>
public sealed record PyInt(BigInteger Value) : PyExpression {

  /// <summary>Layout: the spelling, such as <c>0xFFFF_FFFF</c>; the printer uses it while it still denotes <see cref="Value"/>.</summary>
  public string? Raw { get; init; }

}

/// <summary>
/// A finite floating-point literal, written as the shortest digits that read back as the same double;
/// a negative one is written with its minus sign. Infinities and NaNs are no literals in Python: the
/// emitter writes them as <c>math.inf</c> and <c>math.nan</c>.
/// </summary>
/// <param name="Value">The value.</param>
public sealed record PyFloat(double Value) : PyExpression;

/// <summary><c>True</c> or <c>False</c>.</summary>
/// <param name="Value">The value.</param>
public sealed record PyBool(bool Value) : PyExpression;

/// <summary><c>None</c>.</summary>
public sealed record PyNone : PyExpression;

/// <summary>A string literal, written in double quotes.</summary>
/// <param name="Value">The characters.</param>
public sealed record PyString(string Value) : PyExpression;

/// <summary>A name: of a variable, a function, a module or a built-in.</summary>
/// <param name="Name">The name.</param>
public sealed record PyName(string Name) : PyExpression;

/// <summary><c>target.name</c>.</summary>
/// <param name="Target">The object.</param>
/// <param name="Name">The attribute.</param>
public sealed record PyAttribute(PyExpression Target, string Name) : PyExpression;

/// <summary><c>target[index]</c>.</summary>
/// <param name="Target">The sequence.</param>
/// <param name="Index">The index.</param>
public sealed record PySubscript(PyExpression Target, PyExpression Index) : PyExpression;

/// <summary><c>callee(arguments)</c>.</summary>
/// <param name="Callee">What is called.</param>
/// <param name="Arguments">The positional arguments.</param>
public sealed record PyCall(PyExpression Callee, IReadOnlyList<PyExpression> Arguments) : PyExpression;

/// <summary>A prefix operator: <c>-</c>, <c>+</c>, <c>~</c> or <c>not</c>.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record PyUnary(string Operator, PyExpression Operand) : PyExpression;

/// <summary>An arithmetic, bitwise or shift operator: <c>+ - * / // % ** &lt;&lt; &gt;&gt; &amp; | ^</c>.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Operator">The operator.</param>
/// <param name="Right">The right operand.</param>
public sealed record PyBinary(PyExpression Left, string Operator, PyExpression Right) : PyExpression;

/// <summary>
/// A comparison, possibly chained: <c>a &lt; b &lt;= c</c> compares each neighbouring pair and
/// evaluates every operand at most once.
/// </summary>
/// <param name="Operands">Two operands or more.</param>
/// <param name="Operators">One operator per neighbouring pair: <c>&lt; &lt;= &gt; &gt;= == != in</c>.</param>
public sealed record PyCompare(IReadOnlyList<PyExpression> Operands, IReadOnlyList<string> Operators) : PyExpression;

/// <summary><c>left and right</c> or <c>left or right</c>: the right operand is evaluated only when the left one does not decide.</summary>
/// <param name="Left">The operand evaluated first.</param>
/// <param name="Operator"><c>and</c> or <c>or</c>.</param>
/// <param name="Right">The operand evaluated second, if at all.</param>
public sealed record PyBoolOp(PyExpression Left, string Operator, PyExpression Right) : PyExpression;

/// <summary><c>whenTrue if test else whenFalse</c>.</summary>
/// <param name="Test">The condition, evaluated first.</param>
/// <param name="WhenTrue">The value when it holds.</param>
/// <param name="WhenFalse">The value when it does not.</param>
public sealed record PyConditional(PyExpression Test, PyExpression WhenTrue, PyExpression WhenFalse) : PyExpression;

/// <summary>An assignment expression, <c>(name := value)</c> (Python 3.8); its value is the value assigned.</summary>
/// <param name="Name">The variable.</param>
/// <param name="Value">The value.</param>
public sealed record PyNamedExpression(string Name, PyExpression Value) : PyExpression;

/// <summary>A tuple display, <c>(a, b)</c>; the elements are evaluated left to right.</summary>
/// <param name="Elements">The elements.</param>
public sealed record PyTuple(IReadOnlyList<PyExpression> Elements) : PyExpression {

  /// <summary>Layout: the elements are written eight to a line, as a table.</summary>
  public bool Tabulated { get; init; }

}

/// <summary>A list display, <c>[a, b]</c>; the elements are evaluated left to right.</summary>
/// <param name="Elements">The elements.</param>
public sealed record PyList(IReadOnlyList<PyExpression> Elements) : PyExpression {

  /// <summary>Layout: the elements are written eight to a line, as a table.</summary>
  public bool Tabulated { get; init; }

}

#endregion

#region Statements

/// <summary>A statement.</summary>
public abstract record PyStatement : PyNode {

  /// <summary>Layout: the blank lines and comments in front of the statement.</summary>
  public PyTrivia Leading { get; init; } = PyTrivia.None;

}

/// <summary>An expression evaluated for its effect.</summary>
/// <param name="Expression">The expression.</param>
public sealed record PyExpressionStatement(PyExpression Expression) : PyStatement;

/// <summary><c>target = value</c>, or <c>target: annotation = value</c> where the target is a name.</summary>
/// <param name="Target">A name or a subscript.</param>
/// <param name="Value">The value.</param>
public sealed record PyAssign(PyExpression Target, PyExpression Value) : PyStatement {

  /// <summary>The type the name is annotated with, or <see langword="null"/>.</summary>
  public PyExpression? Annotation { get; init; }

}

/// <summary><c>name: annotation</c>: a variable declared with a type and no value.</summary>
/// <param name="Name">The variable.</param>
/// <param name="Annotation">The type.</param>
public sealed record PyAnnotation(string Name, PyExpression Annotation) : PyStatement;

/// <summary><c>return</c> or <c>return value</c>.</summary>
/// <param name="Value">The value, or <see langword="null"/>.</param>
public sealed record PyReturn(PyExpression? Value) : PyStatement;

/// <summary><c>pass</c>.</summary>
public sealed record PyPass : PyStatement;

/// <summary><c>break</c>.</summary>
public sealed record PyBreak : PyStatement;

/// <summary><c>continue</c>.</summary>
public sealed record PyContinue : PyStatement;

/// <summary><c>raise exception</c>.</summary>
/// <param name="Exception">The exception raised.</param>
public sealed record PyRaise(PyExpression Exception) : PyStatement;

/// <summary><c>global a, b</c>: the names a function assigns at the level of the module.</summary>
/// <param name="Names">The names.</param>
public sealed record PyGlobal(IReadOnlyList<string> Names) : PyStatement;

/// <summary><c>import module</c>.</summary>
/// <param name="Module">The module imported.</param>
public sealed record PyImport(string Module) : PyStatement;

/// <summary><c>from module import a, b</c>.</summary>
/// <param name="Module">The module.</param>
/// <param name="Names">The names imported.</param>
public sealed record PyImportFrom(string Module, IReadOnlyList<string> Names) : PyStatement;

/// <summary>
/// <c>if test:</c> with its body and an <c>else:</c>; an <c>else</c> that holds one <c>if</c> is
/// printed as <c>elif</c>.
/// </summary>
/// <param name="Test">The condition.</param>
/// <param name="Body">The statements when it holds.</param>
/// <param name="Else">The statements when it does not; empty for none.</param>
public sealed record PyIf(PyExpression Test, IReadOnlyList<PyStatement> Body, IReadOnlyList<PyStatement> Else) : PyStatement;

/// <summary><c>while test:</c>.</summary>
/// <param name="Test">The condition, tested before every pass.</param>
/// <param name="Body">The statements of one pass.</param>
public sealed record PyWhile(PyExpression Test, IReadOnlyList<PyStatement> Body) : PyStatement;

/// <summary><c>for variable in iterable:</c>.</summary>
/// <param name="Variable">The loop variable.</param>
/// <param name="Iterable">What it runs over, evaluated once.</param>
/// <param name="Body">The statements of one pass.</param>
public sealed record PyFor(string Variable, PyExpression Iterable, IReadOnlyList<PyStatement> Body) : PyStatement;

/// <summary>
/// One <c>case</c> of a <see cref="PyMatchStatement"/>: literal patterns joined by <c>|</c>, or the
/// wildcard <c>_</c> when there are none, and an optional guard.
/// </summary>
/// <param name="Patterns">The literals; empty for the wildcard.</param>
/// <param name="Guard">The condition after <c>if</c>, or <see langword="null"/>.</param>
/// <param name="Body">The statements of the case.</param>
public sealed record PyMatchCase(IReadOnlyList<PyExpression> Patterns, PyExpression? Guard, IReadOnlyList<PyStatement> Body) : PyNode;

/// <summary><c>match subject:</c> with its cases, tested in order (Python 3.10).</summary>
/// <param name="Subject">The value matched, evaluated once.</param>
/// <param name="Cases">The cases.</param>
public sealed record PyMatchStatement(PyExpression Subject, IReadOnlyList<PyMatchCase> Cases) : PyStatement;

/// <summary>A parameter of a function: its name and type.</summary>
/// <param name="Name">The name.</param>
/// <param name="Annotation">The type.</param>
public sealed record PyParameter(string Name, PyExpression Annotation) : PyNode;

/// <summary><c>def name(parameters) -&gt; returns:</c> with its body.</summary>
/// <param name="Name">The name.</param>
/// <param name="Parameters">The parameters, in call order.</param>
/// <param name="Returns">The type of the result; <c>None</c> for a function without one.</param>
/// <param name="Body">The statements.</param>
public sealed record PyFunctionDef(string Name, IReadOnlyList<PyParameter> Parameters, PyExpression Returns, IReadOnlyList<PyStatement> Body) : PyStatement {

  /// <summary>Whether the function is a helper the backend or legalization added, which the target passes may remove.</summary>
  public bool IsHelper { get; init; }

}

#endregion

/// <summary>A file: the comment lines of its header, then its statements.</summary>
/// <param name="Header">The comment lines in front, without their <c>#</c>.</param>
/// <param name="Body">The statements at the level of the module.</param>
public sealed record PyModule(IReadOnlyList<string> Header, IReadOnlyList<PyStatement> Body) : PyNode;
