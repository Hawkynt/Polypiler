using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// A function value made where the expression stands: a lambda, an anonymous method, a local function
/// or a nested function of the source. Evaluating it evaluates nothing: it yields a new closure over
/// the variables it captures.
/// <para>
/// A closure captures <b>variables</b>, never their values. This is stated here once for every node of
/// the IR and every implementation of it: a captured local is one storage location, shared by the
/// function that declares it and by every closure that captures it, so a write after the closure was
/// made is seen by the closure, and a write by the closure is seen by the function and by every other
/// closure over the same variable. A local declared in a block is a new variable each time its
/// declaration runs - in a loop body once per pass - and a loop variable an <see cref="IrFor"/> lists in
/// <see cref="IrFor.PerIteration"/> is copied into a new variable before every pass.
/// </para>
/// </summary>
/// <param name="Type">The function type of the value; its parameters and result are those of the lambda.</param>
/// <param name="Captures">The variables of the enclosing functions the body uses: each in scope where the lambda stands. Empty for a <c>static</c> lambda.</param>
/// <param name="Parameters">The parameters, in call order.</param>
/// <param name="Locals">Every local the body declares.</param>
/// <param name="Body">The statements of the function; a <see cref="IrReturn"/> in it leaves the lambda.</param>
public sealed record IrLambda(IrType Type, IReadOnlyList<IrLocal> Captures, IReadOnlyList<IrLocal> Parameters, IReadOnlyList<IrLocal> Locals, IrBlock Body) : IrExpr(Type) {

  /// <summary>The type of the result, or <see cref="IrType.Void"/>.</summary>
  public IrType ReturnType => this.Type.Signature!.Result;

  /// <summary>
  /// A lambda that captures exactly the variables its body uses and does not declare: what a frontend
  /// builds once it has lowered the body.
  /// </summary>
  /// <param name="parameters">The parameters, in call order.</param>
  /// <param name="result">The type of the result, or <see cref="IrType.Void"/>.</param>
  /// <param name="locals">Every local the body declares.</param>
  /// <param name="body">The statements of the function.</param>
  /// <returns>The lambda.</returns>
  public static IrLambda Of(IReadOnlyList<IrLocal> parameters, IrType result, IReadOnlyList<IrLocal> locals, IrBlock body) {
    var type = IrType.Function(new IrFunctionType([.. parameters.Select(parameter => parameter.Type)], result));
    return new IrLambda(type, IrClosures.Free(parameters, locals, body), parameters, locals, body);
  }

}

/// <summary>A function of the module as a value: a method group of a static method of the source, or a top-level function used as a value.</summary>
/// <param name="Type">The function type, which is the signature of the function.</param>
/// <param name="Function">The name of the function, one of the module.</param>
public sealed record IrFunctionRef(IrType Type, string Function) : IrExpr(Type);

/// <summary>
/// A method of a class as a function value: for an instance method bound to its receiver, which is
/// evaluated once, when the value is made, and traps there when it is <c>null</c>; for a static method of a
/// class without one. Calling the value calls the method as <see cref="IrMethodCall"/> would with that
/// receiver, of the same <see cref="Kind"/>.
/// </summary>
/// <param name="Type">The function type, which is the signature of the method after its receiver.</param>
/// <param name="Receiver">The object the method is bound to, or <see langword="null"/> for a static method.</param>
/// <param name="Method">The method, one of a class of the module.</param>
public sealed record IrBoundMethod(IrType Type, IrExpr? Receiver, IrMethod Method) : IrExpr(Type) {

  /// <summary>How a call of the value selects the method that runs.</summary>
  public IrMethodCallKind Kind { get; init; } = IrMethodCallKind.Direct;

}

/// <summary>
/// A call of a function value. The callee is evaluated first, then the arguments left to right, then
/// the callee is checked: a <c>null</c> function value traps with <see cref="IrTrap.NullReference"/>
/// after the arguments - as C# and JavaScript both evaluate the arguments of a call through a
/// variable before they find nothing to call.
/// </summary>
/// <param name="Type">The result type of the callee's signature.</param>
/// <param name="Callee">The function value.</param>
/// <param name="Arguments">One value per parameter of the signature.</param>
public sealed record IrInvoke(IrType Type, IrExpr Callee, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>What the passes over closures have to know: which variables a body uses and does not declare.</summary>
public static class IrClosures {

  /// <summary>
  /// The variables a function body uses that are neither its parameters nor its locals - the variables
  /// a lambda with that body captures - in the order they are first named. A lambda inside the body
  /// contributes its own captures.
  /// </summary>
  /// <param name="parameters">The parameters of the function.</param>
  /// <param name="locals">The locals it declares.</param>
  /// <param name="body">The body.</param>
  /// <returns>Each variable once.</returns>
  public static IReadOnlyList<IrLocal> Free(IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, IrBlock body) {
    var own = new HashSet<IrLocal>(parameters.Concat(locals));
    var free = new List<IrLocal>();
    var seen = new HashSet<IrLocal>();
    foreach (var variable in Named(body))
      if (!own.Contains(variable) && seen.Add(variable))
        free.Add(variable);

    return free;
  }

  /// <summary>Every variable a statement reads, writes or captures, in order, a nested lambda's captures included but not its own variables.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The variables, with repetitions.</returns>
  public static IEnumerable<IrLocal> Named(IrStmt statement) {
    IEnumerable<IrLocal> own = statement switch {
      IrDeclare declare => [declare.Local],
      IrAssign assign => [assign.Local],
      IrFor loop => loop.PerIteration,
      _ => []
    };
    var inner = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements);
    return own.Concat(IrTree.Expressions(statement).SelectMany(Named)).Concat(inner.SelectMany(Named));
  }

  /// <summary>Every variable an expression reads or captures, a nested lambda's captures included.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The variables, with repetitions.</returns>
  public static IEnumerable<IrLocal> Named(IrExpr expression) => expression switch {
    IrLocalRef reference => [reference.Local],
    IrLambda lambda => lambda.Captures,
    _ => IrTree.Operands(expression).SelectMany(Named)
  };

  /// <summary>
  /// The locals of a body that hold one lambda for good - what a local function of the source is: the
  /// local is of a function type, declared with the lambda, or declared without a value and assigned
  /// the lambda once in the block that declares it, and nothing else ever assigns it. A backend may
  /// write such a local as a function of its target's own.
  /// </summary>
  /// <param name="body">The body of the function, lambdas inside it included.</param>
  /// <returns>Each such local with its lambda.</returns>
  public static IReadOnlyDictionary<IrLocal, IrLambda> Held(IrBlock body) {
    var statements = Within(body).ToList();
    var definitions = statements.SelectMany(statement => statement switch {
      IrDeclare { Initializer: not null } declare => [declare.Local],
      IrAssign assign => [assign.Local],
      _ => (IEnumerable<IrLocal>)[]
    }).GroupBy(local => local).ToDictionary(group => group.Key, group => group.Count());
    var held = new Dictionary<IrLocal, IrLambda>();
    foreach (var block in statements.OfType<IrBlock>().Prepend(body))
      foreach (var statement in block.Statements) {
        if (statement is not IrDeclare { Local.Type.IsFunction: true } declare || definitions.GetValueOrDefault(declare.Local) != 1)
          continue;

        var lambda = declare.Initializer as IrLambda
          ?? (declare.Initializer is null ? block.Statements.OfType<IrAssign>().FirstOrDefault(assign => assign.Local == declare.Local)?.Value as IrLambda : null);
        if (lambda is not null)
          held[declare.Local] = lambda;
      }

    return held;
  }

  /// <summary>Every statement within a statement, those of the bodies of its lambdas included, the statement first.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The statements in pre-order.</returns>
  public static IEnumerable<IrStmt> Within(IrStmt statement) {
    yield return statement;
    var inner = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements);
    foreach (var nested in inner.SelectMany(Within))
      yield return nested;

    foreach (var lambda in IrTree.Expressions(statement).SelectMany(Outermost))
      foreach (var nested in Within(lambda.Body))
        yield return nested;
  }

  /// <summary>The lambdas of an expression that no other lambda of it holds.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The lambdas, in the order they are evaluated.</returns>
  public static IEnumerable<IrLambda> Outermost(IrExpr expression)
    => expression is IrLambda lambda ? [lambda] : IrTree.Operands(expression).SelectMany(Outermost);

  /// <summary>
  /// The variables a function captures by any lambda inside it, at any depth: those whose storage a
  /// closure shares, which a pass may therefore neither copy nor move.
  /// </summary>
  /// <param name="body">The body of the function.</param>
  /// <returns>Each variable once.</returns>
  public static IReadOnlySet<IrLocal> Captured(IrBlock body)
    => new HashSet<IrLocal>(IrTree.Nodes(body).OfType<IrLambda>().SelectMany(lambda => lambda.Captures));

}
