using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0463, specialisation of functions for the function values passed to them (prototype). A function
/// value that never leaves the function it is made in - a lambda, a local function, a method group -
/// is made a function of the module, and a function it is passed to gets a copy in which the
/// parameter that received it is gone and every call of it is a direct call. Inlining (O0006), which
/// runs next, then copies both into the caller, and propagation, folding and dead-code removal clean
/// up: a LINQ chain or an array callback becomes one loop with no delegate.
/// <para>
/// A parameter of a function type stays local - does not escape - when every use of it is a call of
/// it or an argument to a parameter of another function that stays local in turn; it escapes when it
/// is stored, returned, captured by a lambda, compared, assigned, passed anywhere else, when the
/// function returns a lazy sequence or yields, and when the chain of calls comes back to a function
/// on it. A lambda passed to such a parameter is lifted: its parameters come first, then the
/// variables it captures. A captured variable no lambda of the function writes is passed by value,
/// read where the call evaluates its last argument - what the closure would read when it runs during
/// the call. One a lambda writes is moved into a cell of one element, an array the function, its
/// lambdas and the lifted function share, when it is a number or a truth value the function declares
/// and every lambda that captures it is lifted. A local that holds a lambda for good and is only
/// called or passed on is lifted the same way when its lambda writes nothing it captures; one that
/// does stays a closure of its function, classified as capturing. A lambda called where it stands is
/// lifted too. A cell that nothing but its element reads any more becomes a variable again.
/// </para>
/// <para>
/// Equal copies are made once: one callee with the same functions in the same parameters. A budget
/// bounds the copies of one function (<see cref="CloneLimit"/>) and the size of all copies together
/// (<see cref="GrowthLimit"/>); a call the budget leaves as it is is reported as a note. A function of
/// the program the copies replaced at every call, private and no method, goes.
/// </para>
/// </summary>
public sealed class O0463LambdaSpecialisation : IIrPass {

  /// <summary>The most copies of one function.</summary>
  public const int CloneLimit = 8;

  /// <summary>The largest size of all copies of a module together, in statements and expression nodes.</summary>
  public const int GrowthLimit = 4096;

  private readonly int _cloneLimit;
  private readonly int _growthLimit;

  /// <summary>The pass with the budget of the pipeline.</summary>
  public O0463LambdaSpecialisation() : this(CloneLimit, GrowthLimit) { }

  /// <summary>The pass with a budget of its own.</summary>
  /// <param name="clones">The most copies of one function.</param>
  /// <param name="growth">The largest size of all copies together.</param>
  public O0463LambdaSpecialisation(int clones, int growth) {
    this._cloneLimit = clones;
    this._growthLimit = growth;
  }

  /// <inheritdoc/>
  public string Id => "O0463";

  /// <inheritdoc/>
  public string Name => "lambda specialisation";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) => this.Run(module, scope, applied, _ => { });

  /// <summary>Runs the pass once and reports every call the budget leaves as it is.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">Whether the pass looks at a function.</param>
  /// <param name="applied">Receives every change: the function and the position of the statement it was made at.</param>
  /// <param name="note">Receives a note for every call the budget leaves as it is.</param>
  /// <returns>The module, the same object when nothing changed.</returns>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied, Action<string> note) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    ArgumentNullException.ThrowIfNull(note);
    return new Specialisation(this, module, applied, note).Module(scope);
  }

  /// <summary>Whether a variable of the type fits in a cell: a number or a truth value, whose default is the constant 0.</summary>
  private static bool Cellable(IrType type) => type.IsInteger || type == IrType.Bool || type == IrType.F32 || type == IrType.F64;

  private static IrConst Index => new(IrType.I32, 0);

  /// <summary>Whether an array and an index are the element of a cell: <c>c[0]</c>.</summary>
  private static bool IsElement(IrExpr array, IrExpr index, IrLocal cell) => array is IrLocalRef reference && reference.Local == cell && index is IrConst { Value: 0 };

  /// <summary>A function value as a function of the module: every call of it calls <paramref name="Target"/> with its arguments and then <paramref name="Arguments"/>.</summary>
  /// <param name="Target">The function.</param>
  /// <param name="Captures">The last parameters of the target, which receive what the value captured.</param>
  /// <param name="Arguments">What they receive where the value is made.</param>
  private sealed record Binding(string Target, IReadOnlyList<IrLocal> Captures, IReadOnlyList<IrExpr> Arguments);

  /// <summary>A lambda of the function looked at that is lifted, with what it captures by value and in cells.</summary>
  private sealed class Candidate(IrLambda lambda, bool literal) {

    public IrLambda Lambda => lambda;

    /// <summary>Whether the lambda stands at the call that passes it, rather than in a local.</summary>
    public bool IsLiteral => literal;

    /// <summary>Whether it is lifted after all.</summary>
    public bool Accepted { get; set; } = true;

    /// <summary>The captured variables a lambda writes, which go into cells.</summary>
    public HashSet<IrLocal> Written { get; } = [];

  }

  /// <summary>
  /// Rebuilds a function body bottom-up: the expressions of every statement, those in the bodies of
  /// lambdas included, then the statement. A callback gets the original node and the one rebuilt from
  /// its mapped parts, and gives a replacement or <see langword="null"/>; a statement callback may give
  /// several statements. A statement whose own expressions changed, or that was replaced, outside the
  /// bodies of lambdas is reported with its position.
  /// </summary>
  private sealed class Mapper(Func<IrExpr, IrExpr, IrExpr?> expression, Func<IrStmt, IrStmt, IReadOnlyList<IrStmt>?> statement, Func<IrLocal, IrLocal> capture, Action<string>? applied) {

    private readonly List<int> _position = [];
    private int _depth;

    public IrBlock Block(IrBlock block) {
      var statements = new List<IrStmt>();
      var changed = false;
      for (var index = 0; index < block.Statements.Count; ++index) {
        this._position.Add(index + 1);
        var original = block.Statements[index];
        var mapped = this.Statement(original);
        changed |= mapped.Count != 1 || !ReferenceEquals(mapped[0], original);
        statements.AddRange(mapped);
        this._position.RemoveAt(this._position.Count - 1);
      }

      return changed ? new IrBlock(statements) : block;
    }

    private IReadOnlyList<IrStmt> Statement(IrStmt original) {
      if (original is IrBlock nested)
        return [this.Block(nested)];

      var expressions = IrTree.Expressions(original);
      var mapped = expressions.Select(this.Expression).ToList();
      var blocks = IrTree.Blocks(original);
      var inner = blocks.Select(this.Block).ToList();
      var own = mapped.Where((value, index) => !ReferenceEquals(value, expressions[index])).Any();
      var rebuilt = own || inner.Where((value, index) => !ReferenceEquals(value, blocks[index])).Any() ? IrTree.With(original, mapped, inner) : original;
      var replaced = statement(original, rebuilt);
      if ((own || replaced is not null) && this._depth == 0)
        applied?.Invoke(string.Join('.', this._position));

      return replaced ?? [rebuilt];
    }

    public IrExpr Expression(IrExpr original) {
      IrExpr rebuilt;
      if (original is IrLambda lambda) {
        ++this._depth;
        var body = this.Block(lambda.Body);
        --this._depth;
        var captures = lambda.Captures.Select(capture).ToList();
        rebuilt = ReferenceEquals(body, lambda.Body) && captures.SequenceEqual(lambda.Captures) ? lambda : lambda with { Body = body, Captures = captures };
      } else {
        var operands = IrTree.Operands(original);
        var mapped = operands.Select(this.Expression).ToList();
        rebuilt = mapped.Where((value, index) => !ReferenceEquals(value, operands[index])).Any() ? IrTree.With(original, mapped) : original;
      }

      return expression(original, rebuilt) ?? rebuilt;
    }

  }

  /// <summary>The run of the pass over one module.</summary>
  private sealed class Specialisation {

    private readonly O0463LambdaSpecialisation _pass;
    private readonly IrModule _module;
    private readonly Action<string, string> _applied;
    private readonly Action<string> _note;
    private readonly Dictionary<string, IrFunction> _functions;
    private readonly List<string> _order;
    private readonly HashSet<string> _library;
    private readonly HashSet<string> _members;
    private readonly List<IrScopeFunction> _scopeFunctions;
    private readonly List<IrClone> _clones;
    private readonly Dictionary<(string Function, int Parameter), bool> _escapes = [];
    private readonly HashSet<string> _replaced = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _lifted;
    private bool _changed;

    public Specialisation(O0463LambdaSpecialisation pass, IrModule module, Action<string, string> applied, Action<string> note) {
      this._pass = pass;
      this._module = module;
      this._applied = applied;
      this._note = note;
      this._functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
      this._order = [.. module.Functions.Select(function => function.Name)];
      this._library = new(module.Implementations.Select(pair => pair.Function), StringComparer.Ordinal);
      this._members = new(Members(module), StringComparer.Ordinal);
      this._scopeFunctions = [.. module.ScopeFunctions];
      this._clones = [.. module.Clones];
    }

    /// <summary>The functions of the methods, constructors and accessors of the types of a module, which only a method call reaches.</summary>
    private static IEnumerable<string> Members(IrModule module) {
      foreach (var declaration in module.Types)
        switch (declaration) {
          case IrClassType type:
            foreach (var method in type.Methods.Concat(type.Properties.SelectMany(property => new[] { property.Getter, property.Setter }).OfType<IrMethod>()))
              yield return method.Function;

            foreach (var constructor in type.Constructors)
              yield return constructor.Function;

            break;
          case IrInterfaceType type:
            foreach (var method in type.Methods)
              yield return method.Function;

            break;
        }
    }

    public IrModule Module(Func<IrFunction, bool> scope) {
      foreach (var name in this._order.ToList()) {
        var function = this._functions[name];
        if (!scope(function) || function.Origin is not null || this._library.Contains(name))
          continue;

        var result = this.Lift(this.Uncell(function));
        if (ReferenceEquals(result, function))
          continue;

        this._functions[name] = result;
        this._changed = true;
      }

      if (!this._changed && this._scopeFunctions.Count == this._module.ScopeFunctions.Count)
        return this._module;

      this.RemoveReplaced();
      var specialised = this._module with { Functions = [.. this._order.Select(name => this._functions[name])], Clones = this._clones };
      var effects = IrEffects.Of(specialised);
      return specialised with { ScopeFunctions = [.. this._scopeFunctions.Select(known => known.Effects is null ? known with { Effects = Effects(specialised, effects, known) } : known)] };
    }

    /// <summary>
    /// What a function of a scope does by its effect summary: a lifted one that of its function, one
    /// that stays a closure that of the lambda its variable holds, the variables it captures counted as
    /// storage of its scope.
    /// </summary>
    private static IrEffectKinds Effects(IrModule module, IrEffectTable effects, IrScopeFunction known) {
      if (known.IsLifted)
        return effects.Function(known.Name).Kinds;

      var scope = module.Functions.FirstOrDefault(function => function.Name == known.Scope);
      var held = scope is null ? null : IrClosures.Held(scope.Body).FirstOrDefault(pair => pair.Key.Name == known.Name).Value;
      return held is null ? IrEffects.Unknown.Kinds : effects.Lambda(held).Kinds;
    }

    /// <summary>
    /// Removes the functions of the program whose calls the copies replaced, where nothing names them
    /// any more and nothing outside the module can: private ones that are no method.
    /// </summary>
    private void RemoveReplaced() {
      for (var removed = true; removed;) {
        removed = false;
        var named = this.Named();
        foreach (var name in this._replaced.Where(name => this._functions.TryGetValue(name, out var function) && !function.IsPublic && !named.Contains(name)).ToList()) {
          this._order.Remove(name);
          this._functions.Remove(name);
          removed = true;
        }
      }
    }

    /// <summary>Every function some function or global names, by a call or as a value.</summary>
    private HashSet<string> Named() {
      var named = new HashSet<string>(StringComparer.Ordinal);
      var nodes = this._order.SelectMany(name => IrPassSupport.Nodes(this._functions[name].Body))
        .Concat(this._module.Globals.Select(global => global.Initializer).OfType<IrExpr>().SelectMany(IrTree.Nodes));
      foreach (var node in nodes)
        if (node is IrCall call)
          named.Add(call.Function);
        else if (node is IrFunctionRef reference)
          named.Add(reference.Function);

      return named;
    }

    // ------------------------------------------------------------------ cells back to variables

    /// <summary>
    /// A function with every cell of one element that nothing but its element reads or writes any more
    /// made a variable again: the declaration of the array, the store of its first value right after it,
    /// and every read and store of its element.
    /// </summary>
    private IrFunction Uncell(IrFunction function) {
      // Most functions have no array of numbers at all: nothing to look at.
      if (!function.Locals.Any(local => local.Type.Kind == IrTypeKind.Array && Cellable(local.Type.Element!)))
        return function;

      var reads = IrPassSupport.Reads(function.Body);
      var writes = IrPassSupport.Writes(function.Body);
      var declarations = IrPassSupport.Declarations(function.Body);
      var captured = IrClosures.Captured(function.Body);
      var statements = IrPassSupport.Statements(function.Body).ToList();
      var nodes = IrPassSupport.Nodes(function.Body).ToList();
      var cells = new Dictionary<IrLocal, IrLocal>();
      var taken = new HashSet<string>(function.Parameters.Concat(function.Locals).Select(local => local.Name), StringComparer.Ordinal);
      foreach (var local in function.Locals) {
        if (local.Type.Kind != IrTypeKind.Array || !Cellable(local.Type.Element!) || captured.Contains(local)
            || declarations.GetValueOrDefault(local) != 1 || writes.ContainsKey(local)
            || !statements.OfType<IrDeclare>().Any(declare => declare.Local == local && declare.Initializer is IrNewArray { Length: IrConst { Value: 1 } }))
          continue;

        var elements = nodes.OfType<IrArrayLoad>().Count(load => IsElement(load.Array, load.Index, local))
          + statements.OfType<IrStore>().Count(store => IsElement(store.Array, store.Index, local));
        if (elements != reads.GetValueOrDefault(local))
          continue;

        taken.Remove(local.Name);
        cells[local] = new IrLocal(IrPassSupport.Fresh(local.Name, taken), local.Type.Element!);
      }

      if (cells.Count == 0)
        return function;

      IrLocal? Variable(IrExpr array, IrExpr index) => array is IrLocalRef { Local: var local } && index is IrConst { Value: 0 } && cells.TryGetValue(local, out var variable) ? variable : null;
      var mapper = new Mapper(
        (_, node) => node is IrArrayLoad load && Variable(load.Array, load.Index) is { } variable ? new IrLocalRef(variable) : null,
        (_, statement) => statement switch {
          IrDeclare declare when cells.TryGetValue(declare.Local, out var variable) => [new IrDeclare(variable, new IrConst(variable.Type, 0))],
          IrStore store when Variable(store.Array, store.Index) is { } variable => [new IrAssign(variable, store.Value)],
          _ => null
        },
        local => local,
        position => this._applied(function.Name, position));
      var body = Merged(mapper.Block(function.Body), [.. cells.Values]);
      return function with { Body = body, Locals = [.. function.Locals.Select(local => cells.GetValueOrDefault(local, local))] };
    }

    /// <summary>Every declaration of one of the variables with its default value followed directly by an assignment of it made one declaration with that value.</summary>
    private static IrBlock Merged(IrBlock block, HashSet<IrLocal> variables) {
      var statements = new List<IrStmt>();
      for (var index = 0; index < block.Statements.Count; ++index) {
        var statement = block.Statements[index];
        if (statement is IrDeclare { Initializer: IrConst } declare && variables.Contains(declare.Local) && index + 1 < block.Statements.Count
            && block.Statements[index + 1] is IrAssign assign && assign.Local == declare.Local && !IrClosures.Named(assign.Value).Contains(declare.Local)) {
          statements.Add(new IrDeclare(declare.Local, assign.Value));
          ++index;
          continue;
        }

        statements.Add(statement is IrBlock inner ? Merged(inner, variables) : IrTree.With(statement, IrTree.Expressions(statement), [.. IrTree.Blocks(statement).Select(nested => Merged(nested, variables))]));
      }

      return new IrBlock(statements);
    }

    // ------------------------------------------------------------------ escape

    /// <summary>
    /// Whether a function may be called with a function value bound: one of the program's own, not
    /// generic, no helper, no method, no iterator and no async function (#1330) - once that awaits, its
    /// caller goes on, and it may call the value after the caller changed what the value captures.
    /// </summary>
    private bool Bindable(string name, out IrFunction function) {
      if (!this._functions.TryGetValue(name, out function!) || function.Origin is not null || this._library.Contains(name) || this._members.Contains(name))
        return false;

      return !function.ReturnType.IsLazySequence && !function.ReturnType.IsAsync && !function.Body.Statements.Any(IrSequences.Suspends);
    }

    /// <summary>
    /// Whether a parameter of a function may let its value out: anything but being called and being
    /// passed to a parameter that does not either. Recursion counts as escaping.
    /// </summary>
    private bool Escapes(string name, int index) => this.Escapes(name, index, []);

    private bool Escapes(string name, int index, HashSet<string> visiting) {
      if (this._escapes.TryGetValue((name, index), out var known))
        return known;

      if (!this.Bindable(name, out var function) || index >= function.Parameters.Count || !function.Parameters[index].Type.IsFunction || !visiting.Add(name))
        return true;

      var parameter = function.Parameters[index];
      var escapes = this.Leaves(function.Body, parameter, true, (call, argument) => this.Escapes(call.Function, argument, visiting));
      visiting.Remove(name);
      return this._escapes[(name, index)] = escapes;
    }

    /// <summary>
    /// Whether a variable is used other than by a call of it or as an argument a check admits, assigned
    /// other than by its <paramref name="definition"/>, or captured by a lambda; <paramref name="lambdas"/>
    /// says whether the bodies of the lambdas are looked into, which a capture makes unnecessary. A
    /// parameter, or a local of a function type no lambda captures, is never the variable of a loop over
    /// a sequence, a resource, a caught exception or a struct, nor one a loop copies into every pass.
    /// </summary>
    private bool Leaves(IrBlock body, IrLocal variable, bool lambdas, Func<IrCall, int, bool> passed, IrStmt? definition = null) {
      var statements = lambdas ? IrClosures.Within(body).ToList() : IrPassSupport.Statements(body).ToList();
      if (statements.Any(statement => statement is IrAssign assign && assign.Local == variable && !ReferenceEquals(statement, definition)))
        return true;

      bool Leaves(IrExpr expression) {
        switch (expression) {
          case IrLocalRef reference:
            return reference.Local == variable;
          case IrLambda lambda:
            return lambda.Captures.Contains(variable);
          case IrBind bind when bind.Local == variable:
            return true;
          case IrInvoke invocation when invocation.Callee is IrLocalRef callee && callee.Local == variable:
            return invocation.Arguments.Any(Leaves);
          case IrCall call:
            for (var argument = 0; argument < call.Arguments.Count; ++argument)
              if (call.Arguments[argument] is IrLocalRef reference && reference.Local == variable) {
                if (passed(call, argument))
                  return true;
              } else if (Leaves(call.Arguments[argument])) {
                return true;
              }

            return false;
          default:
            return IrTree.Operands(expression).Any(Leaves);
        }
      }

      return statements.SelectMany(IrTree.Expressions).Any(Leaves);
    }

    // ------------------------------------------------------------------ lifting

    /// <summary>
    /// A function with every function value that stays in it made a function of the module: the
    /// lambdas passed to parameters that do not let them out, the locals that hold one for good and are
    /// only called or passed on, and the lambdas called where they stand; and every call that passes
    /// one made a call of a copy of the callee bound to it.
    /// </summary>
    private IrFunction Lift(IrFunction function) {
      var body = function.Body;
      // Most functions make no function value and name no local of a function type: nothing to look at.
      if (!IrPassSupport.Nodes(body).Any(node => node is IrLambda or IrFunctionRef || node is IrLocalRef { Local.Type.IsFunction: true }))
        return function;

      var statements = IrPassSupport.Statements(body).ToList();
      var roots = statements.SelectMany(IrTree.Expressions).ToList();
      var outermost = roots.SelectMany(IrClosures.Outermost).ToList();
      var writes = IrPassSupport.Writes(body);
      var declarations = IrPassSupport.Declarations(body);
      var everyLambda = IrPassSupport.Nodes(body).OfType<IrLambda>().ToList();
      var writtenByLambdas = everyLambda.SelectMany(lambda => lambda.Body.Statements.SelectMany(IrStatements.Writes)).ToHashSet();
      var receiver = this._members.Contains(function.Name) && function.Parameters.Count > 0 ? function.Parameters[0] : null;

      // The locals that hold one lambda for good, are never captured and are only called or passed on:
      // declared with it, or declared without a value and assigned it once in the same block before any
      // statement that names them, as a local function is.
      var held = new Dictionary<IrLocal, Candidate>();
      var reads = IrPassSupport.Reads(body);
      foreach (var block in statements.SelectMany(IrTree.Blocks).Prepend(body).Distinct(ReferenceEqualityComparer.Instance).Cast<IrBlock>())
        for (var index = 0; index < block.Statements.Count; ++index) {
          if (block.Statements[index] is not IrDeclare { Local.Type.IsFunction: true } declare || declarations.GetValueOrDefault(declare.Local) != 1
              || everyLambda.Any(any => any.Captures.Contains(declare.Local)))
            continue;

          IrStmt? definition = null;
          var lambda = declare.Initializer as IrLambda;
          if (declare.Initializer is null && writes.GetValueOrDefault(declare.Local) == 1) {
            var position = index + 1;
            while (position < block.Statements.Count && !IrStatements.Mentions(block.Statements[position], declare.Local))
              ++position;

            if (position < block.Statements.Count && block.Statements[position] is IrAssign { Value: IrLambda value } first && first.Local == declare.Local
                && block.Statements.Skip(position + 1).SelectMany(IrTree.Nodes).OfType<IrLocalRef>().Count(reference => reference.Local == declare.Local) == reads.GetValueOrDefault(declare.Local))
              (lambda, definition) = (value, first);
          } else if (writes.ContainsKey(declare.Local)) {
            lambda = null;
          }

          if (lambda is not null && !this.Leaves(body, declare.Local, false, (call, argument) => !this.Passable(function, call, argument), definition))
            held[declare.Local] = new Candidate(lambda, false);
        }

      // The calls that pass function values to parameters that keep them, and the lambdas called where they stand.
      var sites = new Dictionary<IrCall, List<(int Argument, IrExpr Value)>>(ReferenceEqualityComparer.Instance);
      var invoked = new Dictionary<IrInvoke, Candidate>(ReferenceEqualityComparer.Instance);
      var literals = new Dictionary<IrLambda, Candidate>(ReferenceEqualityComparer.Instance);
      foreach (var node in roots.SelectMany(Proper))
        if (node is IrCall call) {
          var bound = new List<(int, IrExpr)>();
          for (var argument = 0; argument < call.Arguments.Count; ++argument) {
            var value = call.Arguments[argument];
            var source = value is IrLambda || value is IrFunctionRef reference && this.Targets(reference)
              || value is IrLocalRef { Local: var local } && held.ContainsKey(local);
            if (source && this.Passable(function, call, argument)) {
              bound.Add((argument, value));
              if (value is IrLambda lambda)
                literals[lambda] = new Candidate(lambda, true);
            }
          }

          if (bound.Count > 0)
            sites[call] = bound;
        } else if (node is IrInvoke { Callee: IrLambda lambda } invocation) {
          invoked[invocation] = new Candidate(lambda, false);
        }

      if (held.Count == 0 && sites.Count == 0 && invoked.Count == 0)
        return function;

      // What each lambda captures: by value what no lambda writes, in a cell what one does. One that
      // returns a lazy sequence stays: a function of the module that does is an iterator.
      foreach (var candidate in held.Values.Concat(invoked.Values).Concat(literals.Values)) {
        candidate.Accepted = !candidate.Lambda.ReturnType.IsLazySequence;
        foreach (var capture in candidate.Lambda.Captures)
          if (capture == receiver)
            candidate.Accepted = false;
          else if (writtenByLambdas.Contains(capture) && (!candidate.IsLiteral || !CellableIn(function, capture, statements)))
            candidate.Accepted = false;
          else if (writtenByLambdas.Contains(capture))
            candidate.Written.Add(capture);
      }

      // A held lambda that writes what it captures stays a closure of its function, classified as capturing.
      foreach (var (local, candidate) in held)
        if (!candidate.Accepted && candidate.Lambda.Captures.Any(writtenByLambdas.Contains) && !this._scopeFunctions.Any(known => known.Scope == function.Name && known.Name == local.Name && !known.IsLifted))
          this._scopeFunctions.Add(new IrScopeFunction(function.Name, local.Name, IrCaptureClass.Capturing, false));

      // A variable goes into a cell only where every lambda that captures it is lifted, and every call that passes one is made.
      Dictionary<IrCall, List<(int Argument, IrExpr Value)>> made;
      while (true) {
        Settle(outermost, literals);
        made = this.Budget(function, sites, held, literals);
        var dropped = literals.Values.Where(candidate => candidate.Accepted && candidate.Written.Count > 0
          && !made.Values.Any(bound => bound.Any(pair => ReferenceEquals(pair.Value, candidate.Lambda)))).ToList();
        if (dropped.Count == 0)
          break;

        foreach (var candidate in dropped)
          candidate.Accepted = false;
      }

      var cells = literals.Values.Where(candidate => candidate.Accepted).SelectMany(candidate => candidate.Written)
        .Distinct().ToDictionary(variable => variable, variable => new IrLocal(variable.Name, IrType.Array(variable.Type)));
      var liftedHeld = held.Where(pair => pair.Value.Accepted).ToDictionary(pair => pair.Key, pair => this.Lifted(function, pair.Value.Lambda, cells, pair.Key.Name));
      var liftedInvoked = new Dictionary<IrInvoke, Binding>(ReferenceEqualityComparer.Instance);
      foreach (var (invocation, candidate) in invoked.Where(pair => pair.Value.Accepted))
        liftedInvoked[invocation] = this.Lifted(function, candidate.Lambda, cells, "Lambda");
      if (made.Count == 0 && liftedHeld.Count == 0 && liftedInvoked.Count == 0)
        return function;

      // The values passed at the calls that are made, in the original lambdas' identity.
      var bindings = new Dictionary<IrExpr, Binding>(ReferenceEqualityComparer.Instance);
      foreach (var value in made.Values.SelectMany(bound => bound).Select(pair => pair.Value).Distinct(ReferenceEqualityComparer.Instance).Cast<IrExpr>())
        bindings[value] = value switch {
          IrLambda lambda => this.Lifted(function, lambda, cells, "Lambda"),
          IrFunctionRef reference => new Binding(reference.Function, [], []),
          _ => liftedHeld[((IrLocalRef)value).Local]
        };

      var mapper = new Mapper(
        (original, node) => original switch {
          IrLocalRef reference when cells.TryGetValue(reference.Local, out var cell) => new IrArrayLoad(new IrLocalRef(cell), Index),
          IrCall call when made.TryGetValue(call, out var bound) => this.Call((IrCall)node, bound.Select(pair => (pair.Argument, bindings[pair.Value])).ToList()),
          IrInvoke invocation when liftedInvoked.TryGetValue(invocation, out var binding) => Direct((IrInvoke)node, binding),
          IrInvoke { Callee: IrLocalRef callee } when liftedHeld.TryGetValue(callee.Local, out var binding) => Direct((IrInvoke)node, binding),
          _ => null
        },
        (original, statement) => original switch {
          IrDeclare declare when cells.TryGetValue(declare.Local, out var cell) => declare.Initializer is null
            ? [new IrDeclare(cell, new IrNewArray(cell.Type, new IrConst(IrType.I32, 1)))]
            : [new IrDeclare(cell, new IrNewArray(cell.Type, new IrConst(IrType.I32, 1))), new IrStore(new IrLocalRef(cell), Index, ((IrDeclare)statement).Initializer!)],
          IrAssign assign when cells.TryGetValue(assign.Local, out var cell) => [new IrStore(new IrLocalRef(cell), Index, ((IrAssign)statement).Value)],
          _ => null
        },
        variable => cells.GetValueOrDefault(variable, variable),
        position => this._applied(function.Name, position));
      var rewritten = mapper.Block(body);

      // A held local nothing names any more goes with its declaration.
      var named = IrPassSupport.Reads(rewritten);
      var gone = liftedHeld.Keys.Where(local => !named.ContainsKey(local)).ToHashSet();
      if (gone.Count > 0)
        rewritten = Without(rewritten, gone);

      return function with {
        Body = rewritten,
        Locals = [.. function.Locals.Where(local => !gone.Contains(local)).Select(local => cells.GetValueOrDefault(local, local))]
      };
    }

    /// <summary>The nodes of an expression outside the bodies of its lambdas.</summary>
    private static IEnumerable<IrExpr> Proper(IrExpr expression) {
      yield return expression;
      if (expression is IrLambda)
        yield break;

      foreach (var node in IrTree.Operands(expression).SelectMany(Proper))
        yield return node;
    }

    /// <summary>Whether a method group - a function of the module, of the signature the verifier holds it to - may be bound: one that is no instance of a generic function.</summary>
    private bool Targets(IrFunctionRef reference) => this._functions[reference.Function].Origin is null;

    /// <summary>Whether an argument of a call of the function looked at may be bound: the callee is another function whose parameter keeps it.</summary>
    private bool Passable(IrFunction function, IrCall call, int argument) => call.Function != function.Name && !this.Escapes(call.Function, argument);

    /// <summary>
    /// Whether a variable a lambda writes may go into a cell: a number or a truth value the function
    /// declares once, outside its lambdas - so no parameter and no variable a loop or a catch declares -
    /// that nothing binds and no loop copies into every pass.
    /// </summary>
    private static bool CellableIn(IrFunction function, IrLocal variable, IReadOnlyList<IrStmt> statements)
      => Cellable(variable.Type) && statements.OfType<IrDeclare>().Count(declare => declare.Local == variable) == 1
        && !IrPassSupport.Nodes(function.Body).OfType<IrBind>().Any(bind => bind.Local == variable)
        && !statements.OfType<IrFor>().Any(loop => loop.PerIteration.Contains(variable));

    /// <summary>Drops every literal lambda that captures a variable in a cell which a lambda that is not lifted captures too, until none does.</summary>
    private static void Settle(IReadOnlyList<IrLambda> outermost, Dictionary<IrLambda, Candidate> literals) {
      for (var changed = true; changed;) {
        changed = false;
        foreach (var candidate in literals.Values.Where(candidate => candidate.Accepted && candidate.Written.Count > 0))
          if (outermost.Any(lambda => lambda.Captures.Any(candidate.Written.Contains) && !(literals.TryGetValue(lambda, out var other) && other.Accepted))) {
            candidate.Accepted = false;
            changed = true;
          }
      }
    }

    // ------------------------------------------------------------------ copies

    /// <summary>
    /// The calls whose values are bound, with the values that are: those the budget admits, in the
    /// order of the calls, the copies of an earlier call counting against a later one.
    /// </summary>
    private Dictionary<IrCall, List<(int Argument, IrExpr Value)>> Budget(IrFunction function, Dictionary<IrCall, List<(int Argument, IrExpr Value)>> sites, Dictionary<IrLocal, Candidate> held, Dictionary<IrLambda, Candidate> literals) {
      var made = new Dictionary<IrCall, List<(int Argument, IrExpr Value)>>(ReferenceEqualityComparer.Instance);
      var planned = new List<IrClone>();
      foreach (var (call, values) in sites) {
        var bound = values.Where(pair => pair.Value switch {
          IrLambda lambda => literals[lambda].Accepted,
          IrLocalRef reference => held[reference.Local].Accepted,
          _ => true
        }).ToList();
        if (bound.Count == 0)
          continue;

        var needed = new List<IrClone>();
        this.Needed(call.Function, bound.ToDictionary(pair => pair.Argument, pair => this.Identity(pair.Value, held, literals)), needed, planned);
        var refused = needed.FirstOrDefault(clone => this._clones.Concat(planned).Concat(needed.TakeWhile(other => other != clone)).Count(other => other.Origin == clone.Origin) >= this._pass._cloneLimit)
          is { } many ? $"{this._pass._cloneLimit} copies of '{many.Origin}' are the most" : null;
        if (refused is null && this._clones.Concat(planned).Concat(needed).Sum(clone => clone.Size) > this._pass._growthLimit)
          refused = $"the copies would grow the module by more than {this._pass._growthLimit} statements and nodes";

        if (refused is not null) {
          this._note($"{this._pass.Id} {this._pass.Name}: '{function.Name}' passes its function values to '{call.Function}' as they are: {refused}");
          continue;
        }

        planned.AddRange(needed);
        made[call] = bound;
      }

      return made;
    }

    /// <summary>
    /// What identifies a bound value before it is lifted, as the key of a copy names it: the function of
    /// a method group, and for a lambda the function it was lifted into before or else its text.
    /// </summary>
    private string Identity(IrExpr value, Dictionary<IrLocal, Candidate> held, Dictionary<IrLambda, Candidate> literals) {
      if (value is IrFunctionRef reference)
        return reference.Function;

      var candidate = value is IrLambda lambda ? literals[lambda] : held[((IrLocalRef)value).Local];
      var cells = candidate.Written.ToDictionary(variable => variable, variable => new IrLocal(variable.Name, IrType.Array(variable.Type)));
      var text = Text(Form(candidate.Lambda, cells));
      return this.Known().TryGetValue(text, out var name) ? name : "λ" + text;
    }

    /// <summary>The copies a call needs that do not exist yet: of its callee, and of every function the callee passes a bound value on to.</summary>
    private void Needed(string callee, Dictionary<int, string> bound, List<IrClone> needed, List<IrClone> planned) {
      var key = Key(bound);
      if (this.Existing(callee, key) is not null || needed.Concat(planned).Any(clone => clone.Origin == callee && clone.Key == key))
        return;

      var function = this._functions[callee];
      needed.Add(new IrClone("", callee, key, IrPassSupport.Size(function.Body)));
      var parameters = bound.ToDictionary(pair => function.Parameters[pair.Key], pair => pair.Value);
      foreach (var call in IrPassSupport.Nodes(function.Body).OfType<IrCall>()) {
        var passed = Enumerable.Range(0, call.Arguments.Count)
          .Where(index => call.Arguments[index] is IrLocalRef reference && parameters.ContainsKey(reference.Local))
          .ToDictionary(index => index, index => parameters[((IrLocalRef)call.Arguments[index]).Local]);
        if (passed.Count > 0)
          this.Needed(call.Function, passed, needed, planned);
      }
    }

    private static string Key(Dictionary<int, string> bound) => string.Join(";", bound.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    private string? Existing(string callee, string key)
      => this._clones.LastOrDefault(clone => clone.Origin == callee && clone.Key == key && this._functions.ContainsKey(clone.Function))?.Function;

    /// <summary>A call with its bound arguments gone and the values they captured appended, of the copy of its callee for them.</summary>
    private IrCall Call(IrCall call, IReadOnlyList<(int Argument, Binding Binding)> bound) {
      var clone = this.Clone(call.Function, bound.ToDictionary(pair => pair.Argument, pair => pair.Binding));
      this._replaced.Add(call.Function);
      var indices = bound.Select(pair => pair.Argument).ToHashSet();
      var arguments = call.Arguments.Where((_, index) => !indices.Contains(index))
        .Concat(bound.OrderBy(pair => pair.Argument).SelectMany(pair => pair.Binding.Arguments));
      return new IrCall(clone, call.Type, [.. arguments]);
    }

    /// <summary>A call of a lifted lambda where it stands, or through a local that held it: a direct call with what it captured appended.</summary>
    private static IrCall Direct(IrInvoke invocation, Binding binding) => new(binding.Target, invocation.Type, [.. invocation.Arguments, .. binding.Arguments]);

    /// <summary>
    /// The copy of a function bound to function values, made once per callee and values: the bound
    /// parameters gone, the captured values parameters at the end, every call of a bound parameter a
    /// call of its function, and every call that passes one on a call of a copy of that callee.
    /// </summary>
    private string Clone(string callee, Dictionary<int, Binding> bound) {
      var key = Key(bound.ToDictionary(pair => pair.Key, pair => pair.Value.Target));
      if (this.Existing(callee, key) is { } existing)
        return existing;

      var function = this._functions[callee];
      var taken = new HashSet<string>(function.Parameters.Where((_, index) => !bound.ContainsKey(index)).Concat(function.Locals).Select(local => local.Name), StringComparer.Ordinal);
      var captures = bound.OrderBy(pair => pair.Key).ToDictionary(pair => function.Parameters[pair.Key],
        pair => pair.Value with { Arguments = [.. pair.Value.Captures.Select(capture => (IrExpr)new IrLocalRef(new IrLocal(IrPassSupport.Fresh(capture.Name, taken), capture.Type)))] });
      var name = IrPassSupport.Fresh(callee + "_Bound", this.Names());
      // The copy is listed before its body is made: a call in it that passes a bound value on finds it.
      this._clones.Add(new IrClone(name, callee, key, IrPassSupport.Size(function.Body)));
      this._functions[name] = function;
      this._order.Add(name);
      var mapper = new Mapper(
        (_, node) => node switch {
          IrInvoke { Callee: IrLocalRef target } invocation when captures.TryGetValue(target.Local, out var binding) => Direct(invocation, binding),
          IrCall call when Enumerable.Range(0, call.Arguments.Count).Where(index => call.Arguments[index] is IrLocalRef reference && captures.ContainsKey(reference.Local)).ToList() is { Count: > 0 } passed
            => this.Call(call, [.. passed.Select(index => (index, captures[((IrLocalRef)call.Arguments[index]).Local]))]),
          _ => null
        },
        (_, _) => null,
        local => local,
        null);
      var parameters = function.Parameters.Where((_, index) => !bound.ContainsKey(index))
        .Concat(captures.Values.SelectMany(binding => binding.Arguments.Select(argument => ((IrLocalRef)argument).Local)));
      this._functions[name] = new IrFunction(name, false, function.ReturnType, [.. parameters], function.Locals, mapper.Block(function.Body)) { Absent = function.Absent };
      return name;
    }

    private HashSet<string> Names() => new(this._order.Concat(this._module.Constants.Select(constant => constant.Name)).Concat(this._module.Globals.Select(global => global.Name)), StringComparer.Ordinal);

    /// <summary>
    /// A lambda as a function of the module - its parameters, then what it captures, those in cells as
    /// their cells - made once per text, and listed as a function of the scope it was made in.
    /// </summary>
    private Binding Lifted(IrFunction scope, IrLambda lambda, Dictionary<IrLocal, IrLocal> cells, string suffix) {
      var lifted = Form(lambda, cells);
      var captures = lifted.Parameters.Skip(lambda.Parameters.Count).ToList();
      var text = Text(lifted);
      var arguments = captures.Select(capture => (IrExpr)new IrLocalRef(capture)).ToList();
      if (this.Known().TryGetValue(text, out var existing))
        return new Binding(existing, captures, arguments);

      var name = IrPassSupport.Fresh(scope.Name + "_" + suffix, this.Names());
      this._functions[name] = lifted with { Name = name };
      this._order.Add(name);
      this._scopeFunctions.Add(new IrScopeFunction(scope.Name, name, captures.Any(cells.ContainsValue) ? IrCaptureClass.Capturing : IrCaptureClass.Static, true));
      this.Known()[text] = name;

      return new Binding(name, captures, arguments);
    }

    /// <summary>The functions lifted before, by their text without their name.</summary>
    private Dictionary<string, string> Known()
      => this._lifted ??= this._scopeFunctions.Where(known => known.IsLifted && this._functions.ContainsKey(known.Name))
        .GroupBy(known => this.Text(this._functions[known.Name] with { Name = "" })).ToDictionary(group => group.Key, group => group.First().Name);

    /// <summary>A lambda as a function without a name: its parameters, then what it captures, those in cells as their cells, which its body reads and writes through.</summary>
    private static IrFunction Form(IrLambda lambda, Dictionary<IrLocal, IrLocal> cells) {
      var mapper = new Mapper(
        (original, _) => original is IrLocalRef reference && cells.TryGetValue(reference.Local, out var cell) ? new IrArrayLoad(new IrLocalRef(cell), Index) : null,
        (original, statement) => original is IrAssign assign && cells.TryGetValue(assign.Local, out var cell) ? [new IrStore(new IrLocalRef(cell), Index, ((IrAssign)statement).Value)] : null,
        variable => cells.GetValueOrDefault(variable, variable),
        null);
      var captures = lambda.Captures.Select(capture => cells.GetValueOrDefault(capture, capture)).ToList();
      return new IrFunction("", false, lambda.ReturnType, [.. lambda.Parameters, .. captures], lambda.Locals, mapper.Block(lambda.Body));
    }

    /// <summary>The text of a function in the module's format, which equal functions share: the module is verified, so the format writes every node of it.</summary>
    private string Text(IrFunction function) => Convert.ToBase64String(IrModuleFormat.Write(this._module with { Functions = [function] }));

    /// <summary>A block without the declarations and assignments of the variables, at any depth.</summary>
    private static IrBlock Without(IrBlock block, HashSet<IrLocal> variables)
      => new([.. block.Statements.Where(statement => !(statement is IrDeclare declare && variables.Contains(declare.Local) || statement is IrAssign assign && variables.Contains(assign.Local)))
        .Select(statement => statement is IrBlock inner ? Without(inner, variables) : IrTree.With(statement, IrTree.Expressions(statement), [.. IrTree.Blocks(statement).Select(nested => Without(nested, variables))]))]);

  }

}
