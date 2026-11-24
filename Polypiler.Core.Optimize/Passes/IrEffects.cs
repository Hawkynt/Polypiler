using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>What kind of storage a <see cref="IrPlace"/> is.</summary>
public enum IrPlaceKind {

  /// <summary>A variable of the function looked at: one a closure captures, or one an expression of the caller names.</summary>
  Local,

  /// <summary>A global of the module.</summary>
  Global,

  /// <summary>A field of an object, of whichever object: the analysis does not tell objects apart.</summary>
  Field,

  /// <summary>The elements of every array of one element type: the analysis does not tell arrays apart.</summary>
  Elements

}

/// <summary>Storage a function may read or write outside its own variables, named by its kind and its name.</summary>
/// <param name="Kind">What kind of storage it is.</param>
/// <param name="Name">The variable, the global, the field, or the element type of the arrays.</param>
public sealed record IrPlace(IrPlaceKind Kind, string Name) {

  /// <summary>The place as <c>--explain-opt</c> names it: the name, an array type's elements with <c>[]</c> after it.</summary>
  public override string ToString() => this.Kind == IrPlaceKind.Elements ? this.Name + "[]" : this.Name;

}

/// <summary>
/// The effect summary of a function (#1361): what storage outside its own variables it may read and
/// write, whether it makes a new array or object, whether it may trap and whether it may run forever.
/// <para>
/// A function is <b>pure</b> (<see cref="IsPure"/>) when it reads nothing that can change, writes
/// nothing and makes nothing; a call of it with the same arguments gives the same value wherever and
/// however often it runs. It is <b>read-only</b> (<see cref="IsReadOnly"/>) when it reads such
/// storage but writes and makes nothing: two calls give the same value when nothing it reads is
/// written between them. A call may go unevaluated (<see cref="IsRemovable"/>) when the function
/// writes nothing, cannot trap and ends.
/// </para>
/// <para>
/// The summary is computed bottom-up over the calls of the module, the functions that call each other
/// together until nothing changes (<see cref="Of(IrModule, string)"/>); recursion counts as possibly
/// running forever, and so does a loop the analysis finds no bound for. A call through a function value,
/// a method, an iterator, an async function or an await, a native function, a sequence, a collection, a pointer and a function the module
/// does not have is the worst case: it may read and write everything, trap and run forever. Summaries
/// are kept for as long as the list of functions of a module lives; a pass that rewrites a function
/// makes a new list, and the summaries of the new one are computed again.
/// </para>
/// </summary>
public sealed record IrEffects {

  private static readonly IReadOnlySet<IrPlace> _none = new HashSet<IrPlace>();

  /// <summary>No effect at all: pure, total and ending.</summary>
  public static IrEffects None { get; } = new();

  /// <summary>The worst case: everything read and written, an allocation, a trap and no end.</summary>
  public static IrEffects Unknown { get; } = new() { ReadsEverything = true, WritesEverything = true, Allocates = true, MayTrap = true, MayNotTerminate = true };

  /// <summary>The storage it may read, outside its own variables and the tables of the module.</summary>
  public IReadOnlySet<IrPlace> Reads { get; init; } = _none;

  /// <summary>The storage it may write, outside its own variables.</summary>
  public IReadOnlySet<IrPlace> Writes { get; init; } = _none;

  /// <summary>Whether it may read any storage at all.</summary>
  public bool ReadsEverything { get; init; }

  /// <summary>Whether it may write any storage at all.</summary>
  public bool WritesEverything { get; init; }

  /// <summary>Whether it may make a new array, object or closure, whose identity a caller can tell apart.</summary>
  public bool Allocates { get; init; }

  /// <summary>Whether it may trap or throw.</summary>
  public bool MayTrap { get; init; }

  /// <summary>Whether it may run forever: a loop without a bound the analysis proves, recursion, or a call of a function that may.</summary>
  public bool MayNotTerminate { get; init; }

  /// <summary>Whether it reads anything outside its own variables.</summary>
  public bool ReadsAnything => this.ReadsEverything || this.Reads.Count > 0;

  /// <summary>Whether it writes anything outside its own variables.</summary>
  public bool WritesAnything => this.WritesEverything || this.Writes.Count > 0;

  /// <summary>Whether it reads nothing that can change, writes nothing and makes nothing: its value depends on its arguments alone.</summary>
  public bool IsPure => !this.ReadsAnything && this.IsReadOnly;

  /// <summary>Whether it writes nothing and makes nothing, whatever it reads.</summary>
  public bool IsReadOnly => !this.WritesAnything && !this.Allocates;

  /// <summary>Whether a call whose value nobody reads may go: it writes nothing, cannot trap and ends.</summary>
  public bool IsRemovable => !this.WritesAnything && !this.MayTrap && !this.MayNotTerminate;

  /// <summary>Whether it may write something another summary reads: then the two do not commute.</summary>
  /// <param name="reader">The summary of what is read.</param>
  public bool WritesWhatIsRead(IrEffects reader) {
    ArgumentNullException.ThrowIfNull(reader);
    if (!reader.ReadsAnything || !this.WritesAnything)
      return false;

    return this.WritesEverything || reader.ReadsEverything || this.Writes.Overlaps(reader.Reads);
  }

  /// <summary>Both summaries at once: what either may do.</summary>
  /// <param name="other">The other summary.</param>
  public IrEffects With(IrEffects other) {
    ArgumentNullException.ThrowIfNull(other);
    return new() {
      Reads = Union(this.Reads, other.Reads),
      Writes = Union(this.Writes, other.Writes),
      ReadsEverything = this.ReadsEverything || other.ReadsEverything,
      WritesEverything = this.WritesEverything || other.WritesEverything,
      Allocates = this.Allocates || other.Allocates,
      MayTrap = this.MayTrap || other.MayTrap,
      MayNotTerminate = this.MayNotTerminate || other.MayNotTerminate
    };
  }

  private static IReadOnlySet<IrPlace> Union(IReadOnlySet<IrPlace> left, IReadOnlySet<IrPlace> right)
    => right.Count == 0 || left.IsSupersetOf(right) ? left : left.Count == 0 ? right : new HashSet<IrPlace>(left.Concat(right));

  /// <summary>Whether two summaries say the same.</summary>
  /// <param name="other">The other summary.</param>
  public bool Same(IrEffects other) {
    ArgumentNullException.ThrowIfNull(other);
    return this.Reads.SetEquals(other.Reads) && this.Writes.SetEquals(other.Writes) && this.ReadsEverything == other.ReadsEverything
      && this.WritesEverything == other.WritesEverything && this.Allocates == other.Allocates && this.MayTrap == other.MayTrap && this.MayNotTerminate == other.MayNotTerminate;
  }

  /// <summary>The kinds of effect it has, as a function of a scope records them (<see cref="IrScopeFunction.Effects"/>).</summary>
  public IrEffectKinds Kinds
    => (this.ReadsAnything ? IrEffectKinds.Reads : 0) | (this.WritesAnything ? IrEffectKinds.Writes : 0) | (this.Allocates ? IrEffectKinds.Allocates : 0)
      | (this.MayTrap ? IrEffectKinds.Traps : 0) | (this.MayNotTerminate ? IrEffectKinds.MayNotTerminate : 0);

  /// <summary>The summary as <c>--explain-opt</c> names it: <c>pure</c>, <c>reads k</c>, <c>writes n[]</c>, then whether it traps or may not end.</summary>
  public override string ToString() => Describe(this.Kinds) + Places(" reading ", this.ReadsEverything, this.Reads) + Places(" writing ", this.WritesEverything, this.Writes);

  /// <summary>The kinds of effect as words: <c>pure</c>, <c>reads</c> or <c>writes</c>, and <c>allocates</c>, <c>traps</c> and <c>may not end</c> as they apply.</summary>
  /// <param name="kinds">The kinds.</param>
  public static string Describe(IrEffectKinds kinds) {
    var changes = kinds.HasFlag(IrEffectKinds.Writes) ? "writes" : kinds.HasFlag(IrEffectKinds.Reads) ? "reads" : null;
    var words = new List<string> { changes ?? (kinds.HasFlag(IrEffectKinds.Allocates) ? "allocates" : "pure") };
    if (changes is not null && kinds.HasFlag(IrEffectKinds.Allocates))
      words.Add("allocates");

    if (kinds.HasFlag(IrEffectKinds.Traps))
      words.Add("traps");

    if (kinds.HasFlag(IrEffectKinds.MayNotTerminate))
      words.Add("may not end");

    return string.Join(", ", words);
  }

  private static string Places(string verb, bool everything, IReadOnlySet<IrPlace> places)
    => everything ? verb + "anything" : places.Count == 0 ? "" : verb + string.Join(", ", places.Select(place => place.ToString()).Order(StringComparer.Ordinal));

  // ---------------------------------------------------------------- the summaries of a module

  private static readonly ConditionalWeakTable<IReadOnlyList<IrFunction>, IrEffectTable> _tables = [];

  /// <summary>The summaries of the functions of a module, kept for as long as its list of functions lives.</summary>
  /// <param name="module">The module.</param>
  public static IrEffectTable Of(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return _tables.GetValue(module.Functions, functions => new IrEffectTable(functions));
  }

  /// <summary>The summary of a function of a module; the worst case for a name the module has no function of.</summary>
  /// <param name="module">The module.</param>
  /// <param name="function">The name of the function.</param>
  public static IrEffects Of(IrModule module, string function) => Of(module).Function(function);

}

/// <summary>
/// The effect summaries of the functions of one list, each computed once when it is first asked for:
/// the functions that call each other (a strongly connected part of the call graph) together, from the
/// worst-free start until nothing changes, after every function they call.
/// </summary>
public sealed class IrEffectTable {

  private readonly Dictionary<string, IrFunction> _functions;
  private readonly Dictionary<string, IrEffects> _done = new(StringComparer.Ordinal);
  private readonly ConditionalWeakTable<IrExpr, StrongBox<bool>> _purity = [];

  internal IrEffectTable(IReadOnlyList<IrFunction> functions) {
    this._functions = new(StringComparer.Ordinal);
    foreach (var function in functions)
      this._functions[function.Name] = function;
  }

  /// <summary>The summary of a function; the worst case for a name the list has no function of.</summary>
  /// <param name="name">The name of the function.</param>
  public IrEffects Function(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (this._done.TryGetValue(name, out var known))
      return known;

    if (!this._functions.ContainsKey(name))
      return IrEffects.Unknown;

    new Components(this).Visit(name);
    return this._done[name];
  }

  /// <summary>
  /// The summary of a closure's body, run where it is called: what it reads and writes of the variables
  /// it captures counts as storage of its scope (<see cref="IrPlaceKind.Local"/>).
  /// </summary>
  /// <param name="lambda">The closure.</param>
  public IrEffects Lambda(IrLambda lambda) {
    ArgumentNullException.ThrowIfNull(lambda);
    var captures = lambda.Captures.ToHashSet();
    return new Walker(this.Function, captures.Contains).Block(lambda.Body).Result;
  }

  /// <summary>What evaluating an expression of a function may do, its variables included (<see cref="IrPlaceKind.Local"/>); the bodies of its closures do not run.</summary>
  /// <param name="expression">The expression.</param>
  public IrEffects Expression(IrExpr expression) {
    ArgumentNullException.ThrowIfNull(expression);
    var walker = new Walker(this.Function, _ => true);
    walker.Expression(expression);
    return walker.Result;
  }

  /// <summary>What one node of an expression of a function may do itself, its operands aside, its variables included.</summary>
  /// <param name="node">The node.</param>
  public IrEffects Node(IrExpr node) {
    ArgumentNullException.ThrowIfNull(node);
    var walker = new Walker(this.Function, _ => true);
    walker.Node(node);
    return walker.Result;
  }

  /// <summary>
  /// Whether an expression may be evaluated anywhere, any number of times or not at all: every node has
  /// no effect and never traps (<see cref="IrPassSupport.IsPure(IrExpr)"/>), or is a call of a function
  /// that is pure, cannot trap and ends.
  /// </summary>
  /// <param name="expression">The expression.</param>
  public bool IsPure(IrExpr expression) {
    ArgumentNullException.ThrowIfNull(expression);
    if (IrPassSupport.IsPure(expression))
      return true;

    if (this._purity.TryGetValue(expression, out var known))
      return known.Value;

    var pure = (expression is IrCall call ? this.Function(call.Function) is { IsPure: true, MayTrap: false, MayNotTerminate: false }
      : IrShape.IsSideEffectFreeNode(expression) && IrShape.IsTrapFreeNode(expression)) && IrTree.Operands(expression).All(this.IsPure);
    this._purity.AddOrUpdate(expression, new StrongBox<bool>(pure));
    return pure;
  }

  /// <summary>
  /// Whether an expression whose value nobody reads may go unevaluated: it is pure
  /// (<see cref="IsPure(IrExpr)"/>), or its calls are of functions that write nothing, cannot trap and
  /// end (<see cref="IrEffects.IsRemovable"/>) and its other nodes have no effect and never trap.
  /// </summary>
  /// <param name="expression">The expression.</param>
  public bool IsRemovable(IrExpr expression) {
    ArgumentNullException.ThrowIfNull(expression);
    if (this.IsPure(expression))
      return true;

    return (expression is IrCall call ? this.Function(call.Function).IsRemovable : IrShape.IsSideEffectFreeNode(expression) && IrShape.IsTrapFreeNode(expression))
      && IrTree.Operands(expression).All(this.IsRemovable);
  }

  /// <summary>The functions a function calls directly, outside the bodies of its closures, which do not run where they are made.</summary>
  private IEnumerable<string> Callees(IrFunction function)
    => function.Body.Statements.SelectMany(Proper).OfType<IrCall>().Select(call => call.Function).Where(this._functions.ContainsKey).Distinct(StringComparer.Ordinal);

  /// <summary>The expression nodes of a statement and its blocks, outside the bodies of closures.</summary>
  private static IEnumerable<IrExpr> Proper(IrStmt statement) {
    var inner = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements);
    return IrTree.Expressions(statement).SelectMany(Proper).Concat(inner.SelectMany(Proper));
  }

  private static IEnumerable<IrExpr> Proper(IrExpr expression) {
    var pending = new Stack<IrExpr>([expression]);
    while (pending.Count > 0) {
      var node = pending.Pop();
      yield return node;
      if (node is not IrLambda)
        foreach (var operand in IrTree.Operands(node))
          pending.Push(operand);
    }
  }

  /// <summary>The strongly connected parts of the call graph below a function (Tarjan), each summarised once all it calls is.</summary>
  private sealed class Components(IrEffectTable table) {

    private readonly Dictionary<string, (int Index, int Low)> _marks = new(StringComparer.Ordinal);
    private readonly Stack<string> _stack = new();
    private readonly HashSet<string> _onStack = new(StringComparer.Ordinal);

    public void Visit(string name) {
      var index = this._marks.Count;
      this._marks[name] = (index, index);
      this._stack.Push(name);
      this._onStack.Add(name);
      var callees = table.Callees(table._functions[name]).ToList();
      foreach (var callee in callees.Where(callee => !table._done.ContainsKey(callee))) {
        if (!this._marks.ContainsKey(callee)) {
          this.Visit(callee);
          this._marks[name] = (index, Math.Min(this._marks[name].Low, this._marks[callee].Low));
        } else if (this._onStack.Contains(callee)) {
          this._marks[name] = (index, Math.Min(this._marks[name].Low, this._marks[callee].Index));
        }
      }

      if (this._marks[name].Low != index)
        return;

      var members = new List<string>();
      string member;
      do {
        member = this._stack.Pop();
        this._onStack.Remove(member);
        members.Add(member);
      } while (member != name);

      table.Summarise(members, members.Count > 1 || callees.Contains(name, StringComparer.Ordinal));
    }

  }

  /// <summary>The summaries of functions that call each other: from no effect up, every member walked again until none changes.</summary>
  private void Summarise(IReadOnlyList<string> members, bool recursive) {
    var current = members.ToDictionary(member => member, _ => recursive ? IrEffects.None with { MayNotTerminate = true } : IrEffects.None, StringComparer.Ordinal);
    IrEffects Lookup(string name) => current.TryGetValue(name, out var inside) ? inside : this.Function(name);
    for (var changed = true; changed;) {
      changed = false;
      foreach (var member in members) {
        var function = this._functions[member];
        var summary = Opaque(function) ? IrEffects.Unknown : new Walker(Lookup, _ => false).Block(function.Body).Result;
        if (recursive)
          summary = summary with { MayNotTerminate = true };

        if (summary.Same(current[member]))
          continue;

        current[member] = summary;
        changed = true;
      }
    }

    foreach (var (name, summary) in current)
      this._done[name] = summary;
  }

  /// <summary>
  /// Whether a function does not run to its end where it is called: an iterator, whose effects happen as
  /// its sequence is read, and an async function (#1330), whose effects happen as it is awaited.
  /// </summary>
  private static bool Opaque(IrFunction function) => function.IsAsync || IrSequences.IsIterator(function) || function.Body.Statements.Any(IrSequences.Suspends);

  /// <summary>Gathers the effects of a body, statement by statement and node by node, outside the bodies of closures.</summary>
  private sealed class Walker(Func<string, IrEffects> callee, Func<IrLocal, bool> counts) {

    private readonly HashSet<IrPlace> _reads = [];
    private readonly HashSet<IrPlace> _writes = [];
    private bool _readsEverything;
    private bool _writesEverything;
    private bool _allocates;
    private bool _mayTrap;
    private bool _mayNotTerminate;

    public IrEffects Result => new() {
      Reads = this._reads,
      Writes = this._writes,
      ReadsEverything = this._readsEverything,
      WritesEverything = this._writesEverything,
      Allocates = this._allocates,
      MayTrap = this._mayTrap,
      MayNotTerminate = this._mayNotTerminate
    };

    private void Worst() => this.Add(IrEffects.Unknown);

    private void Add(IrEffects effects) {
      this._reads.UnionWith(effects.Reads);
      this._writes.UnionWith(effects.Writes);
      this._readsEverything |= effects.ReadsEverything;
      this._writesEverything |= effects.WritesEverything;
      this._allocates |= effects.Allocates;
      this._mayTrap |= effects.MayTrap;
      this._mayNotTerminate |= effects.MayNotTerminate;
    }

    public Walker Block(IrBlock block) {
      foreach (var statement in block.Statements)
        this.Statement(statement);

      return this;
    }

    private void Statement(IrStmt statement) {
      if (statement is IrThrow thrown) {
        this.Thrown(thrown.Exception);
        return;
      }

      foreach (var expression in IrTree.Expressions(statement))
        this.Expression(expression);

      switch (statement) {
        case IrDeclare declare:
          this.Local(declare.Local, true);
          break;
        case IrAssign assign:
          this.Local(assign.Local, true);
          break;
        case IrGlobalAssign assign:
          this._writes.Add(new IrPlace(IrPlaceKind.Global, assign.Global.Name));
          break;
        case IrStore store:
          this._mayTrap = true;
          this._writes.Add(Elements(store.Array.Type));
          break;
        case IrFieldStore store:
          this.Store(new IrFieldLoad(store.Target, store.Field));
          break;
        case IrWhile or IrDoWhile or IrFor when !Bounded(statement):
          this._mayNotTerminate = true;
          break;
        case IrWriteLine or IrForEachCollection or IrForEachSequence or IrUsing or IrYield or IrYieldFrom:
          this.Worst();
          break;
      }

      foreach (var block in IrTree.Blocks(statement))
        this.Block(block);
    }

    /// <summary>A write of a place: a variable, an array element, a field of an object or of a struct, a global; the worst case for any other.</summary>
    private void Store(IrExpr place) {
      switch (place) {
        case IrLocalRef reference:
          this.Local(reference.Local, true);
          break;
        case IrArrayLoad load:
          this._mayTrap = true;
          this._writes.Add(Elements(load.Array.Type));
          break;
        case IrFieldLoad load when load.Target.Type.IsObject:
          this._mayTrap = true;
          this._writes.Add(new IrPlace(IrPlaceKind.Field, load.Field.Name));
          break;
        case IrFieldLoad load:
          // A field of a struct is part of where the struct is.
          this.Store(load.Target);
          break;
        case IrGlobalRef reference:
          this._writes.Add(new IrPlace(IrPlaceKind.Global, reference.Global.Name));
          break;
        default:
          this._writesEverything = true;
          break;
      }
    }

    private void Local(IrLocal local, bool write) {
      if (!counts(local))
        return;

      (write ? this._writes : this._reads).Add(new IrPlace(IrPlaceKind.Local, local.Name));
    }

    private static IrPlace Elements(IrType array) => new(IrPlaceKind.Elements, array.Element!.ToString());

    /// <summary>An exception thrown: a trap; a new one of the library's classes is made only to be thrown, which no caller that goes on sees.</summary>
    private void Thrown(IrExpr exception) {
      this._mayTrap = true;
      if (exception is IrNew { Type.ClassType.IsLibrary: true } creation)
        foreach (var argument in creation.Arguments)
          this.Expression(argument);
      else
        this.Expression(exception);
    }

    public void Expression(IrExpr expression) {
      if (expression is IrThrowExpression thrown) {
        this.Thrown(thrown.Exception);
        return;
      }

      // Operands first, as they are evaluated; the body of a closure runs only where it is called.
      if (expression is not IrLambda)
        foreach (var operand in IrTree.Operands(expression))
          this.Expression(operand);

      this.Node(expression);
    }

    public void Node(IrExpr node) {
      switch (node) {
        case IrLocalRef reference:
          this.Local(reference.Local, false);
          return;
        case IrArrayLoad load:
          this._mayTrap = true;
          // A table of the module is never written.
          if (load.Array is not IrConstRef)
            this._reads.Add(Elements(load.Array.Type));

          return;
        case IrFieldLoad load when load.Target.Type.IsObject:
          this._mayTrap = true;
          this._reads.Add(new IrPlace(IrPlaceKind.Field, load.Field.Name));
          return;
        case IrGlobalRef reference:
          this._reads.Add(new IrPlace(IrPlaceKind.Global, reference.Global.Name));
          return;
        case IrCall call:
          this.Add(callee(call.Function));
          return;
        case IrStep step:
          this._mayTrap |= step.Trap;
          this.Store(step.Target);
          return;
        case IrBind bind:
          this.Local(bind.Local, true);
          return;
        case IrNewArray:
          this._mayTrap = true;
          this._allocates = true;
          return;
        case IrLambda or IrBoundMethod:
          this._allocates = true;
          return;
        case IrIntrinsic intrinsic:
          this.Intrinsic(intrinsic);
          return;
        case IrNew creation:
          // A class of the library - an exception - only keeps what it is given; one of the module runs its constructor.
          this._allocates = true;
          if (!creation.Type.ClassType!.IsLibrary)
            this.Add(callee(creation.Constructor.Function));

          return;
        case IrInvoke or IrMethodCall or IrSequence or IrArraySequence or IrCollection or IrAwait or IrAsync or IrForeignCall or IrLastError:
          this.Worst();
          return;
        case IrArrayLength or IrConstRef or IrStrConst or IrNull or IrStructNew or IrStructDefault or IrFieldLoad or IrFunctionRef
          or IrTypeTest or IrReferenceEquals or IrIsNull or IrIsNone or IrSome or IrNone or IrCoalesce or IrStructEquals:
          this._mayTrap |= node is IrArrayLength;
          return;
        default:
          this._mayTrap |= !IrShape.IsTrapFreeNode(node);
          return;
      }
    }

    /// <summary>
    /// An operation of the IR: what it reads of the arrays it is given, a new array or view it makes,
    /// and the worst case for one that writes - it returns nothing - or works on memory or pointers.
    /// </summary>
    private void Intrinsic(IrIntrinsic intrinsic) {
      this._mayTrap |= !IrShape.IsTrapFreeNode(intrinsic);
      if (intrinsic.Type == IrType.Void || intrinsic.Type.IsPointer || intrinsic.Arguments.Any(argument => argument.Type.IsView || argument.Type.IsMemory || argument.Type.IsPointer)) {
        this.Worst();
        return;
      }

      foreach (var argument in intrinsic.Arguments.Where(argument => argument.Type.IsArray))
        this._reads.Add(Elements(argument.Type));

      this._allocates |= intrinsic.Type.IsArray || intrinsic.Type.IsView || intrinsic.Type.IsMemory;
    }

  }

  /// <summary>
  /// Whether a loop ends for certain: it counts a variable of integers up by one while it is less than a
  /// bound, or down by one while it is greater, the bound made of constants, of variables the loop
  /// does not write and of the lengths of their arrays; the variable is written once in the loop, by its
  /// step - the last statement of the body of a while that has no continue, or the step of a for - and
  /// no closure captures it. Each pass then brings the variable one closer to the bound it cannot step
  /// over, so it cannot wrap around.
  /// </summary>
  internal static bool Bounded(IrStmt loop) {
    var (condition, body, step) = loop switch {
      IrWhile run => (run.Condition, run.Body, null),
      IrFor run => (run.Condition, run.Body, run.Step),
      _ => (null, null, (IrBlock?)null)
    };
    if (condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.Greater, Left: IrLocalRef { Local: var counter } } compare || !counter.Type.IsInteger)
      return false;

    var everything = new IrBlock([new IrExprStmt(condition), body!, .. step is null ? Array.Empty<IrStmt>() : [step]]);
    var writes = IrPassSupport.Writes(everything);
    if (writes.GetValueOrDefault(counter) != 1 || IrPassSupport.Nodes(everything).OfType<IrLambda>().Any(lambda => lambda.Captures.Contains(counter)) || !Invariant(compare.Right, writes))
      return false;

    var last = step is null ? body!.Statements.LastOrDefault() : step.Statements.LastOrDefault();
    if (step is null && IrPassSupport.Statements(body!).Any(statement => statement is IrContinue))
      return false;

    var up = compare.Op == IrBinaryOp.Less;
    return last switch {
      IrAssign { Value: IrBinary { Left: IrLocalRef { Local: var stepped }, Right: IrConst { Value: 1 } } next } assign
        => assign.Local == counter && stepped == counter && (up ? next.Op is IrBinaryOp.Add or IrBinaryOp.AddChecked : next.Op is IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked),
      IrExprStmt { Expression: IrStep { Target: IrLocalRef { Local: var stepped } } stepping } => stepped == counter && stepping.Increment == up,
      _ => false
    };
  }

  /// <summary>Whether a bound stays the same while the loop runs: constants, variables it does not write and the lengths of their arrays, in arithmetic that wraps.</summary>
  private static bool Invariant(IrExpr bound, Dictionary<IrLocal, int> writes) => bound switch {
    IrConst => true,
    IrLocalRef reference => !writes.ContainsKey(reference.Local),
    IrArrayLength { Array: IrLocalRef reference } => !writes.ContainsKey(reference.Local),
    IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical } binary
      => Invariant(binary.Left, writes) && Invariant(binary.Right, writes),
    IrConvert { Contract: IrConversion.Wrap } conversion => Invariant(conversion.Operand, writes),
    _ => false
  };

}
