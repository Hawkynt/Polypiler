using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The element of a zipped array a parallel loop reads and writes in place of <c>a[i]</c>, by the
    /// loop's counter and the array: the name of the pattern's binding and whether it is a reference
    /// (<c>iter_mut</c>), which a read dereferences and a write assigns through.
    /// </summary>
    private readonly Dictionary<(IrLocal Counter, IrLocal Array), (string Name, bool Reference)> _zipped = [];

    /// <summary>
    /// A parallel loop, <c>for (&amp;s, d) in src.iter().zip(dst.iter_mut()) { … }</c>: a counted loop from 0
    /// to the shorter length of two arrays - <c>Min(a.Length, b.Length)</c>, directly or in a variable
    /// declared for it, as Rust's <c>zip</c> lowers - whose body uses the counter only to read and write
    /// the elements of those two arrays at it and touches neither array otherwise. <c>zip</c> stops at the
    /// shorter side as the bound does, so no bounds check is lost; an array only read is iterated by
    /// <c>iter()</c>, one written by <c>iter_mut()</c>. The variables of the bound, which nothing else reads,
    /// are left out. <see langword="null"/> for any other run of statements.
    /// </summary>
    /// <param name="statements">The statements of the block.</param>
    /// <param name="index">The position of the first statement of the run.</param>
    /// <param name="consumed">How many statements the loop stands for.</param>
    private RsStatement? ParallelLoop(IReadOnlyList<IrStmt> statements, int index, out int consumed) {
      consumed = 0;
      var position = index;
      var bounds = new List<IrLocal>();
      var lengths = new Dictionary<IrLocal, IrExpr>();
      while (position < statements.Count && statements[position] is IrDeclare { Initializer: { } initializer } declaration && !this._shared.Contains(declaration.Local)
          && (LengthSource(initializer) is not null || initializer is IrIntrinsic { Op: IrIntrinsicOp.Min })) {
        if (LengthSource(initializer) is { } measured)
          lengths[declaration.Local] = measured;

        bounds.Add(declaration.Local);
        ++position;
      }

      if (position + 1 >= statements.Count || statements[position] is not IrDeclare { Initializer: IrConst { Value: 0 } } counter
          || Counted(counter, statements[position + 1]) is not ( { } bound, { } body))
        return null;

      // The bound: Min(length of a, length of b), or a variable of the run that holds it.
      var minimum = bound;
      if (bound is IrLocalRef { Local: var held } && bounds.Contains(held))
        minimum = ((IrDeclare)statements[index + bounds.IndexOf(held)]).Initializer!;

      IrExpr? Measured(IrExpr length) => LengthSource(length) ?? (length is IrLocalRef { Local: var local } && lengths.TryGetValue(local, out var array) ? array : null);
      if (minimum is not IrIntrinsic { Op: IrIntrinsicOp.Min, Arguments: [var left, var right] } || Measured(left) is not IrLocalRef first || Measured(right) is not IrLocalRef second
          || first.Local == second.Local || this.IsNullableArray(first.Local) || this.IsNullableArray(second.Local))
        return null;

      var end = position + 2;
      // The variables of the bound are read by nothing but the run and the loop's test, the counter by nothing after the loop.
      if (bounds.Append(counter.Local).Any(local => statements.Skip(end).Any(after => Mentions(after, local)))
          || bounds.Any(local => body.Any(statement => Mentions(statement, local))))
        return null;

      var arrays = new[] { first.Local, second.Local };
      var nodes = body.SelectMany(Walk).SelectMany(ExpressionsOf).SelectMany(IrTree.Nodes).ToList();
      bool AtCounter(IrExpr array, IrExpr at) => array is IrLocalRef { Local: var local } && arrays.Contains(local) && at is IrLocalRef { Local: var i } && i == counter.Local;
      var loads = nodes.OfType<IrArrayLoad>().Where(load => AtCounter(load.Array, load.Index)).ToList();
      var stores = body.SelectMany(Walk).OfType<IrStore>().Where(store => AtCounter(store.Array, store.Index)).ToList();
      var counterUses = nodes.Count(node => node is IrLocalRef reference && reference.Local == counter.Local);
      if (counterUses != loads.Count + stores.Count
          || arrays.Any(array => array.Type.Element is not { } element || !IsValue(element) || Assigns(body, array)
            || nodes.Count(node => node is IrLocalRef reference && reference.Local == array) != loads.Count(load => ((IrLocalRef)load.Array).Local == array) + stores.Count(store => ((IrLocalRef)store.Array).Local == array))
          || nodes.Any(node => node is IrStep { Target: IrArrayLoad stepped } && AtCounter(stepped.Array, stepped.Index)))
        return null;

      var written = arrays.Select(array => stores.Any(store => ((IrLocalRef)store.Array).Local == array)).ToArray();
      var read = arrays.Select(array => loads.Any(load => ((IrLocalRef)load.Array).Local == array)).ToArray();
      var patterns = new string[2];
      for (var side = 0; side < 2; ++side) {
        if (!written[side] && !read[side]) {
          patterns[side] = "_";
          continue;
        }

        string name;
        if (!written[side] && body.Count > 0 && body[0] is IrDeclare { Initializer: IrArrayLoad { Array: IrLocalRef { Local: var named }, Index: IrLocalRef { Local: var at } } } element
            && named == arrays[side] && at == counter.Local && element.Local.Type == arrays[side].Type.Element && !Assigns(body, element.Local) && !this._shared.Contains(element.Local)) {
          name = this._locals[element.Local];
          body = [.. body.Skip(1)];
        } else {
          name = this.Temporary(written[side] ? "item" : "element");
        }

        this._zipped[(counter.Local, arrays[side])] = (name, written[side]);
        patterns[side] = written[side] ? name : "&" + name;
      }

      var label = this.NewLabel("l");
      var breakable = new Breakable(BreakableKind.Loop, label);
      this._breakables.Add(breakable);
      var block = this.Block(body);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      foreach (var array in arrays)
        this._zipped.Remove((counter.Local, array));

      RsExpression Iterator(int side) => new RsMethodCall(new RsPath(this._locals[arrays[side]]), written[side] ? "iter_mut" : "iter", []);
      var pattern = patterns.All(text => text == "_") ? "_" : $"({patterns[0]}, {patterns[1]})";
      consumed = end - index;
      return new RsExpressionStatement(new RsFor(breakable.Used ? label : null, pattern, new RsMethodCall(Iterator(0), "zip", [Iterator(1)]), block));
    }

    /// <summary>The array or view whose length an expression is, through the conversions of a length; <see langword="null"/> for anything else.</summary>
    private static IrExpr? LengthSource(IrExpr expression) => expression switch {
      IrArrayLength { Array: var array } => array,
      IrIntrinsic { Op: IrIntrinsicOp.ViewLength, Arguments: [var view] } => view,
      _ => null
    };

    /// <summary>The element of a zipped array at the counter of its parallel loop: its binding, dereferenced where it is a reference; <see langword="null"/> elsewhere.</summary>
    private RsExpression? ZippedElement(IrExpr array, IrExpr index)
      => array is IrLocalRef { Local: var local } && index is IrLocalRef { Local: var counter } && this._zipped.TryGetValue((counter, local), out var element)
        ? element.Reference ? new RsUnary("*", new RsPath(element.Name)) { Type = array.Type.Element } : new RsPath(element.Name) { Type = array.Type.Element }
        : null;

    /// <summary>
    /// The parts of a counted loop: the declaration of an integer variable, then a <c>while</c> that tests
    /// it against a bound with <c>&lt;</c> and whose body ends with the step by one and has no
    /// <c>continue</c> of its own, or a <c>for</c> whose step is that step alone; its body assigns the counter
    /// nowhere else. The bound and the body without the step, or <see langword="null"/>.
    /// </summary>
    private (IrExpr? Bound, List<IrStmt>? Body) Counted(IrDeclare declare, IrStmt loop) {
      IrExpr condition;
      List<IrStmt> rest;
      IrStmt step;
      switch (loop) {
        case IrWhile { Body.Statements.Count: > 0 } plain when !ContinuesLoop(plain.Body.Statements.Take(plain.Body.Statements.Count - 1)):
          (condition, rest, step) = (plain.Condition, [.. plain.Body.Statements.Take(plain.Body.Statements.Count - 1)], plain.Body.Statements[^1]);
          break;
        case IrFor { Step.Statements: [var only] } counted:
          (condition, rest, step) = (counted.Condition, [.. counted.Body.Statements], only);
          break;
        default:
          return (null, null);
      }

      if (condition is not IrBinary { Op: IrBinaryOp.Less, Left: IrLocalRef counter, Right: var bound } || counter.Local != declare.Local || !counter.Type.IsInteger || this._shared.Contains(counter.Local))
        return (null, null);

      var stepsByOne = step switch {
        IrAssign { Value: IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.AddChecked, Left: IrLocalRef self, Right: IrConst { Value: 1 } } } assign => assign.Local == declare.Local && self.Local == declare.Local,
        IrExprStmt { Expression: IrStep { Target: IrLocalRef self, Increment: true } } => self.Local == declare.Local,
        _ => false
      };
      return stepsByOne && !Assigns(rest, declare.Local) ? (bound, rest) : (null, null);
    }

  }

}
