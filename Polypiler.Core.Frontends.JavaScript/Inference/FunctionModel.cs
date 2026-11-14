using System.Collections.Generic;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>The conversion JavaScript applies to a value where it stands, so that only the value modulo a power of two matters.</summary>
internal enum Coercion {

  /// <summary>None: the value itself is used.</summary>
  None,

  /// <summary><c>ToInt32</c>: a bitwise operand, <c>| 0</c>, an argument of <c>Math.imul</c>.</summary>
  Int32,

  /// <summary><c>ToUint32</c>: the left operand of <c>&gt;&gt;&gt;</c>, the argument of <c>Math.clz32</c>.</summary>
  Uint32,

  /// <summary>An operand of a <c>+</c> or <c>-</c> that stands under <c>ToInt32</c>: only its value modulo 2^32 matters, but it is not converted itself.</summary>
  Modulo32,

  /// <summary>An operand of a <c>+</c> or <c>-</c> that stands under <c>ToUint32</c>.</summary>
  ModuloU32,

  /// <summary><c>BigInt.asIntN(64, …)</c>.</summary>
  Int64,

  /// <summary><c>BigInt.asUintN(64, …)</c>.</summary>
  Uint64

}

/// <summary>A parameter or a declared local of a function, with what the inference knows about it.</summary>
/// <param name="name">The name.</param>
/// <param name="declaration">The identifier that declares it.</param>
/// <param name="isParameter">Whether it is a parameter.</param>
internal sealed class Variable(string name, Node declaration, bool isParameter) {

  /// <summary>The name.</summary>
  public string Name { get; } = name;

  /// <summary>The identifier that declares the variable.</summary>
  public Node Declaration { get; } = declaration;

  /// <summary>Whether the variable is a parameter.</summary>
  public bool IsParameter { get; } = isParameter;

  /// <summary>Whether the variable is declared with <c>const</c>.</summary>
  public bool IsConstant { get; init; }

  /// <summary>Whether the variable is declared without a value, so that an assignment must precede every read.</summary>
  public bool IsUninitialized { get; init; }

  /// <summary>The type JSDoc states for a parameter, or <see langword="null"/>.</summary>
  public JsType? Declared { get; set; }

  /// <summary>The type of every value that reaches the variable.</summary>
  public JsType Type { get; set; } = JsType.Unknown;

  /// <summary>
  /// The type of every value a parameter is called with: what JSDoc states, or what the function and
  /// its callers in the file show. It is the type of the parameter in the IR; when the body assigns
  /// the parameter something else, the body has a variable of its own for it.
  /// </summary>
  public JsType Entry { get; set; } = JsType.Unknown;

  /// <summary>The expression that decided <see cref="Entry"/>, or <see langword="null"/> when <see cref="Reason"/> did.</summary>
  public Node? EntryEvidence { get; set; }

  /// <summary>The parameter in the IR, once the function is lowered.</summary>
  public IrLocal? Parameter { get; set; }

  /// <summary>The expression that decided the type: the first one whose value made it what it is.</summary>
  public Node? Evidence { get; set; }

  /// <summary>What decided the type when it was no expression of the body: JSDoc, or the lack of it.</summary>
  public string? Reason { get; set; }

  /// <summary>The coercions under which a parameter is read, with the expression that applies each; <see cref="Coercion.None"/> for any other read.</summary>
  public List<(Coercion Coercion, Node Use)> Uses { get; } = [];

  /// <summary>The coercion with which the first statements of the function re-assign a parameter to itself, as <c>x = x | 0</c> does.</summary>
  public (Coercion Coercion, Node Use)? Prologue { get; set; }

  /// <summary>The first expression that takes an element or the length of the variable, or <see langword="null"/>.</summary>
  public Node? UsedAsArray { get; set; }

  /// <summary>The first expression that uses the variable as only a string can be used - a method only strings have, or the iterable of a <c>for…of</c> - or <see langword="null"/>.</summary>
  public Node? UsedAsString { get; set; }

  /// <summary>Whether a parameter without a JSDoc type is an integer because of how the function reads it.</summary>
  public bool IsIntegerByUse { get; set; }

  /// <summary>
  /// Whether a parameter without a JSDoc type, of a function whose callers are not all known, is used
  /// as an array: its type is then the type of the arrays the file itself passes to it.
  /// </summary>
  public bool IsArrayByCall { get; set; }

  /// <summary>The variable in the IR, once the function is lowered.</summary>
  public IrLocal? Local { get; set; }

}

/// <summary>The ways a value reaches a place.</summary>
internal enum FlowKind {

  /// <summary>The value of the node is assigned to the variable: an initializer, or the right side of <c>=</c>.</summary>
  Assign,

  /// <summary>The node is a compound assignment to the variable.</summary>
  Compound,

  /// <summary>The node steps the variable with <c>++</c> or <c>--</c>.</summary>
  Step,

  /// <summary>The node is an assignment to an element of the array the variable holds.</summary>
  Store,

  /// <summary>The node steps an element of the array the variable holds.</summary>
  StepElement,

  /// <summary>The node is a <c>return</c>.</summary>
  Return,

  /// <summary>The node is an argument of a call of a function of the file.</summary>
  Argument,

  /// <summary>The node is a property of a destructuring declaration: the variable gets the value of that key of the record the source holds.</summary>
  Field

}

/// <summary>One way a value reaches a variable, a result or a parameter of another function.</summary>
/// <param name="Kind">How.</param>
/// <param name="Node">The expression or statement, as <paramref name="Kind"/> says.</param>
/// <param name="Target">The variable, or <see langword="null"/> for a result and an argument.</param>
/// <param name="Callee">The function called, for an argument.</param>
/// <param name="Index">The position of an argument.</param>
/// <param name="Source">The record a destructuring declaration takes the value of a key of.</param>
internal sealed record Flow(FlowKind Kind, Node Node, Variable? Target = null, Candidate? Callee = null, int Index = 0, Node? Source = null);

/// <summary>A constant of the scope that encloses the functions: a number, or a table of numbers.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type of the value, an array type for a table.</param>
/// <param name="Constant">The constant in the IR.</param>
internal sealed record ModuleConstant(string Name, JsType Type, IrModuleConst Constant);

/// <summary>What the inference knows about one function.</summary>
/// <param name="candidate">The function.</param>
internal sealed class FunctionModel(Candidate candidate) {

  /// <summary>The function.</summary>
  public Candidate Candidate { get; } = candidate;

  /// <summary>Why the function cannot be imported whatever its body is, or <see langword="null"/>.</summary>
  public Diagnostic? Problem { get; set; }

  /// <summary>The parameters, in call order.</summary>
  public List<Variable> Parameters { get; } = [];

  /// <summary>The declared locals, in source order.</summary>
  public List<Variable> Locals { get; } = [];

  /// <summary>The variable or constant every identifier of the body names; an identifier that names neither is not in it.</summary>
  public Dictionary<Node, object> Bindings { get; } = [];

  /// <summary>The declarations that the scoping rules refuse, each with what it is.</summary>
  public Dictionary<Node, string> Refused { get; } = [];

  /// <summary>How values reach the variables, the result and the parameters of called functions.</summary>
  public List<Flow> Flows { get; } = [];

  /// <summary>The functions of the file the body calls, each once.</summary>
  public List<Candidate> Callees { get; } = [];

  /// <summary>The type JSDoc states for the result, or <see langword="null"/>.</summary>
  public IrType? DeclaredResult { get; set; }

  /// <summary>The type of every value the function returns; undefined when it returns none.</summary>
  public JsType Result { get; set; } = JsType.Unknown;

  /// <summary>The <c>return</c> that decided the type of the result.</summary>
  public Node? ResultEvidence { get; set; }

  /// <summary>Whether the function is visible outside the module, so that its callers are not all known.</summary>
  public bool IsPublic { get; set; }

  /// <summary>The type of the result in the IR.</summary>
  public IrType ReturnType {
    get {
      var inferred = this.Result.Ir ?? IrType.Void;
      return this.DeclaredResult is { } declared && Holds(declared, inferred) ? declared : inferred;
    }
  }

  /// <summary>Whether a stated type holds every value of an inferred one, so that stating it changes no value.</summary>
  private static bool Holds(IrType declared, IrType inferred)
    => declared == inferred
      || (inferred == IrType.F32 && declared == IrType.F64)
      || (inferred.IsInteger && !inferred.IsLong && (declared == IrType.F64 || (declared.IsInteger && !declared.IsLong && IrOperators.Contains(declared, inferred))));

}
