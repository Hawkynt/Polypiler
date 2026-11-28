using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.IR.Intrinsics;

namespace Polypiler.Core.Legalize;

/// <summary>
/// What a backend replaces an expression by, with its operands already legalized: a call of a
/// function of the libraries, made through <paramref name="calls"/>, or <see langword="null"/> to
/// keep the expression.
/// </summary>
/// <param name="expression">The expression.</param>
/// <param name="calls">Makes the calls of the functions of the libraries.</param>
/// <returns>The replacement, of the type of the expression, or <see langword="null"/>.</returns>
public delegate IrExpr? LibraryRewrite(IrExpr expression, LibraryCalls calls);

/// <summary>
/// Makes a call of a function of the libraries of Polypiler's own code - a portable floating-point
/// implementation or a helper - while a module is legalized: the function, what it calls and the
/// tables it reads are copied into the module under names no name of the module has.
/// </summary>
public sealed class LibraryCalls : IIntrinsicLibrary {

  private readonly Func<string, IReadOnlyList<IrExpr>, IrCall> _call;
  private readonly Func<string, IrFunction> _signature;
  private readonly Func<IrExpr, IrExpr> _legalize;

  internal LibraryCalls(Func<string, IReadOnlyList<IrExpr>, IrCall> call, Func<string, IrFunction> signature, Func<IrExpr, IrExpr> legalize) {
    this._call = call;
    this._signature = signature;
    this._legalize = legalize;
  }

  /// <summary>
  /// An expression a rewrite builds of other operations, legalized as the module is: its nodes that
  /// are new - not the operands it was given, which are legalized already - are replaced where the
  /// target lacks them.
  /// </summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The legalized expression.</returns>
  public IrExpr Legalize(IrExpr expression) => this._legalize(expression);

  /// <summary>
  /// A call of a function of the libraries, as an expression of a type. An argument of another
  /// numeric type than the parameter is converted to it, wrapping where it is an integer, and the
  /// result is converted to the type the same way, so that a helper over a wide type serves the
  /// narrow ones.
  /// </summary>
  /// <param name="implementation">The name of the function in its library.</param>
  /// <param name="type">The type of the expression the call stands for.</param>
  /// <param name="arguments">The arguments, one for each parameter.</param>
  /// <returns>The call, converted to the type where the function returns another.</returns>
  /// <exception cref="ArgumentException">No library has the function, or the arguments do not fit its parameters.</exception>
  public IrExpr Call(string implementation, IrType type, params IReadOnlyList<IrExpr> arguments) {
    var function = this._signature(implementation);
    if (function.Parameters.Count != arguments.Count)
      throw new ArgumentException($"'{implementation}' takes {function.Parameters.Count} arguments, not {arguments.Count}.", nameof(arguments));

    var adapted = arguments.Select((argument, index) => Adapt(argument, function.Parameters[index].Type)).ToList();
    return Adapt(this._call(implementation, adapted), type);
  }

  /// <summary>A value converted to a type it differs from in width or kind of number only.</summary>
  private static IrExpr Adapt(IrExpr value, IrType type) {
    if (value.Type == type)
      return value;

    if (!(value.Type.IsInteger || value.Type.IsFloat) || !(type.IsInteger || type.IsFloat) || (value.Type.IsFloat && type.IsInteger))
      throw new ArgumentException($"A value of the type {value.Type} cannot stand for one of the type {type}.", nameof(value));

    return value is IrConst constant && type.IsInteger ? new IrConst(type, IrOperators.Wrap(type, constant.Value)) : new IrConvert(type, value);
  }

}

/// <summary>
/// Replaces what a target has no native form of by calls of the functions of the libraries of
/// Polypiler's own code, which come as one module (<see cref="ImplementationSources.Merge"/>): the
/// functions a call needs, the ones they call in turn and the tables they read are copied into the
/// module under names no name of the module has, and their own expressions are legalized the same
/// way. Generated code stays self-contained, and the interpreter, which runs the same functions,
/// gives the same results. The module records every function it copied in under
/// <see cref="IrModule.Implementations"/>.
/// </summary>
public static class LibraryLegalization {

  /// <summary>The module with every expression a rewrite replaces replaced, the library functions it calls copied in.</summary>
  /// <param name="module">The module to legalize.</param>
  /// <param name="implementations">Gives the libraries as one module; asked only when a call needs it, since reading it costs.</param>
  /// <param name="rewrite">What replaces an expression; applied to every expression after its operands.</param>
  /// <returns>The legalized module; the module itself when nothing was replaced.</returns>
  /// <exception cref="ArgumentException">A function a call needs is missing from <paramref name="implementations"/>.</exception>
  public static IrModule Lower(IrModule module, Func<IrModule> implementations, LibraryRewrite rewrite) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(rewrite);
    var run = new Run(module, implementations, rewrite);
    var globals = run.Globals();
    var functions = module.Functions.Select(run.Function).ToList();
    if (run.Names.Count == 0 && !run.Changed)
      return module;

    var helpers = run.Helpers();
    return module with {
      Globals = globals,
      Functions = [.. functions, .. helpers.Functions],
      Constants = [.. module.Constants, .. helpers.Constants],
      Implementations = [.. module.Implementations, .. run.Names.Select(pair => (pair.Value, pair.Key))]
    };
  }

  /// <summary>Thrown inside a function of the libraries for a rewrite that would make it call itself; the expression is then kept.</summary>
  private sealed class SelfReference : Exception;

  private sealed class Run(IrModule module, Func<IrModule> implementations, LibraryRewrite rewrite) {

    private readonly Lazy<Dictionary<string, IrFunction>> _available = new(() => implementations().Functions.ToDictionary(function => function.Name));
    private readonly HashSet<string> _taken = [
      .. module.Functions.Select(function => function.Name), .. module.Constants.Select(constant => constant.Name), .. module.Globals.Select(global => global.Name)
    ];
    private readonly Dictionary<IrModuleConst, IrModuleConst> _tables = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IrGlobal> _globals = new(StringComparer.Ordinal);

    /// <summary>How many expressions were replaced or made to name a new global so far.</summary>
    private int _edits;
    private LibraryCalls? _calls;
    private bool _inHelper;
    private string? _current;

    /// <summary>The name in the module of every function of the libraries it needs, in the order they were first needed.</summary>
    public Dictionary<string, string> Names { get; } = [];

    /// <summary>Whether a rewrite replaced an expression by one that calls no function of the libraries.</summary>
    public bool Changed { get; private set; }

    private LibraryCalls Calls => this._calls ??= new(this.CallOf, this.Signature, this.Expression);

    public IrFunction Function(IrFunction function) => function with { Body = this.Block(function.Body) };

    /// <summary>
    /// The globals with their initializers legalized, in their order, which is the order they are
    /// initialised in: a global whose initializer changes is a new one, and every reference to it - in
    /// a later initializer, in a function - is made to name the new one.
    /// </summary>
    public IReadOnlyList<IrGlobal> Globals() {
      var globals = new List<IrGlobal>();
      foreach (var global in module.Globals) {
        var before = this._edits;
        var initializer = global.Initializer is null ? null : this.Expression(global.Initializer);
        var legalized = this._edits == before ? global : global with { Initializer = initializer };
        this.Changed |= !ReferenceEquals(legalized, global);
        this._globals[global.Name] = legalized;
        globals.Add(legalized);
      }

      return globals;
    }

    private IrGlobal Global(IrGlobal global) => this._globals.TryGetValue(global.Name, out var legalized) ? legalized : global;

    /// <summary>The functions the module calls, each legalized itself, and the tables they read.</summary>
    public (List<IrFunction> Functions, List<IrModuleConst> Constants) Helpers() {
      this._inHelper = true;
      var done = new List<IrFunction>();
      var index = 0;
      while (index < this.Names.Count) {
        var name = this.Names.Keys.ElementAt(index++);
        this._current = name;
        done.Add(this.Function(this._available.Value[name]) with { Name = this.Names[name], IsPublic = false });
      }

      return (done, [.. this._tables.Values]);
    }

    private IrFunction Signature(string name)
      => this._available.Value.TryGetValue(name, out var function) ? function : throw new ArgumentException($"The library function '{name}' is missing.", nameof(implementations));

    private IrCall CallOf(string name, IReadOnlyList<IrExpr> arguments) {
      if (name == this._current)
        throw new SelfReference();

      var function = this.Signature(name);
      return new IrCall(this.Rename(name), function.ReturnType, arguments);
    }

    private string Rename(string name) {
      if (this.Names.TryGetValue(name, out var renamed))
        return renamed;

      if (!this._available.Value.ContainsKey(name))
        throw new ArgumentException($"The library function '{name}' is missing.", nameof(implementations));

      var candidate = ImplementationSources.IsFloat(name) ? "Float" + name : name;
      while (!this._taken.Add(candidate))
        candidate += "_";

      this.Names[name] = candidate;
      return candidate;
    }

    private IrModuleConst Table(IrModuleConst constant) {
      if (!this._inHelper)
        return constant;

      if (!this._tables.TryGetValue(constant, out var renamed)) {
        var trimmed = constant.Name.TrimStart('_');
        var candidate = this._current is { } current && ImplementationSources.IsFloat(current) ? "Float" + trimmed : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        while (!this._taken.Add(candidate))
          candidate += "_";

        renamed = constant with { Name = candidate };
        this._tables[constant] = renamed;
      }

      return renamed;
    }

    private IrBlock Block(IrBlock block) => new(block.Statements.Select(this.Statement).ToList());

    private IrStmt Statement(IrStmt statement) => statement switch {
      IrBlock block => this.Block(block),
      IrDeclare declare => declare with { Initializer = declare.Initializer is null ? null : this.Expression(declare.Initializer) },
      IrAssign assign => assign with { Value = this.Expression(assign.Value) },
      IrGlobalAssign assign => assign with { Global = this.Global(assign.Global), Value = this.Expression(assign.Value) },
      IrStore store => new IrStore(this.Expression(store.Array), this.Expression(store.Index), this.Expression(store.Value)),
      IrFieldStore store => store with { Target = this.Expression(store.Target), Value = this.Expression(store.Value) },
      IrExprStmt { Expression: IrStep { Target: IrLocalRef or IrGlobalRef } step } => this.Stepped(step),
      IrExprStmt expression => new IrExprStmt(this.Expression(expression.Expression)),
      IrWriteLine write => write with { Value = this.Expression(write.Value) },
      IrIf conditional => new IrIf(this.Expression(conditional.Condition), this.Block(conditional.Then), conditional.Else is null ? null : this.Block(conditional.Else)),
      IrWhile loop => new IrWhile(this.Expression(loop.Condition), this.Block(loop.Body)),
      IrDoWhile loop => new IrDoWhile(this.Block(loop.Body), this.Expression(loop.Condition)),
      IrFor loop => new IrFor(this.Expression(loop.Condition), this.Block(loop.Body), this.Block(loop.Step)) { PerIteration = loop.PerIteration },
      IrSwitch choice => new IrSwitch(this.Expression(choice.Value), choice.Sections.Select(section => section with { Body = this.Block(section.Body) }).ToList()),
      IrReturn result => new IrReturn(result.Value is null ? null : this.Expression(result.Value)),
      IrThrow thrown => new IrThrow(this.Expression(thrown.Exception)),
      IrTry attempt => new IrTry(this.Block(attempt.Body),
        [.. attempt.Catches.Select(clause => clause with { Filter = clause.Filter is null ? null : this.Expression(clause.Filter), Body = this.Block(clause.Body) })],
        attempt.Finally is null ? null : this.Block(attempt.Finally)),
      IrUsing scope => scope with { Initializer = this.Expression(scope.Initializer), Body = this.Block(scope.Body) },
      IrForEachCollection loop => loop with { Collection = this.Expression(loop.Collection), Body = this.Block(loop.Body) },
      IrYield produced => new IrYield(this.Expression(produced.Value)),
      IrYieldFrom delegated => new IrYieldFrom(this.Expression(delegated.Sequence)),
      IrForEachSequence loop => loop with { Sequence = this.Expression(loop.Sequence), Body = this.Block(loop.Body) },
      _ => statement
    };

    /// <summary>
    /// A step of a variable as a statement: where its legalization is the call of a lambda that stores
    /// the value - the form of a step whose value is used - it is the assignment of that value instead,
    /// which reads no variable from a lambda, also one whose address is taken.
    /// </summary>
    private IrStmt Stepped(IrStep step) {
      var legalized = this.Expression(step);
      if (legalized is not IrInvoke { Callee: IrLambda })
        return new IrExprStmt(legalized);

      var op = (step.Increment, step.Trap) switch {
        (true, false) => IrBinaryOp.Add,
        (false, false) => IrBinaryOp.Subtract,
        (true, true) => IrBinaryOp.AddChecked,
        _ => IrBinaryOp.SubtractChecked
      };
      var value = this.Expression(new IrBinary(op, step.Target, new IrConst(step.Type, 1)));
      return step.Target is IrLocalRef local ? new IrAssign(local.Local, value) : new IrGlobalAssign(((IrGlobalRef)step.Target).Global, value);
    }

    private IrExpr Expression(IrExpr expression) {
      var operands = this.Operands(expression);
      try {
        if (rewrite(operands, this.Calls) is { } replaced) {
          this.Changed = true;
          ++this._edits;
          return replaced;
        }
      } catch (SelfReference) {
        // The function of the libraries computes this expression itself: it keeps its own form.
      }

      return operands;
    }

    private IrExpr Operands(IrExpr expression) {
      switch (expression) {
        case IrConstRef reference:
          return new IrConstRef(this.Table(reference.Constant));
        case IrGlobalRef reference when !ReferenceEquals(this.Global(reference.Global), reference.Global):
          ++this._edits;
          return new IrGlobalRef(this.Global(reference.Global));
        case IrUnary unary:
          return new IrUnary(unary.Op, this.Expression(unary.Operand));
        case IrBinary binary:
          return new IrBinary(binary.Op, this.Expression(binary.Left), this.Expression(binary.Right));
        case IrConvert conversion:
          return new IrConvert(conversion.Type, this.Expression(conversion.Operand), conversion.Contract);
        case IrConditional choice:
          return new IrConditional(this.Expression(choice.Condition), this.Expression(choice.WhenTrue), this.Expression(choice.WhenFalse));
        case IrArrayLoad load:
          return new IrArrayLoad(this.Expression(load.Array), this.Expression(load.Index));
        case IrArrayLength length:
          return new IrArrayLength(this.Expression(length.Array));
        case IrNewArray creation:
          return new IrNewArray(creation.Type, this.Expression(creation.Length));
        case IrStep step:
          return step with { Target = this.Expression(step.Target) };
        case IrStructNew creation:
          return creation with { Arguments = [.. creation.Arguments.Select(this.Expression)] };
        case IrFieldLoad load:
          return load with { Target = this.Expression(load.Target) };
        case IrStructEquals equality:
          return new IrStructEquals(this.Expression(equality.Left), this.Expression(equality.Right));
        case IrNew creation:
          return creation with { Arguments = [.. creation.Arguments.Select(this.Expression)] };
        case IrMethodCall call:
          return call with { Target = call.Target is null ? null : this.Expression(call.Target), Arguments = [.. call.Arguments.Select(this.Expression)] };
        case IrReferenceEquals identity:
          return new IrReferenceEquals(this.Expression(identity.Left), this.Expression(identity.Right));
        case IrGenericEquals equality:
          return new IrGenericEquals(this.Expression(equality.Left), this.Expression(equality.Right));
        case IrGenericCompare order:
          return new IrGenericCompare(this.Expression(order.Left), this.Expression(order.Right));
        case IrLambda lambda:
          return lambda with { Body = this.Block(lambda.Body) };
        case IrBoundMethod { Receiver: { } receiver } bound:
          return bound with { Receiver = this.Expression(receiver) };
        case IrInvoke invocation:
          return invocation with { Callee = this.Expression(invocation.Callee), Arguments = [.. invocation.Arguments.Select(this.Expression)] };
        case IrTypeTest test:
          return test with { Value = this.Expression(test.Value) };
        case IrCast cast:
          return cast with { Value = this.Expression(cast.Value) };
        case IrAsCast cast:
          return cast with { Value = this.Expression(cast.Value) };
        case IrSome some:
          return new IrSome(this.Expression(some.Value));
        case IrIsNone test:
          return new IrIsNone(this.Expression(test.Value));
        case IrUnwrap unwrap:
          return unwrap with { Value = this.Expression(unwrap.Value) };
        case IrCoalesce coalesce:
          return new IrCoalesce(this.Expression(coalesce.Value), this.Expression(coalesce.Fallback));
        case IrIsNull test:
          return new IrIsNull(this.Expression(test.Value));
        case IrVariantOf made:
          return made with { Value = made.Value is null ? null : this.Expression(made.Value) };
        case IrVariantTagOf tag:
          return new IrVariantTagOf(this.Expression(tag.Value));
        case IrVariantValue value:
          return value with { Value = this.Expression(value.Value) };
        case IrUnionOf made:
          return made with { Value = this.Expression(made.Value) };
        case IrUnionTagOf tag:
          return new IrUnionTagOf(this.Expression(tag.Value));
        case IrUnionValue value:
          return new IrUnionValue(this.Expression(value.Value), value.Member) { IsTested = value.IsTested };
        case IrBind bind:
          return bind with { Value = this.Expression(bind.Value) };
        case IrThrowExpression thrown:
          return thrown with { Exception = this.Expression(thrown.Exception) };
        case IrCollection operation:
          return operation with { Arguments = [.. operation.Arguments.Select(this.Expression)] };
        case IrArraySequence sequence:
          return new IrArraySequence(this.Expression(sequence.Array));
        case IrAwait awaited:
          return new IrAwait(this.Expression(awaited.Operand));
        case IrAsync operation:
          return operation with { Arguments = [.. operation.Arguments.Select(this.Expression)] };
        case IrForeignCall call:
          return call with { Arguments = [.. call.Arguments.Select(this.Expression)] };
        case IrCall call:
          var arguments = call.Arguments.Select(this.Expression).ToList();
          return new IrCall(this._inHelper ? this.Rename(call.Function) : call.Function, call.Type, arguments);
        case IrIntrinsic intrinsic:
          return new IrIntrinsic(intrinsic.Op, intrinsic.Type, [.. intrinsic.Arguments.Select(this.Expression)]);
        default:
          return expression;
      }
    }

  }

}
