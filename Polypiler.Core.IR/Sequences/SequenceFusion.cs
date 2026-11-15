using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// Lowers every operation on lazy sequences (<see cref="IrSequence"/>) to loops, before a backend
/// writes the module (#1329). A chain of operations - a source, the lazy operations on it, and a
/// terminal or nothing - becomes the call of one function the pass adds to the module, whose
/// arguments are the values the chain was given, in the order the chain evaluates them, and whose body
/// is <b>one loop</b>: it pulls an element from the source and takes it through every operation in
/// turn, calling the functions of the operations as the lazy enumeration calls them - element by
/// element, a filter before the projection after it, an inner sequence completely before the next
/// outer element, nothing after a <c>Take</c> is satisfied. A chain that ends in a terminal is a
/// function that returns its value; one that ends in a sequence - one that escapes: stored, returned,
/// passed - is an iterator function, which runs the loop anew on every enumeration and yields the
/// elements, so that re-enumeration re-runs the pipeline. An ordering splits the loop: the elements
/// before it are pulled into a list, their keys computed level by level and their indices sorted
/// stably (<see cref="SequenceLibrary.StableSort"/>); the operations after it loop over the sorted list.
/// </summary>
public static class SequenceFusion {

  /// <summary>Lowers the sequence operations of a module; a module without any is returned as it is.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The module without <see cref="IrSequence"/> nodes, with the functions that replace them.</returns>
  public static IrModule Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    if (!module.Functions.Any(function => IrTree.Nodes(function.Body).Any(node => node is IrSequence)))
      return module;

    var fusion = new Fusion(module);
    var functions = module.Functions.Select(fusion.Function).ToList();
    functions.AddRange(fusion.Added);
    SequenceLibrary.Require(functions, fusion.Library);
    return module with { Functions = functions };
  }

  /// <summary>The rewrite of one module: the functions it adds and the names it has taken.</summary>
  private sealed class Fusion(IrModule module) {

    private readonly HashSet<string> _names = [.. module.Functions.Select(function => function.Name)];

    /// <summary>The functions that replace the chains, in the order they were made.</summary>
    public List<IrFunction> Added { get; } = [];

    /// <summary>The functions of <see cref="SequenceLibrary"/> the added ones call.</summary>
    public HashSet<string> Library { get; } = [];

    /// <summary>A function with every chain of its body, and of the lambdas in it, replaced.</summary>
    public IrFunction Function(IrFunction function) {
      var owner = new Owner(function.Name);
      var body = this.Block(function.Body, owner);
      return owner.Temporaries.Count == 0 ? function with { Body = body }
        : function with { Locals = [.. function.Locals, .. owner.Temporaries], Body = owner.Declared(body) };
    }

    private IrBlock Block(IrBlock block, Owner owner) => new([.. block.Statements.Select(statement => this.Statement(statement, owner))]);

    private IrStmt Statement(IrStmt statement, Owner owner) {
      if (statement is IrBlock block)
        return this.Block(block, owner);

      var expressions = IrTree.Expressions(statement).Select(expression => this.Expression(expression, owner)).ToList();
      var blocks = IrTree.Blocks(statement).Select(nested => this.Block(nested, owner)).ToList();
      return IrTree.With(statement, expressions, blocks);
    }

    /// <summary>An expression with every chain in it replaced, those of the lambdas in it as functions of their own.</summary>
    private IrExpr Expression(IrExpr expression, Owner owner) {
      switch (expression) {
        case IrSequence chain:
          return this.Chain(chain, owner);
        case IrLambda lambda:
          var inner = new Owner(owner.Name);
          var body = this.Block(lambda.Body, inner);
          return inner.Temporaries.Count == 0 ? lambda with { Body = body }
            : lambda with { Locals = [.. lambda.Locals, .. inner.Temporaries], Body = inner.Declared(body) };
        default:
          var operands = IrTree.Operands(expression);
          return operands.Count == 0 ? expression : IrTree.With(expression, [.. operands.Select(operand => this.Expression(operand, owner))]);
      }
    }

    /// <summary>
    /// A chain as the call of the function that runs it: the source, the lazy operations from the
    /// innermost out, and the root - a terminal or the last lazy operation.
    /// </summary>
    private IrExpr Chain(IrSequence root, Owner owner) {
      var stages = new List<IrSequence>();
      IrExpr current = root;
      while (current is IrSequence { IsSource: false } stage) {
        stages.Insert(0, stage);
        current = stage.Arguments[0];
      }

      // An array that only escapes as a sequence is the sequence of its elements, which the backends write.
      if (current is IrSequence { Op: IrSequenceOp.FromArray } array && stages.Count == 0)
        return new IrArraySequence(this.Expression(array.Arguments[0], owner));

      var terminal = root.IsTerminal ? root : null;
      if (terminal is not null)
        stages.RemoveAt(stages.Count - 1);

      var builder = new Builder(this.Library);
      var arguments = new List<IrExpr>();
      IrExpr Parameter(IrExpr value, string name) {
        arguments.Add(this.Expression(value, owner));
        return builder.Parameter(name, value.Type);
      }

      // The source.
      Source source;
      switch (current) {
        case IrSequence { Op: IrSequenceOp.FromArray } from:
          source = new Source(SourceKind.Array, [Parameter(from.Arguments[0], "source")]);
          break;
        case IrSequence { Op: IrSequenceOp.FromList } from:
          source = new Source(SourceKind.List, [Parameter(from.Arguments[0], "source")]);
          break;
        case IrSequence range:
          // The range is checked where it is evaluated: its start is kept in a temporary that the check reads.
          var start = this.Expression(range.Arguments[0], owner);
          var count = this.Expression(range.Arguments[1], owner);
          IrExpr first = start, checkedStart = start;
          if (start is not IrConst) {
            var temporary = owner.Temporary(IrType.I32);
            (first, checkedStart) = (new IrBind(temporary, start), new IrLocalRef(temporary));
          }

          this.Library.Add(SequenceLibrary.RangeCountName);
          arguments.Add(first);
          arguments.Add(new IrCall(SequenceLibrary.RangeCountName, IrType.I32, [checkedStart, count]));
          source = new Source(SourceKind.Range, [builder.Parameter("start", IrType.I32), builder.Parameter("count", IrType.I32)]);
          break;
        default:
          source = new Source(SourceKind.Sequence, [Parameter(current, "source")]);
          break;
      }

      // The operands of every operation, in the order the chain evaluates them.
      var operands = new List<IReadOnlyList<IrExpr>>();
      foreach (var stage in stages.Append(terminal).OfType<IrSequence>())
        operands.Add([.. stage.Arguments.Skip(1).Select((argument, index) => argument is IrSequence { Op: IrSequenceOp.FromArray or IrSequenceOp.FromList } second && stage.Op == IrSequenceOp.Zip
          ? Parameter(second.Arguments[0], "second")
          : Parameter(argument, index == 0 ? stage.Op.ToString().ToLowerInvariant() : "operand"))]);

      var body = Reachable(builder.Body(source, stages, terminal, operands, root.Type));
      var name = this.Name(owner.Name, terminal?.Op ?? (stages.Count > 0 ? stages[^1].Op : ((IrSequence)current).Op));
      this.Added.Add(new IrFunction(name, false, root.Type, builder.Parameters, builder.Locals, body));
      return new IrCall(name, root.Type, arguments);
    }

    /// <summary>A block without the statements after one that never completes - a return, a throw, a break - in it or in any block it holds.</summary>
    private static IrBlock Reachable(IrBlock block) {
      var kept = new List<IrStmt>();
      foreach (var statement in block.Statements) {
        kept.Add(statement is IrBlock nested ? Reachable(nested)
          : IrTree.With(statement, IrTree.Expressions(statement), [.. IrTree.Blocks(statement).Select(Reachable)]));
        if (statement is IrReturn or IrThrow or IrBreak or IrContinue)
          break;
      }

      return new IrBlock(kept);
    }

    /// <summary>A name no function of the module has: the function the chain stands in, a <c>$</c> and the operation that ends it.</summary>
    private string Name(string owner, IrSequenceOp op) {
      var name = $"{owner}${op}";
      for (var index = 2; !this._names.Add(name); ++index)
        name = $"{owner}${op}{index}";

      return name;
    }

  }

  /// <summary>A function or a lambda being rewritten: the temporaries its chains need, declared at its start.</summary>
  private sealed class Owner(string name) {

    public string Name { get; } = name;

    public List<IrLocal> Temporaries { get; } = [];

    public IrLocal Temporary(IrType type) {
      var local = new IrLocal($"$range{this.Temporaries.Count}", type);
      this.Temporaries.Add(local);
      return local;
    }

    public IrBlock Declared(IrBlock body) => new([.. this.Temporaries.Select(local => (IrStmt)new IrDeclare(local, null)), .. body.Statements]);

  }

  private enum SourceKind { Array, List, Range, Sequence, Sorted }

  /// <summary>Where the loop of a segment takes its elements from, with the values that name it.</summary>
  private sealed record Source(SourceKind Kind, IReadOnlyList<IrExpr> Values);

  /// <summary>The parameters, locals and statements of the function of one chain.</summary>
  private sealed class Builder(HashSet<string> library) {

    public List<IrLocal> Parameters { get; } = [];

    public List<IrLocal> Locals { get; } = [];

    public IrExpr Parameter(string name, IrType type) {
      var local = new IrLocal(this.Unique(name), type);
      this.Parameters.Add(local);
      return new IrLocalRef(local);
    }

    public IrLocal Local(string name, IrType type) {
      var local = new IrLocal(this.Unique(name), type);
      this.Locals.Add(local);
      return local;
    }

    private string Unique(string name) {
      var taken = this.Parameters.Concat(this.Locals).Select(local => local.Name).ToHashSet();
      var unique = name;
      for (var index = 2; taken.Contains(unique); ++index)
        unique = name + index;

      return unique;
    }

    private static IrExpr I32(long value) => new IrConst(IrType.I32, value);

    private static IrExpr Ref(IrLocal local) => new IrLocalRef(local);

    private static IrExpr Invoke(IrExpr function, params IrExpr[] arguments) => new IrInvoke(function.Type.Signature!.Result, function, arguments);

    private static IrStmt Increment(IrLocal counter) => new IrAssign(counter, new IrBinary(IrBinaryOp.AddChecked, Ref(counter), I32(1)));

    private static IrStmt Throw(IrClassType type, string message)
      => new IrThrow(new IrNew(IrType.Object(type), IrExceptionClass.Constructor(type, 1), [new IrStrConst(message)]));

    /// <summary>Whether two values are equal by <c>Equals</c>: strings by code unit, numbers and truth values as <see cref="IrGenericEquals"/> says.</summary>
    private static IrExpr Equal(IrExpr left, IrExpr right)
      => left.Type.IsString ? new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]) : new IrGenericEquals(left, right);

    /// <summary>The body of the function: the segments of the chain, one loop each, split at the orderings.</summary>
    public IrBlock Body(Source source, IReadOnlyList<IrSequence> stages, IrSequence? terminal, IReadOnlyList<IReadOnlyList<IrExpr>> operands, IrType result) {
      var sink = this.Terminal(terminal, terminal is null ? [] : operands[^1], result, stages.Count == 0 ? (source.Kind == SourceKind.Range ? IrType.I32 : SourceElement(source)) : Produced(stages[^1]));
      var statements = new List<IrStmt>();
      this.Loops(source, stages, operands, sink.Each, statements);
      // A Take of nothing anywhere in the chain pulls nothing: no loop starts, no function is called.
      var takes = stages.Select((stage, index) => (stage, index)).Where(pair => pair.stage.Op == IrSequenceOp.Take)
        .Select(pair => (IrExpr)new IrBinary(IrBinaryOp.LessOrEqual, operands[pair.index][0], I32(0)))
        .Aggregate((IrExpr?)null, (all, test) => all is null ? test : new IrBinary(IrBinaryOp.LogicalOr, all, test));
      IrStmt[] loops = takes is null ? [.. statements] : [new IrIf(new IrUnary(IrUnaryOp.LogicalNot, takes), new IrBlock(statements), null)];
      return new IrBlock([.. sink.Before, .. loops, .. sink.After]);
    }

    /// <summary>The loops of the chain, one per segment between the orderings, the last one to the sink.</summary>
    private void Loops(Source source, IReadOnlyList<IrSequence> stages, IReadOnlyList<IReadOnlyList<IrExpr>> operands, Action<IrExpr, List<IrStmt>> each, List<IrStmt> statements) {
      var element = source.Kind == SourceKind.Range ? IrType.I32 : SourceElement(source);
      var start = 0;
      while (true) {
        var ordering = Enumerable.Range(start, stages.Count - start).FirstOrDefault(index => IrSequenceOps.IsOrdering(stages[index].Op), -1);
        var end = ordering < 0 ? stages.Count : ordering;
        var segment = Enumerable.Range(start, end - start).Select(index => (stages[index], operands[index])).ToList();
        if (ordering < 0) {
          this.Segment(source, segment, each, statements);
          return;
        }

        var type = segment.Count == 0 ? element : Produced(segment[^1].Item1);
        var buffer = this.Local("buffer", IrType.List(type));
        statements.Add(new IrDeclare(buffer, new IrCollection(IrCollectionOp.ListNew, IrType.List(type), [])));
        this.Segment(source, segment, (value, into) => into.Add(new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [Ref(buffer), value]))), statements);

        var last = ordering + 1;
        while (last < stages.Count && stages[last].Op is IrSequenceOp.ThenBy or IrSequenceOp.ThenByDescending)
          ++last;

        var order = this.Sort(buffer, [.. Enumerable.Range(ordering, last - ordering).Select(index => (stages[index], operands[index][0]))], statements);
        source = new Source(SourceKind.Sorted, [Ref(buffer), Ref(order)]);
        element = type;
        start = last;
      }
    }

    /// <summary>
    /// The value a variable of a type starts with before the loop gives it one: zero, <c>false</c>, the
    /// empty string, the default struct, <c>null</c>.
    /// </summary>
    private static IrExpr Default(IrType type) => type switch {
      { IsFloat: true } => new IrConst(type, IrOperators.Bits(type, 0.0)),
      { IsString: true } => new IrStrConst(""),
      { IsStruct: true } => new IrStructDefault(type),
      { IsNullable: true } => new IrNull(type),
      _ => new IrConst(type, 0)
    };

    /// <summary>The element type of a source.</summary>
    private static IrType SourceElement(Source source) => source.Values[0].Type.Element!;

    /// <summary>The element type of the sequence an operation makes.</summary>
    private static IrType Produced(IrSequence stage) => stage.Type.Element!;

    /// <summary>
    /// Pulls the keys of the elements of a list level by level, then sorts their indices stably by them:
    /// the array of the indices in the order of the elements.
    /// </summary>
    private IrLocal Sort(IrLocal buffer, IReadOnlyList<(IrSequence Stage, IrExpr Key)> levels, List<IrStmt> statements) {
      var count = this.Local("n", IrType.I32);
      statements.Add(new IrDeclare(count, new IrCollection(IrCollectionOp.ListCount, IrType.I32, [Ref(buffer)])));
      var keys = new List<IrLocal>();
      foreach (var (_, key) in levels) {
        var type = key.Type.Signature!.Result;
        // The keys of a level in a list, which holds every key type - a string array has no elements to start with.
        var list = this.Local("keys", IrType.List(type));
        keys.Add(list);
        statements.Add(new IrDeclare(list, new IrCollection(IrCollectionOp.ListNew, IrType.List(type), [])));
        var index = this.Local("k", IrType.I32);
        statements.Add(new IrDeclare(index, I32(0)));
        var element = new IrCollection(IrCollectionOp.ListGet, buffer.Type.Element!, [Ref(buffer), Ref(index)]);
        statements.Add(new IrFor(new IrBinary(IrBinaryOp.Less, Ref(index), Ref(count)),
          new IrBlock([new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [Ref(list), Invoke(key, element)]))]),
          new IrBlock([new IrAssign(index, new IrBinary(IrBinaryOp.Add, Ref(index), I32(1)))])));
      }

      var order = this.Local("order", IrType.Array(IrType.I32));
      var position = this.Local("k", IrType.I32);
      statements.Add(new IrDeclare(order, new IrNewArray(IrType.Array(IrType.I32), Ref(count))));
      statements.Add(new IrDeclare(position, I32(0)));
      statements.Add(new IrFor(new IrBinary(IrBinaryOp.Less, Ref(position), Ref(count)),
        new IrBlock([new IrStore(Ref(order), Ref(position), Ref(position))]),
        new IrBlock([new IrAssign(position, new IrBinary(IrBinaryOp.Add, Ref(position), I32(1)))])));

      // The comparison of two indices: the keys level by level, a descending level the other way round.
      var a = new IrLocal("a", IrType.I32);
      var b = new IrLocal("b", IrType.I32);
      var compared = new IrLocal("c", IrType.I32);
      var comparison = new List<IrStmt> { new IrDeclare(compared, I32(0)) };
      for (var level = 0; level < levels.Count; ++level) {
        var descending = levels[level].Stage.Op is IrSequenceOp.OrderByDescending or IrSequenceOp.ThenByDescending;
        IrExpr Key(IrLocal at) => new IrCollection(IrCollectionOp.ListGet, keys[level].Type.Element!, [Ref(keys[level]), Ref(at)]);
        var (left, right) = descending ? (Key(b), Key(a)) : (Key(a), Key(b));
        IrExpr value = left.Type.IsString ? new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [left, right]) : new IrGenericCompare(left, right);
        comparison.Add(new IrAssign(compared, value));
        comparison.Add(new IrIf(new IrBinary(IrBinaryOp.NotEqual, Ref(compared), I32(0)), new IrBlock([new IrReturn(Ref(compared))]), null));
      }

      comparison.Add(new IrReturn(I32(0)));
      var lambda = IrLambda.Of([a, b], IrType.I32, [compared], new IrBlock(comparison));
      library.Add(SequenceLibrary.StableSortName);
      statements.Add(new IrExprStmt(new IrCall(SequenceLibrary.StableSortName, IrType.Void, [Ref(order), lambda])));
      return order;
    }

    /// <summary>
    /// The loop of a segment: every element of the source taken through the operations in turn, to the
    /// sink. A segment that can end early - a <c>Take</c>, a <c>Zip</c> - has a flag that ends it, which
    /// is tested at the end of every pass, before the next element is pulled.
    /// </summary>
    private void Segment(Source source, IReadOnlyList<(IrSequence Stage, IReadOnlyList<IrExpr> Operands)> stages, Action<IrExpr, List<IrStmt>> sink, List<IrStmt> statements) {
      var stops = stages.Any(stage => stage.Stage.Op is IrSequenceOp.Take or IrSequenceOp.Zip);
      var stop = stops ? this.Local("stop", IrType.Bool) : null;
      if (stop is not null)
        statements.Add(new IrDeclare(stop, new IrConst(IrType.Bool, 0)));

      var before = new List<IrStmt>();
      IrBlock Pass(IrLocal element, List<IrStmt> prefix) {
        var body = new List<IrStmt>(prefix);
        this.Stage(stages, 0, Ref(element), sink, stop, body, before);
        if (stop is not null)
          body.Add(new IrIf(Ref(stop), new IrBlock([new IrBreak()]), null));

        return new IrBlock(body);
      }

      IrStmt loop;
      var elementType = source.Kind is SourceKind.Range ? IrType.I32 : source.Kind == SourceKind.Sorted ? source.Values[0].Type.Element! : SourceElement(source);
      var item = this.Local("x", elementType);
      switch (source.Kind) {
        case SourceKind.List:
          loop = new IrForEachCollection(source.Values[0], EnumerationMode.Checked, item, null, Pass(item, []));
          break;
        case SourceKind.Sequence:
          loop = new IrForEachSequence(item, source.Values[0], Pass(item, []));
          break;
        default:
          var index = this.Local("i", IrType.I32);
          before.Add(new IrDeclare(index, I32(0)));
          var (limit, value) = source.Kind switch {
            SourceKind.Array => ((IrExpr)new IrArrayLength(source.Values[0]), (IrExpr)new IrArrayLoad(source.Values[0], Ref(index))),
            SourceKind.Range => (source.Values[1], new IrBinary(IrBinaryOp.Add, source.Values[0], Ref(index))),
            _ => (new IrCollection(IrCollectionOp.ListCount, IrType.I32, [source.Values[0]]),
              new IrCollection(IrCollectionOp.ListGet, elementType, [source.Values[0], new IrArrayLoad(source.Values[1], Ref(index))]))
          };
          // The index steps before the operations run, so that a pass that returns leaves no step behind it.
          loop = new IrWhile(new IrBinary(IrBinaryOp.Less, Ref(index), limit),
            Pass(item, [new IrDeclare(item, value), new IrAssign(index, new IrBinary(IrBinaryOp.Add, Ref(index), I32(1)))]));
          break;
      }

      statements.AddRange(before);
      statements.Add(loop);
    }

    /// <summary>Takes one element through the operation at an index and those after it, to the sink.</summary>
    private void Stage(IReadOnlyList<(IrSequence Stage, IReadOnlyList<IrExpr> Operands)> stages, int at, IrExpr element, Action<IrExpr, List<IrStmt>> sink, IrLocal? stop, List<IrStmt> into, List<IrStmt> before) {
      if (at == stages.Count) {
        sink(element, into);
        return;
      }

      var (stage, operands) = stages[at];
      void Next(IrExpr value, List<IrStmt> target) => this.Stage(stages, at + 1, value, sink, stop, target, before);
      IrLocal Value(IrExpr value, List<IrStmt> target) {
        var local = this.Local("y", value.Type);
        target.Add(new IrDeclare(local, value));
        return local;
      }

      IrLocal Counter(long first) {
        var counter = this.Local("n", IrType.I32);
        before.Add(new IrDeclare(counter, I32(first)));
        return counter;
      }

      switch (stage.Op) {
        case IrSequenceOp.Select:
          Next(Ref(Value(Invoke(operands[0], element), into)), into);
          break;
        case IrSequenceOp.SelectIndexed or IrSequenceOp.WhereIndexed:
          var position = Counter(-1);
          into.Add(Increment(position));
          var result = Value(Invoke(operands[0], element, Ref(position)), into);
          if (stage.Op == IrSequenceOp.SelectIndexed) {
            Next(Ref(result), into);
          } else {
            var kept = new List<IrStmt>();
            Next(element, kept);
            into.Add(new IrIf(Ref(result), new IrBlock(kept), null));
          }

          break;
        case IrSequenceOp.Where:
          var passed = new List<IrStmt>();
          Next(element, passed);
          into.Add(new IrIf(Invoke(operands[0], element), new IrBlock(passed), null));
          break;
        case IrSequenceOp.SelectMany or IrSequenceOp.SelectManyResult:
          var inner = this.Local("z", stage.Op == IrSequenceOp.SelectMany ? stage.Type.Element! : operands[0].Type.Signature!.Result.Element!);
          var each = new List<IrStmt>();
          Next(stage.Op == IrSequenceOp.SelectMany ? Ref(inner) : Ref(Value(Invoke(operands[1], element, Ref(inner)), each)), each);
          if (stop is not null)
            each.Add(new IrIf(Ref(stop), new IrBlock([new IrBreak()]), null));

          into.Add(new IrForEachSequence(inner, Invoke(operands[0], element), new IrBlock(each)));
          break;
        case IrSequenceOp.Take:
          var taken = Counter(0);
          into.Add(new IrAssign(taken, new IrBinary(IrBinaryOp.Add, Ref(taken), I32(1))));
          Next(element, into);
          into.Add(new IrIf(new IrBinary(IrBinaryOp.GreaterOrEqual, Ref(taken), operands[0]), new IrBlock([new IrAssign(stop!, new IrConst(IrType.Bool, 1))]), null));
          break;
        case IrSequenceOp.Skip:
          var skipped = Counter(0);
          var rest = new List<IrStmt>();
          Next(element, rest);
          into.Add(new IrIf(new IrBinary(IrBinaryOp.Less, Ref(skipped), operands[0]),
            new IrBlock([new IrAssign(skipped, new IrBinary(IrBinaryOp.Add, Ref(skipped), I32(1)))]), new IrBlock(rest)));
          break;
        default:
          // Zip: the next element of the second sequence, an array or a list read by index.
          var second = operands[0];
          var at2 = Counter(0);
          var (length, read) = second.Type.IsArray
            ? ((IrExpr)new IrArrayLength(second), (IrExpr)new IrArrayLoad(second, Ref(at2)))
            : (new IrCollection(IrCollectionOp.ListCount, IrType.I32, [second]), new IrCollection(IrCollectionOp.ListGet, second.Type.Element!, [second, Ref(at2)]));
          var paired = new List<IrStmt>();
          var other = Value(read, paired);
          paired.Add(new IrAssign(at2, new IrBinary(IrBinaryOp.Add, Ref(at2), I32(1))));
          Next(Ref(Value(Invoke(operands[1], element, Ref(other)), paired)), paired);
          into.Add(new IrIf(new IrBinary(IrBinaryOp.Less, Ref(at2), length), new IrBlock(paired), new IrBlock([new IrAssign(stop!, new IrConst(IrType.Bool, 1))])));
          break;
      }
    }

    /// <summary>What a terminal declares before the loop, does with every element and returns after it; a lazy chain yields every element.</summary>
    private (List<IrStmt> Before, Action<IrExpr, List<IrStmt>> Each, List<IrStmt> After) Terminal(IrSequence? terminal, IReadOnlyList<IrExpr> operands, IrType result, IrType element) {
      var before = new List<IrStmt>();
      var after = new List<IrStmt>();
      if (terminal is null)
        return (before, (value, into) => into.Add(new IrYield(value)), after);

      IrExpr Test(IrExpr value) => Invoke(operands[0], value);
      IrStmt Return(IrExpr value) => new IrReturn(value);
      IrBlock Block(params IrStmt[] statements) => new(statements);
      IrLocal Declared(string name, IrType type, IrExpr initial) {
        var local = this.Local(name, type);
        before.Add(new IrDeclare(local, initial));
        return local;
      }

      var zero = Default(element);
      switch (terminal.Op) {
        case IrSequenceOp.ToArray or IrSequenceOp.ToList:
          var list = Declared("list", IrType.List(element), new IrCollection(IrCollectionOp.ListNew, IrType.List(element), []));
          after.Add(Return(terminal.Op == IrSequenceOp.ToList ? Ref(list) : new IrCollection(IrCollectionOp.ListCopyToArray, result, [Ref(list)])));
          return (before, (value, into) => into.Add(new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [Ref(list), value]))), after);
        case IrSequenceOp.Count or IrSequenceOp.CountWhere:
          var count = Declared("count", IrType.I32, I32(0));
          after.Add(Return(Ref(count)));
          return (before, (value, into) => into.Add(terminal.Op == IrSequenceOp.Count ? Increment(count) : new IrIf(Test(value), Block(Increment(count)), null)), after);
        case IrSequenceOp.Any or IrSequenceOp.AnyWhere or IrSequenceOp.All or IrSequenceOp.Contains:
          var found = terminal.Op != IrSequenceOp.All;
          after.Add(Return(new IrConst(IrType.Bool, found ? 0 : 1)));
          return (before, (value, into) => {
            var answer = Return(new IrConst(IrType.Bool, found ? 1 : 0));
            into.Add(terminal.Op switch {
              IrSequenceOp.Any => answer,
              IrSequenceOp.AnyWhere => new IrIf(Test(value), Block(answer), null),
              IrSequenceOp.All => new IrIf(new IrUnary(IrUnaryOp.LogicalNot, Test(value)), Block(answer), null),
              _ => new IrIf(Equal(value, operands[0]), Block(answer), null)
            });
          }, after);
        case IrSequenceOp.First or IrSequenceOp.FirstWhere or IrSequenceOp.FirstOrDefault or IrSequenceOp.FirstOrDefaultWhere:
          var filtered = terminal.Op is IrSequenceOp.FirstWhere or IrSequenceOp.FirstOrDefaultWhere;
          after.Add(terminal.Op switch {
            IrSequenceOp.First => Throw(IrExceptionClass.InvalidOperationException, IrSequenceOps.NoElements),
            IrSequenceOp.FirstWhere => Throw(IrExceptionClass.InvalidOperationException, IrSequenceOps.NoMatch),
            _ => Return(zero)
          });
          return (before, (value, into) => into.Add(filtered ? new IrIf(Test(value), Block(Return(value)), null) : Return(value)), after);
        case IrSequenceOp.Sum:
          var wide = element == IrType.F32 ? IrType.F64 : element;
          var sum = Declared("sum", wide, wide.IsFloat ? new IrConst(wide, IrOperators.Bits(wide, 0.0)) : new IrConst(wide, 0));
          after.Add(Return(element == IrType.F32 ? new IrConvert(IrType.F32, Ref(sum)) : Ref(sum)));
          return (before, (value, into) => into.Add(new IrAssign(sum, new IrBinary(wide.IsFloat ? IrBinaryOp.Add : IrBinaryOp.AddChecked, Ref(sum), element == IrType.F32 ? new IrConvert(IrType.F64, value) : value))), after);
        case IrSequenceOp.Min or IrSequenceOp.Max:
          return this.Extreme(terminal.Op == IrSequenceOp.Max, element, zero, before, after);
        case IrSequenceOp.Aggregate:
          var has = Declared("has", IrType.Bool, new IrConst(IrType.Bool, 0));
          var folded = Declared("result", element, zero);
          after.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, Ref(has)), Block(Throw(IrExceptionClass.InvalidOperationException, IrSequenceOps.NoElements)), null));
          after.Add(Return(Ref(folded)));
          return (before, (value, into) => into.Add(new IrIf(Ref(has), Block(new IrAssign(folded, Invoke(operands[0], Ref(folded), value))),
            Block(new IrAssign(has, new IrConst(IrType.Bool, 1)), new IrAssign(folded, value)))), after);
        case IrSequenceOp.AggregateSeed:
          var accumulated = Declared("result", result, operands[0]);
          after.Add(Return(Ref(accumulated)));
          return (before, (value, into) => into.Add(new IrAssign(accumulated, Invoke(operands[1], Ref(accumulated), value))), after);
        default:
          // IndexOf and FindIndex: the index of the first element that matches.
          var index = Declared("index", IrType.I32, I32(0));
          after.Add(Return(I32(-1)));
          return (before, (value, into) => {
            into.Add(new IrIf(terminal.Op == IrSequenceOp.IndexOf ? Equal(value, operands[0]) : Test(value), Block(Return(Ref(index))), null));
            into.Add(new IrAssign(index, new IrBinary(IrBinaryOp.Add, Ref(index), I32(1))));
          }, after);
      }
    }

    /// <summary>
    /// <c>Min</c> and <c>Max</c> as .NET computes them: of floating-point numbers a NaN is the smallest -
    /// <c>Min</c> returns the first NaN at once, <c>Max</c> takes any element over a NaN it holds - and
    /// of equal elements the first stays.
    /// </summary>
    private (List<IrStmt>, Action<IrExpr, List<IrStmt>>, List<IrStmt>) Extreme(bool largest, IrType element, IrExpr zero, List<IrStmt> before, List<IrStmt> after) {
      var has = this.Local("has", IrType.Bool);
      var best = this.Local("best", element);
      before.Add(new IrDeclare(has, new IrConst(IrType.Bool, 0)));
      before.Add(new IrDeclare(best, zero));
      after.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, Ref(has)), new IrBlock([Throw(IrExceptionClass.InvalidOperationException, IrSequenceOps.NoElements)]), null));
      after.Add(new IrReturn(Ref(best)));
      IrExpr NaN(IrExpr value) => new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [value]);
      return (before, (value, into) => {
        var x = this.Local("v", element);
        into.Add(new IrDeclare(x, value));
        var first = new List<IrStmt> { new IrAssign(has, new IrConst(IrType.Bool, 1)), new IrAssign(best, Ref(x)) };
        IrStmt later;
        if (largest) {
          IrExpr better = new IrBinary(IrBinaryOp.Greater, Ref(x), Ref(best));
          if (element.IsFloat)
            better = new IrBinary(IrBinaryOp.LogicalOr, better, NaN(Ref(best)));

          later = new IrIf(better, new IrBlock([new IrAssign(best, Ref(x))]), null);
        } else {
          var smaller = new IrIf(new IrBinary(IrBinaryOp.Less, Ref(x), Ref(best)), new IrBlock([new IrAssign(best, Ref(x))]),
            element.IsFloat ? new IrBlock([new IrIf(NaN(Ref(x)), new IrBlock([new IrReturn(Ref(x))]), null)]) : null);
          if (element.IsFloat)
            first.Add(new IrIf(NaN(Ref(x)), new IrBlock([new IrReturn(Ref(x))]), null));

          later = smaller;
        }

        into.Add(new IrIf(Ref(has), new IrBlock([later]), new IrBlock(first)));
      }, after);
    }

  }

}
