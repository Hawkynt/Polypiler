using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>Who holds an array a variable or a result of a function names.</summary>
internal enum ArrayHolder {

  /// <summary>Not an array.</summary>
  None,

  /// <summary>The variable owns it: a <c>Vec&lt;T&gt;</c>, created in the function or given back by a call that created it.</summary>
  Owned,

  /// <summary>The variable borrows it from a parameter: <c>&amp;[T]</c> or <c>&amp;mut [T]</c>.</summary>
  Borrowed

}

/// <summary>What one function does with arrays: which parameters it writes, what its result is, and who holds the array of each local.</summary>
internal sealed class FunctionArrays {

  /// <summary>The indices of the array parameters the function writes, directly or through a callee: <c>&amp;mut [T]</c>.</summary>
  public SortedSet<int> MutableParameters { get; } = [];

  /// <summary>The indices of the array parameters the function assigns a new array to: never written, they are copied into a vector of their own.</summary>
  public SortedSet<int> CopiedParameters { get; } = [];

  /// <summary>The indices of the pointer parameters the function writes through, directly or through a callee: a <c>&amp;mut [T]</c> region.</summary>
  public SortedSet<int> MutablePointers { get; } = [];

  /// <summary>Who holds the array the function returns.</summary>
  public ArrayHolder Result { get; set; }

  /// <summary>The indices of the parameters a borrowed result may borrow from.</summary>
  public SortedSet<int> ResultParameters { get; } = [];

  /// <summary>Who holds the array of each array local.</summary>
  public Dictionary<IrLocal, ArrayHolder> Locals { get; } = [];

  /// <summary>The array locals that are written: a <c>let mut</c> vector or a <c>&amp;mut</c> borrow.</summary>
  public HashSet<IrLocal> MutableLocals { get; } = [];

  /// <summary>The array locals that name the array a field of an object holds, each with the load it is declared with.</summary>
  public Dictionary<IrLocal, IrFieldLoad> FieldAliases { get; } = [];

  /// <summary>The variables whose fields' arrays the function writes in place (<c>o.items[i] = v</c>), directly or through a callee.</summary>
  public HashSet<IrLocal> WrittenFieldTargets { get; } = [];

  /// <summary>
  /// The declarations and assignments that move an array from one variable into another: an array the
  /// function made, which the source variable never names again, so the vector changes owner instead of
  /// being shared or cloned.
  /// </summary>
  public HashSet<IrStmt> Moves { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>Why the arrays of the function have no safe Rust form, or <see langword="null"/>.</summary>
  public string? Problem { get; set; }

  /// <summary>The same facts, for the fixed point to compare.</summary>
  public string Key => $"{string.Join(",", this.MutableParameters)}|{this.Result}|{string.Join(",", this.ResultParameters)}|{string.Join(",", this.MutablePointers)}";

}

/// <summary>
/// The ownership analysis of arrays, which decides the Rust form of every array a module names. An
/// array of the IR is a reference that several variables may share; Rust lets one owner hold it and
/// others borrow it. The variables of a function that one array may flow into are grouped (an
/// assignment, a declaration, a return, a call that gives back a borrowed parameter join their
/// groups); a group holds a new array (<c>Vec&lt;T&gt;</c>), a parameter's (<c>&amp;[T]</c>, or
/// <c>&amp;mut [T]</c> where any variable of the group is written), never both. A group that is
/// written and has more than one owner, or an owned and a borrowed array, is refused: safe Rust has
/// no shared mutable array; one nothing writes is a value, copied where it changes hands. A constant table is never in a group: the IR reads its elements only, and lets no variable
/// hold it. Parameters written by a callee are written by the caller, and a borrowed result a caller
/// writes through is exclusive; the facts of the functions are computed to their fixed point.
/// </summary>
internal static class ArrayOwnership {

  /// <summary>The array a view of it is made of, through views of views, or <see langword="null"/>.</summary>
  private static IrExpr? ViewRoot(IrExpr view) => view switch {
    IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice or IrIntrinsicOp.Reinterpret } operation => operation.Arguments[0].Type.IsArray ? operation.Arguments[0] : ViewRoot(operation.Arguments[0]),
    _ => null
  };

  /// <summary>
  /// Whether a function writes the elements of a writable view variable: a store, a step, a copy into it or a
  /// fill of it, through the variable or a view made of it - directly, or held by another view variable the
  /// function writes.
  /// </summary>
  internal static bool WritesView(IrFunction function, IrLocal local) => WritesView(function, local, []);

  private static bool WritesView(IrFunction function, IrLocal local, HashSet<IrLocal> seen) {
    if (local.Type.Kind != IrTypeKind.Span || !seen.Add(local))
      return false;
    if (function.Locals.Any(variable => variable.Type.IsPointer) && PointerRegions.Of(function).Written.Any(region => region.Kind == RegionKind.Variable && region.Owner == local))
      return true;

    bool Reaches(IrExpr view) => view is IrLocalRef reference && reference.Local == local
      || view is IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice or IrIntrinsicOp.Reinterpret } operation && Reaches(operation.Arguments[0]);
    return Statements(function.Body).Any(statement => statement switch {
      IrStore { Array: var target } => Reaches(target),
      IrExprStmt { Expression: IrStep { Target: IrArrayLoad { Array: var target } } } => Reaches(target),
      IrDeclare { Local: var derived, Initializer: { } view } when derived != local && Reaches(view) => WritesView(function, derived, seen),
      IrAssign { Local: var derived, Value: var view } when derived != local && Reaches(view) => WritesView(function, derived, seen),
      _ => false
    } || IrTree.Expressions(statement).SelectMany(IrTree.Nodes).Any(node => node is IrIntrinsic { Op: IrIntrinsicOp.ViewCopy or IrIntrinsicOp.ViewTryCopy or IrIntrinsicOp.ViewFill } operation
      && Reaches(operation.Arguments[operation.Op == IrIntrinsicOp.ViewFill ? 0 : 1])));
  }

  private static readonly object _result = new();

  /// <summary>The facts of every function of a module, by name.</summary>
  public static IReadOnlyDictionary<string, FunctionArrays> Of(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var facts = module.Functions.ToDictionary(function => function.Name, _ => new FunctionArrays(), StringComparer.Ordinal);
    // The functions whose borrowed result a caller writes through: their result is an exclusive borrow.
    var writtenResults = new HashSet<string>(StringComparer.Ordinal);
    var byName = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    var foreignByName = module.ForeignFunctions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    for (var round = 0; round <= 2 * module.Functions.Count + 1; ++round) {
      var changed = false;
      foreach (var function in module.Functions) {
        var before = facts[function.Name].Key;
        var results = writtenResults.Count;
        facts[function.Name] = Analyse(function, facts, writtenResults, byName, foreignByName);
        changed |= facts[function.Name].Key != before || writtenResults.Count != results;
      }

      if (!changed)
        break;
    }

    return facts;
  }

  /// <summary>Every statement of a block and of the blocks inside it, outer ones first.</summary>
  public static IEnumerable<IrStmt> Statements(IrBlock block) {
    foreach (var statement in block.Statements) {
      yield return statement;
      foreach (var inner in IrTree.Blocks(statement))
        foreach (var nested in Statements(inner))
          yield return nested;
    }
  }

  private static FunctionArrays Analyse(IrFunction function, IReadOnlyDictionary<string, FunctionArrays> facts, HashSet<string> writtenResults, IReadOnlyDictionary<string, IrFunction> byName, IReadOnlyDictionary<string, IrForeignFunction> foreignByName) {
    var first = Analyse(function, facts, writtenResults, byName, foreignByName, null);
    var moves = first.Moves.Count == 0 ? null : first.Moves;
    return moves is null ? first : Analyse(function, facts, writtenResults, byName, foreignByName, moves);
  }

  /// <summary>
  /// The statements, in the order of the function, outer ones first, each with whether a loop holds it.
  /// </summary>
  private static IEnumerable<(IrStmt Statement, bool InLoop)> Ordered(IrBlock block, bool inLoop) {
    foreach (var statement in block.Statements) {
      yield return (statement, inLoop);
      var loop = inLoop || statement is IrWhile or IrDoWhile or IrFor;
      foreach (var inner in IrTree.Blocks(statement))
        foreach (var nested in Ordered(inner, loop))
          yield return nested;
    }
  }

  /// <summary>Whether a statement names a variable in its own expressions, declares or assigns it.</summary>
  private static bool Names(IrStmt statement, IrLocal local)
    => statement is IrDeclare declare && declare.Local == local || statement is IrAssign assign && assign.Local == local
      || IrTree.Expressions(statement).SelectMany(IrTree.Nodes).Any(node => node is IrLocalRef reference && reference.Local == local);

  /// <summary>
  /// The moves of a function among the links of its first pass: <c>x = y</c> of two array locals, outside every
  /// loop, where <c>y</c> holds an array the function made with no parameter in its group, and nothing after the
  /// statement names <c>y</c>.
  /// </summary>
  private static void FindMoves(IrFunction function, FunctionArrays result, Func<object, object> find, HashSet<object> owned) {
    var ordered = Ordered(function.Body, false).ToList();
    for (var index = 0; index < ordered.Count; ++index) {
      var (statement, inLoop) = ordered[index];
      var (target, source) = MoveOf(statement);
      if (target is null || source is null || inLoop || target == source || function.Parameters.Contains(target) || function.Parameters.Contains(source)
          || result.FieldAliases.ContainsKey(source) || result.FieldAliases.ContainsKey(target)
          || !owned.Contains(find(source)) || function.Parameters.Any(parameter => find(parameter) == find(source))
          || ordered.Skip(index + 1).Any(after => Names(after.Statement, source)))
        continue;

      result.Moves.Add(statement);
    }
  }

  /// <summary>The variables of a statement that gives one array variable another's array, <c>x = y</c>; nulls for any other statement.</summary>
  internal static (IrLocal? Target, IrLocal? Source) MoveOf(IrStmt statement) => statement switch {
    IrDeclare { Local.Type.IsArray: true, Initializer: IrLocalRef { Local: var from } } declare => (declare.Local, from),
    IrAssign { Local.Type.IsArray: true, Value: IrLocalRef { Local: var from } } assign => (assign.Local, from),
    _ => (null, null)
  };

  private static FunctionArrays Analyse(IrFunction function, IReadOnlyDictionary<string, FunctionArrays> facts, HashSet<string> writtenResults, IReadOnlyDictionary<string, IrFunction> byName, IReadOnlyDictionary<string, IrForeignFunction> foreignByName, HashSet<IrStmt>? moves) {
    var result = new FunctionArrays();
    var groups = new Groups();
    // The same unions but for the moves, a variable given the array of one read for the last time: one owner after the other, never two at once.
    var shared = new Groups();
    IrStmt? current = null;
    var written = new List<object>();
    var created = new List<object>();
    var problems = new List<string>();
    // The variables given a choice of a field's array and another (#1422): each holds a copy of the field's, as a read of the field as a whole does.
    var copies = new List<object>();

    void Link(object node, IrExpr value) {
      switch (value) {
        case IrLocalRef reference:
          groups.Union(node, reference.Local);
          if (!(node is IrLocal && current is IrDeclare or IrAssign && Moves.IsLast(function.Body, current, reference.Local)))
            shared.Union(node, reference.Local);
          break;
        case IrNewArray:
        case IrIntrinsic { Op: IrIntrinsicOp.ArrayOf or IrIntrinsicOp.StrToCodeUnits or IrIntrinsicOp.StrSplit or IrIntrinsicOp.ViewToArray or IrIntrinsicOp.EnumNames or IrIntrinsicOp.EnumValues }:
          created.Add(node);
          break;
        case IrCall call when facts.TryGetValue(call.Function, out var callee):
          if (callee.Result == ArrayHolder.Borrowed)
            foreach (var index in callee.ResultParameters)
              Link(node, call.Arguments[index]);
          else
            created.Add(node);
          break;
        case IrConditional choice:
          foreach (var branch in new[] { choice.WhenTrue, choice.WhenFalse }) {
            if (branch is IrFieldLoad { Target.Type.IsObject: true } && node is IrLocal) {
              created.Add(node);
              copies.Add(node);
            } else {
              Link(node, branch);
            }
          }
          break;
        case IrCollection { Op: IrCollectionOp.ListCopyToArray }:
          created.Add(node);
          break;
        case IrFieldLoad { Target: { Type.IsObject: true } or IrLocalRef { Type.IsStruct: true } } load when node is IrLocal alias:
          // A variable that names a field's array, as `var s = this._s;`: it borrows the field's cell.
          if (!result.FieldAliases.TryAdd(alias, load))
            problems.Add($"the variable '{alias.Name}' given the arrays of two fields");
          break;
        case IrNull:
          // Null holds no array: the variable is an Option, which ArrayNullness finds.
          break;
        case IrAwait { Operand: IrAsync { Op: IrAsyncOp.WhenAll } }:
          created.Add(node);
          break;
        case IrAwait { Operand: IrAsync { Op: IrAsyncOp.FromResult } result }:
          Link(node, result.Arguments[0]);
          break;
        case IrAwait { Operand: IrCall awaitedCall }:
          Link(node, awaitedCall);
          break;
        case IrBind bind:
          Link(bind.Local, bind.Value);
          groups.Union(node, bind.Local);
          shared.Union(node, bind.Local);
          break;
        default:
          problems.Add($"an array of the form {value.GetType().Name[2..]}");
          break;
      }
    }

    void Write(IrExpr array) {
      switch (array) {
        case IrLocalRef reference:
          written.Add(reference.Local);
          break;
        case IrFieldLoad { Target: IrLocalRef target }:
          result.WrittenFieldTargets.Add(target.Local);
          break;
        case IrCall call when facts.TryGetValue(call.Function, out var callee) && callee.Result == ArrayHolder.Borrowed:
          writtenResults.Add(call.Function);
          foreach (var index in callee.ResultParameters)
            Write(call.Arguments[index]);
          break;
        case IrConditional choice:
          Write(choice.WhenTrue);
          Write(choice.WhenFalse);
          break;
      }
    }

    foreach (var local in function.Parameters.Concat(function.Locals).Where(local => local.Type.IsArray))
      groups.Add(local);

    // A store through a pointer writes its region: the pinned array, or the region of a pointer parameter.
    var pointers = PointerRegions.Of(function);
    void WriteRegion(PointerRegion? region) {
      if (region is { Kind: RegionKind.Variable, Owner.Type.IsArray: true })
        Write(new IrLocalRef(region.Owner));
      else if (region is { Kind: RegionKind.Parameter })
        result.MutablePointers.Add(function.Parameters.ToList().IndexOf(region.Owner));
    }

    foreach (var region in pointers.Written)
      WriteRegion(region);

    foreach (var statement in Statements(function.Body)) {
      current = statement;
      switch (statement) {
        case IrDeclare or IrAssign when moves is not null && moves.Contains(statement):
          // The array changes owner: the two variables never hold it at once.
          break;
        case IrDeclare { Local.Type.IsArray: true, Initializer: { } initializer } declare:
          Link(declare.Local, initializer);
          break;
        case IrAssign { Local.Type.IsArray: true } assign:
          Link(assign.Local, assign.Value);
          break;
        case IrReturn { Value: { Type.IsArray: true } value }:
          Link(_result, value);
          break;
        case IrStore store:
          Write(store.Array);
          break;
        case IrFieldStore store when RustEmitter.ElementOf(store.Target) is { } array:
          Write(array);
          break;
      }

      foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
        switch (node) {
          case IrStep { Target: IrArrayLoad load }:
            Write(load.Array);
            break;
          case IrBind { Local.Type.IsArray: true } bind:
            current = null;
            Link(bind.Local, bind.Value);
            break;
          case IrIntrinsic { Op: IrIntrinsicOp.ArrayCopy } copy:
            Write(copy.Arguments[2]);
            break;
          case IrIntrinsic { Op: IrIntrinsicOp.ArrayFill or IrIntrinsicOp.ArrayClear or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig } fill:
            Write(fill.Arguments[0]);
            break;
          case IrCall call when facts.TryGetValue(call.Function, out var callee):
            foreach (var index in callee.MutableParameters)
              Write(call.Arguments[index]);
            foreach (var index in callee.MutablePointers)
              WriteRegion(pointers.Of(call.Arguments[index], false));
            // A writable view of an array the callee writes through writes the array.
            if (byName.TryGetValue(call.Function, out var target))
              for (var index = 0; index < call.Arguments.Count && index < target.Parameters.Count; ++index)
                if (ViewRoot(call.Arguments[index]) is { } viewed && WritesView(target, target.Parameters[index]))
                  Write(viewed);
            break;
          case IrForeignCall foreignCall when foreignByName.TryGetValue(foreignCall.Function, out var declared):
            for (var index = 0; index < foreignCall.Arguments.Count && index < declared.Parameters.Count; ++index)
              if (declared.Parameters[index].Marshal.Kind is IrMarshalKind.Array or IrMarshalKind.ByRef
                && declared.Parameters[index].Marshal.Direction != IrMarshalDirection.In)
                Write(foreignCall.Arguments[index]);
            break;
          case IrIntrinsic { Op: IrIntrinsicOp.ViewFill or IrIntrinsicOp.ViewCopy or IrIntrinsicOp.ViewTryCopy } operation
            when ViewRoot(operation.Arguments[operation.Op == IrIntrinsicOp.ViewFill ? 0 : 1]) is { } viewed:
            Write(viewed);
            break;
        }
      }

      if (statement is IrDeclare { Local.Type.IsView: true, Initializer: { } view } viewDeclare && ViewRoot(view) is { } root && WritesView(function, viewDeclare.Local))
        Write(root);
    }

    if (writtenResults.Contains(function.Name))
      written.Add(_result);

    // A group that holds an array the function made: each new array is one group of its own. An
    // array nothing writes is a value: who holds it cannot be told apart, so a group of a parameter
    // and a new array, or of two owners, copies it where it changes hands.
    var owned = created.Select(groups.Find).ToHashSet(ReferenceEqualityComparer.Instance);
    var writtenRoots = written.Select(groups.Find).ToHashSet(ReferenceEqualityComparer.Instance);
    // A copy is the field's array only where neither is written: .NET shares it.
    if (copies.FirstOrDefault(copy => writtenRoots.Contains(groups.Find(copy))) is IrLocal copied)
      problems.Add($"the variable '{copied.Name}' given a field's array or another one, and written");
    for (var index = 0; index < function.Parameters.Count; ++index) {
      var parameter = function.Parameters[index];
      if (!parameter.Type.IsArray)
        continue;

      var root = groups.Find(parameter);
      if (owned.Contains(root) && writtenRoots.Contains(root))
        problems.Add($"the parameter '{parameter.Name}' and a new array in one variable");
      else if (owned.Contains(root))
        result.CopiedParameters.Add(index);
      if (writtenRoots.Contains(root))
        result.MutableParameters.Add(index);
    }

    foreach (var local in function.Locals.Where(local => local.Type.IsArray)) {
      var root = groups.Find(local);
      var holder = !owned.Contains(root) && function.Parameters.Any(parameter => groups.Find(parameter) == root) ? ArrayHolder.Borrowed : ArrayHolder.Owned;
      result.Locals[local] = result.FieldAliases.ContainsKey(local) || holder == ArrayHolder.Owned && writtenRoots.Contains(root) && BorrowsLocal(function, local) ? ArrayHolder.Borrowed : holder;
      if (writtenRoots.Contains(root))
        result.MutableLocals.Add(local);
    }

    if (moves is null)
      FindMoves(function, result, groups.Find, owned);
    else
      result.Moves.UnionWith(moves);

    // Two owners of one array at once: Rust moves a vector, where the IR shares it. A move hands it from one to the next.
    var owners = function.Locals.Where(local => result.Locals.GetValueOrDefault(local) == ArrayHolder.Owned).GroupBy(groups.Find)
      .Where(group => writtenRoots.Contains(group.Key)).SelectMany(group => group.GroupBy(shared.Find))
      .FirstOrDefault(group => group.Count() > 1);
    if (owners is not null)
      problems.Add($"one array shared by the variables {string.Join(" and ", owners.Select(local => $"'{local.Name}'"))}");

    var returnedType = function.ReturnType.IsAsync ? function.ReturnType.Element! : function.ReturnType;
    if (returnedType.IsArray) {
      if (function.IsAsync) {
        result.Result = ArrayHolder.Owned;
      } else {
        var root = groups.Find(_result);
        var borrowed = function.Parameters.Select((parameter, index) => (parameter, index)).Where(pair => pair.parameter.Type.IsArray && groups.Find(pair.parameter) == root).ToList();
        result.Result = !owned.Contains(root) && borrowed.Count > 0 ? ArrayHolder.Borrowed : ArrayHolder.Owned;
        if (result.Result == ArrayHolder.Borrowed)
          foreach (var (_, index) in borrowed)
            result.ResultParameters.Add(index);
      }
    }

    result.Problem = problems.FirstOrDefault();
    return result;
  }

  /// <summary>
  /// Whether a variable can borrow the array or collection of the variable it is declared with, <c>let r = &amp;mut a;</c>
  /// (#1393): it is declared with another local that is read again later and never given another value, and from its declaration to its
  /// last read in the block that declares it nothing names the other variable, returns it or captures it in a
  /// lambda - the borrow ends before the owner is used again.
  /// </summary>
  internal static bool BorrowsLocal(IrFunction function, IrLocal local) {
    if (Statements(function.Body).Any(statement => statement is IrAssign assign && assign.Local == local))
      return false;

    foreach (var block in Blocks(function.Body)) {
      var at = block.Statements.ToList().FindIndex(statement => statement is IrDeclare { Initializer: IrLocalRef } declare && declare.Local == local);
      if (at < 0)
        continue;

      var owner = ((IrLocalRef)((IrDeclare)block.Statements[at]).Initializer!).Local;
      // Where the declaration reads the other variable for the last time, the value moves instead.
      if (function.Parameters.Contains(owner) || Moves.IsLast(function.Body, block.Statements[at], owner))
        return false;

      bool Refers(IrStmt statement, IrLocal variable) => IrTree.Nodes(statement).Any(node => node is IrLocalRef reference && reference.Local == variable);
      var following = block.Statements.Skip(at + 1).ToList();
      var last = following.FindLastIndex(statement => Refers(statement, local));
      var living = following.Take(last + 1).ToList();
      return !living.Any(statement => Refers(statement, owner))
        && !living.SelectMany(IrTree.Nodes).OfType<IrLambda>().Any(lambda => IrTree.Nodes(lambda.Body).Any(node => node is IrLocalRef reference && reference.Local == local))
        && !Statements(new IrBlock(living)).Any(statement => statement is IrReturn { Value: IrLocalRef reference } && reference.Local == local);
    }

    return false;
  }

  /// <summary>Every block of a body, the body first.</summary>
  private static IEnumerable<IrBlock> Blocks(IrBlock block) {
    yield return block;
    foreach (var statement in block.Statements)
      foreach (var inner in statement is IrBlock nested ? [nested] : IrTree.Blocks(statement))
        foreach (var deeper in Blocks(inner))
          yield return deeper;
  }

  /// <summary>Unions of the variables of a function and of the sentinel of its result.</summary>
  private sealed class Groups {

    private readonly Dictionary<object, object> _parents = new(ReferenceEqualityComparer.Instance);

    public void Add(object node) => this._parents.TryAdd(node, node);

    public bool Contains(object node) => this._parents.ContainsKey(node);

    public object Find(object node) {
      this.Add(node);
      var parent = this._parents[node];
      if (ReferenceEquals(parent, node))
        return node;

      var root = this.Find(parent);
      this._parents[node] = root;
      return root;
    }

    public void Union(object first, object second) {
      var a = this.Find(first);
      var b = this.Find(second);
      if (!ReferenceEquals(a, b))
        this._parents[b] = a;
    }

  }

}
