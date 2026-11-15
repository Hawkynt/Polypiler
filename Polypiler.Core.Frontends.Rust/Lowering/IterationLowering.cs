using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// Iterations over slices, arrays, vectors and ranges with their adapters, as loops over positions
/// rather than iterator objects, and the closures they call, inlined where they are called.
/// <para>
/// The adapters that work position by position - <c>rev</c>, <c>step_by</c>, <c>zip</c>,
/// <c>enumerate</c>, <c>copied</c> - turn the length of the sequence and the element at a position
/// into the length and the element of the adapted sequence, so the loop runs over the positions of
/// the result: <c>zip</c> stops at the shorter side, <c>chunks_exact</c> never visits its remainder,
/// <c>step_by(0)</c> and <c>chunks_exact(0)</c> panic before the first element. The adapters after
/// them - <c>map</c>, <c>filter</c>, and an <c>enumerate</c> after a <c>filter</c> - run element by
/// element in the loop body, in source order, so <c>map(f).filter(p)</c> calls <c>f</c> and <c>p</c>
/// alternately as Rust's lazy iterators do, and a consuming method stops where Rust's does.
/// </para>
/// <para>
/// A closure that does not escape is its body, inlined at every call with its parameters bound to
/// the arguments: a variable it captures is the enclosing function's own, so a mutation through a
/// mutable capture is seen afterwards, as the exclusive borrow makes it in Rust; a <c>move</c> closure
/// reads the copies it took when it was made. A <c>return</c> in a closure leaves the closure only.
/// </para>
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary>The frames of the closures being inlined, innermost last, which a <c>return</c> in them leaves.</summary>
  private readonly List<Frame> _closureFrames = [];

  /// <summary>The result types of the closures being inlined, innermost last.</summary>
  private readonly List<RType> _closureResults = [];

  /// <summary>The copies a <c>move</c> closure took of the variables it captures, made where the closure is.</summary>
  private readonly Dictionary<ClosureInfo, Dictionary<Binding, IrExpr>> _snapshots = new(ReferenceEqualityComparer.Instance);

  /// <summary>The adapted sequence at a point of the positional adapters: its length and its item at a position.</summary>
  private sealed record Stage(IrExpr Count, Func<IrExpr, Item> ItemAt);

  /// <summary>The loop of an iteration: its test, its step, and the item of a pass, made by statements of the pass.</summary>
  private sealed record LoopShape(IrExpr Condition, IrBlock Step, Func<Item> Current, int Streaming);

  // ---------------------------------------------------------------- the loop

  /// <summary>Emits what an iteration needs before its loop - the source, the bounds, the checks - and returns the shape of the loop.</summary>
  private LoopShape? Shape(Iteration iteration) {
    if (iteration.Kind == SourceKind.Range) {
      var (counter, condition, advance) = this.RangeCounter(iteration.Range!);
      var first = iteration.Adapters.ToList().FindIndex(adapter => adapter.Name is not ("rev" or "step_by"));
      return new LoopShape(condition, new IrBlock(advance), () => new ValueItem(new IrLocalRef(counter)), first < 0 ? iteration.Adapters.Count : first);
    }

    var source = iteration.Source!;
    var container = iteration.Kind == SourceKind.Text ? this.Stable(this.TextSource(source), "items") : this.IterationContainer(source);
    if (this._diverged)
      return null;

    Stage stage;
    if (iteration.Kind == SourceKind.Chunks) {
      var size = this.Stable(this.Value(source.ChunkSize!), "chunk");
      this.Emit(new IrIf(Equal(size, new IrConst(IrType.U64, 0)), new IrBlock([RustTraps.PanicStatement("chunk size must be non-zero")]), null));
      var view = this.Stable(this.View(container, source.Access == ElementAccess.Exclusive), "chunks");
      var chunks = this.Stable(Cast(Binary(IrBinaryOp.Divide, Length(view), size), IrType.I32), "count");
      var width = Cast(size, IrType.I32);
      stage = new Stage(chunks, position => new ValueItem(new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, Binary(IrBinaryOp.Multiply, position, width), width])));
    } else {
      var count = this.Stable(Count(container), "count");
      stage = new Stage(count, position => this.Element(container, position, source.Access));
    }

    var streaming = iteration.Adapters.Count;
    for (var index = 0; index < iteration.Adapters.Count; ++index) {
      var adapter = iteration.Adapters[index];
      if (adapter.Name is "map" or "filter") {
        streaming = index;
        break;
      }

      stage = this.Adapted(stage, adapter);
      if (this._diverged)
        return null;
    }

    var position = this.Counter("position");
    var final = stage;
    return new LoopShape(Binary(IrBinaryOp.Less, new IrLocalRef(position), final.Count), new IrBlock([Next(position)]), () => final.ItemAt(new IrLocalRef(position)), streaming);
  }

  /// <summary>The container of a source of elements, read once; an array iterated by value is a copy of it, which the body cannot change.</summary>
  private IrExpr IterationContainer(ElementSource source) {
    var container = this.Container(source.Container);
    if (source.Access == ElementAccess.Value && container.Type.IsArray && this.IsPlaceExpression(source.Container))
      return this.Stable(this.Duplicate(container, this._typed.TypeOf(source.Container).Dereferenced), "copy");

    return container;
  }

  /// <summary>The item at a position of a container: its value, or for an exclusive reference its place.</summary>
  private Item Element(IrExpr container, IrExpr position, ElementAccess access) {
    if (access != ElementAccess.Exclusive)
      return new ValueItem(Load(container, position));

    var index = this.Stable(position, "at");
    return new PlaceItem(container.Type.IsList ? new ListElementPlace(container, index) : new ElementPlace(container, index));
  }

  /// <summary>A positional adapter applied to a stage.</summary>
  private Stage Adapted(Stage stage, Adapter adapter) {
    var count = stage.Count;
    switch (adapter.Name) {
      case "rev":
        return new Stage(count, position => stage.ItemAt(Binary(IrBinaryOp.Subtract, Binary(IrBinaryOp.Subtract, count, new IrConst(IrType.I32, 1)), position)));
      case "step_by": {
        var step = this.Stable(this.Value(adapter.Call.Arguments[0]), "step");
        this.Emit(new IrIf(Equal(step, new IrConst(IrType.U64, 0)), new IrBlock([RustTraps.PanicStatement("assertion failed: step != 0")]), null));
        var stepped = this.Stable(new IrConditional(Equal(count, new IrConst(IrType.I32, 0)), new IrConst(IrType.I32, 0),
          Cast(Binary(IrBinaryOp.Add, Binary(IrBinaryOp.Divide, Binary(IrBinaryOp.Subtract, Cast(count, IrType.U64), new IrConst(IrType.U64, 1)), step), new IrConst(IrType.U64, 1)), IrType.I32)), "count");
        return new Stage(stepped, position => stage.ItemAt(Cast(Binary(IrBinaryOp.Multiply, Cast(position, IrType.U64), step), IrType.I32)));
      }
      case "zip": {
        var other = adapter.Zipped!;
        var container = this.IterationContainer(other);
        var shorter = this.Stable(new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [count, Count(container)]), "count");
        return new Stage(shorter, position => new TupleItem([stage.ItemAt(position), this.Element(container, position, other.Access)]));
      }
      case "enumerate":
        return new Stage(count, position => new TupleItem([new ValueItem(Cast(position, IrType.U64)), stage.ItemAt(position)]));
      default:
        // copied, cloned.
        return new Stage(count, position => this.Dereferenced(stage.ItemAt(position)));
    }
  }

  /// <summary>The value a reference item points to.</summary>
  private Item Dereferenced(Item item) => item is PlaceItem place ? new ValueItem(this.Read(place.Place)) : item;

  /// <summary>
  /// The item of a pass after the adapters that run element by element; <see langword="null"/> when
  /// a <c>filter</c> dropped it, which has then emitted the <c>continue</c> that skips the pass.
  /// </summary>
  private Item Streamed(Iteration iteration, LoopShape shape, List<IrLocal> counters) {
    var item = shape.Current();
    for (var index = shape.Streaming; index < iteration.Adapters.Count; ++index) {
      var adapter = iteration.Adapters[index];
      switch (adapter.Name) {
        case "map": {
          var value = this.ApplyFunction(adapter.Function!, [item]);
          item = new ValueItem(value is null ? new IrConst(IrType.I32, 0) : this.Stable(value, "mapped"));
          break;
        }
        case "filter": {
          var keep = this.ApplyFunction(adapter.Function!, [item]);
          if (keep is not null)
            this.Emit(new IrIf(Not(keep), new IrBlock([new IrContinue()]), null));

          break;
        }
        case "enumerate": {
          var counter = counters[index];
          var current = this.Temporary(new IrLocalRef(counter), "count");
          this.Emit(new IrAssign(counter, Binary(IrBinaryOp.Add, new IrLocalRef(counter), new IrConst(IrType.U64, 1))));
          item = new TupleItem([new ValueItem(current), item]);
          break;
        }
        default:
          item = this.Dereferenced(item);
          break;
      }
    }

    return item;
  }

  /// <summary>The counters of the <c>enumerate</c>s that run element by element, declared before the loop.</summary>
  private List<IrLocal> StreamCounters(Iteration iteration, LoopShape shape) {
    var counters = new List<IrLocal>();
    for (var index = 0; index < iteration.Adapters.Count; ++index) {
      if (index >= shape.Streaming && iteration.Adapters[index].Name == "enumerate") {
        var counter = this.NewLocal("counted", IrType.U64);
        this.Emit(new IrDeclare(counter, new IrConst(IrType.U64, 0)));
        counters.Add(counter);
      } else {
        counters.Add(null!);
      }
    }

    return counters;
  }

  // ---------------------------------------------------------------- for and the consuming methods

  private IrExpr? IterationFor(ForExpr loop, Iteration iteration) {
    var shape = this.Shape(iteration);
    if (shape is null)
      return null;

    var counters = this.StreamCounters(iteration, shape);
    return this.Breakable(loop, _ => {
      var item = this.Streamed(iteration, shape, counters);
      this.Bind(loop.Pattern, item, iteration.Item);
      this.Block(loop.Body);
    }, statements => new IrFor(shape.Condition, new IrBlock(statements), shape.Step));
  }

  /// <summary>A consuming method: its accumulator, the loop that feeds it, and its value.</summary>
  private IrExpr? Terminal(MethodCallExpr call, Iteration iteration) {
    var name = iteration.Terminal!;
    var result = this._typed.TypeOf(call);
    // fold's initial value is evaluated before the iterator runs, after its source.
    IrLocal? accumulator = null;
    var shape = this.Shape(iteration);
    if (shape is null)
      return null;

    switch (name) {
      case "sum" or "product": {
        var type = result.Ir;
        var start = name == "sum" ? 0 : 1;
        accumulator = this.NewLocal(name, type);
        this.Emit(new IrDeclare(accumulator, type.IsFloat ? FloatConstant(type, start == 0 ? -0.0 : 1.0) : new IrConst(type, start)));
        break;
      }
      case "count":
        accumulator = this.NewLocal("count", IrType.U64);
        this.Emit(new IrDeclare(accumulator, new IrConst(IrType.U64, 0)));
        break;
      case "fold":
        accumulator = this.NewLocal("folded", this.Ir(result));
        this.Emit(new IrDeclare(accumulator, this.Value(call.Arguments[0])));
        break;
      case "any" or "all":
        accumulator = this.NewLocal(name, IrType.Bool);
        this.Emit(new IrDeclare(accumulator, Truth(name == "all")));
        break;
    }

    var counters = this.StreamCounters(iteration, shape);
    var body = this.Capture(() => {
      var item = this.Streamed(iteration, shape, counters);
      switch (name) {
        case "sum" or "product": {
          var value = this.ValueOf(this.Dereferenced(item), iteration.Item.Dereferenced);
          var op = name == "sum" ? IrBinaryOp.Add : IrBinaryOp.Multiply;
          var type = accumulator!.Type;
          this.Emit(new IrAssign(accumulator, type.IsFloat ? Binary(op, new IrLocalRef(accumulator), value) : this.Arithmetic(op, new IrLocalRef(accumulator), value, type, this.Checked)));
          break;
        }
        case "count":
          this.Emit(new IrAssign(accumulator!, Binary(IrBinaryOp.Add, new IrLocalRef(accumulator!), new IrConst(IrType.U64, 1))));
          break;
        case "fold": {
          var value = this.ApplyFunction(iteration.TerminalFunction!, [new ValueItem(new IrLocalRef(accumulator!)), item]);
          if (value is not null && !this._diverged)
            this.Emit(new IrAssign(accumulator!, value));

          break;
        }
        case "any" or "all": {
          var test = this.ApplyFunction(iteration.TerminalFunction!, [item]);
          if (test is not null && !this._diverged)
            this.Emit(new IrIf(name == "any" ? test : Not(test), new IrBlock([new IrAssign(accumulator!, Truth(name == "any")), new IrBreak()]), null));

          break;
        }
        default:
          // for_each: the function returns nothing.
          this.ApplyFunction(iteration.TerminalFunction!, [item]);
          break;
      }

      return null;
    });
    this.Emit(new IrFor(shape.Condition, new IrBlock(body.Statements), shape.Step));
    return accumulator is null ? null : new IrLocalRef(accumulator);
  }

  // ---------------------------------------------------------------- closures

  /// <summary>A closure where it is made: a <c>move</c> closure takes its copies now; the closure itself is inlined where it is called.</summary>
  private IrExpr? ClosureValue(ClosureExpr closure) {
    var info = this._typed.Closures[closure];
    if (closure.Move) {
      var copies = new Dictionary<Binding, IrExpr>(ReferenceEqualityComparer.Instance);
      foreach (var captured in info.Captures)
        copies[captured] = this.Temporary(this.CapturedValue(captured), captured.Name);

      this._snapshots[info] = copies;
    }

    return null;
  }

  private IrExpr CapturedValue(Binding binding) => this._values[binding];

  /// <summary>Calls what an iterator method was given: a closure, a closure variable, a function or a function pointer.</summary>
  private IrExpr? ApplyFunction(Expr function, IReadOnlyList<Item> arguments) {
    if (function is ClosureExpr closure) {
      this.ClosureValue(closure);
      return this.Apply(this._typed.Closures[closure], arguments);
    }

    var path = (PathExpr)function;
    if (this._typed.Bindings.TryGetValue(path, out var binding)) {
      if (binding.Type.Resolved.Declaration is ClosureInfo info)
        return this.Apply(info, arguments);

      var signature = binding.Type.Resolved;
      var values = arguments.Select((argument, index) => this.ValueOf(argument, signature.Elements[index])).ToList();
      var resultType = signature.Element!.Resolved.Kind is RKind.Unit ? IrType.Void : this.Ir(signature.Element);
      return this.Called(new IrInvoke(resultType, this._values[binding], values));
    }

    var callee = this._typed.Callees[path];
    var parameters = callee.Receiver is null ? callee.Parameters : [callee.Receiver, .. callee.Parameters];
    var arguments2 = arguments.Select((argument, index) => this.ValueOf(argument, parameters[index])).ToList();
    var result = callee.Result.Resolved.Kind is RKind.Unit or RKind.Never ? IrType.Void : this.Ir(callee.Result);
    if (Outputs(callee).Count > 0) {
      this.Refuse(RustFrontend.Unsupported, $"function '{callee.CratePath}' with &mut parameters passed to an iterator method", path.At);
      return null;
    }

    return this.Called(new IrCall(callee.IrName, result, arguments2));
  }

  /// <summary>A call as a value, or as a statement when it has none.</summary>
  private IrExpr? Called(IrExpr call) {
    if (call.Type != IrType.Void)
      return call;

    this.Emit(new IrExprStmt(call));
    return null;
  }

  /// <summary>Inlines a closure: its parameters bound to the arguments, its body lowered where it is called.</summary>
  private IrExpr? Apply(ClosureInfo info, IReadOnlyList<Item> arguments) {
    var closure = info.Expression;
    // A move closure captures Copy values only, which are values, never places.
    var restore = new List<(Binding Binding, IrExpr Value)>();
    if (this._snapshots.TryGetValue(info, out var copies)) {
      foreach (var (binding, copy) in copies) {
        restore.Add((binding, this._values[binding]));
        this._values[binding] = copy;
      }
    }

    try {
      for (var index = 0; index < arguments.Count; ++index)
        this.Bind(closure.Parameters[index].Pattern, arguments[index], info.Parameters[index]);

      if (!Returns(closure.Body))
        return this.Lower(closure.Body);

      var resultType = info.Result.Resolved.Kind is RKind.Unit or RKind.Never ? null : this.Ir(info.Result);
      this._closureResults.Add(info.Result);
      try {
        return this.Breakable(closure, frame => {
          this._closureFrames.Add(frame);
          try {
            var value = this.Lower(closure.Body);
            if (value is not null && frame.Value is not null)
              this.Emit(new IrAssign(frame.Value, value));
          } finally {
            this._closureFrames.RemoveAt(this._closureFrames.Count - 1);
          }
        }, statements => new IrDoWhile(new IrBlock(statements), Truth(false)), resultType);
      } finally {
        this._closureResults.RemoveAt(this._closureResults.Count - 1);
      }
    } finally {
      foreach (var (binding, value) in restore)
        this._values[binding] = value;
    }
  }

  /// <summary>Whether a closure body leaves by <c>return</c> or <c>?</c>, outside closures nested in it.</summary>
  private static bool Returns(Expr body) {
    var found = false;
    void Visit(object? node) {
      switch (node) {
        case null or ClosureExpr:
          return;
        case ReturnExpr or TryExpr:
          found = true;
          return;
      }

      foreach (var child in Children(node))
        Visit(child);
    }

    Visit(body);
    return found;
  }

  /// <summary>The expressions, statements and blocks directly inside a node.</summary>
  private static IEnumerable<object?> Children(object node) => node switch {
    Block block => [.. block.Statements, block.Tail],
    LetStatement let => [let.Initializer, let.Else],
    ExpressionStatement statement => [statement.Expression],
    UnaryExpr unary => [unary.Operand],
    ReferenceExpr reference => [reference.Operand],
    DerefExpr deref => [deref.Operand],
    BinaryExpr binary => [binary.Left, binary.Right],
    AssignExpr assign => [assign.Target, assign.Value],
    CastExpr cast => [cast.Operand],
    MethodCallExpr call => [call.Receiver, .. call.Arguments],
    CallExpr call => [call.Callee, .. call.Arguments],
    FieldExpr field => [field.Target],
    IndexExpr index => [index.Target, index.Index],
    TupleExpr tuple => tuple.Elements,
    ArrayExpr array => array.Elements,
    ArrayRepeatExpr array => [array.Value, array.Count],
    StructExpr structure => [.. structure.Fields.Select(field => field.Value), structure.Base],
    RangeExpr range => [range.Start, range.End],
    BlockExpr block => [block.Block],
    IfExpr conditional => [conditional.Condition, conditional.Then, conditional.Else],
    MatchExpr match => [match.Scrutinee, .. match.Arms.SelectMany(arm => new object?[] { arm.Guard, arm.Body })],
    WhileExpr loop => [loop.Condition, loop.Body],
    LoopExpr loop => [loop.Body],
    ForExpr loop => [loop.Iterable, loop.Body],
    BreakExpr jump => [jump.Value],
    MacroExpr macro => macro.Arguments,
    _ => []
  };

}
