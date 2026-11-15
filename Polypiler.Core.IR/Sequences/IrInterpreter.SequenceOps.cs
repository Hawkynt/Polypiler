using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// A cursor over the elements an enumerator of the interpreter computes: the operations on lazy
  /// sequences are written as iterators of their own, which open the cursors of their sources when they
  /// start and close them however they end - closing this cursor disposes the enumerator, which runs
  /// those finally blocks.
  /// </summary>
  private sealed class ComputedCursor(IEnumerator<object> enumerator) : Cursor {

    public override bool MoveNext() {
      if (!enumerator.MoveNext())
        return false;

      this.Current = enumerator.Current;
      return true;
    }

    public override void Close() => enumerator.Dispose();

  }

  /// <summary>The values of a sequence, one enumeration of it: the cursor is opened when the first value is asked for and closed however the enumeration ends.</summary>
  private static IEnumerable<object> Values(SequenceValue sequence) {
    var cursor = sequence.Open();
    try {
      while (cursor.MoveNext())
        yield return cursor.Current;
    } finally {
      cursor.Close();
    }
  }

  /// <summary>A sequence whose every enumeration runs an iterator of the interpreter afresh.</summary>
  private static SequenceValue Computed(bool restartable, Func<IEnumerable<object>> values)
    => new(restartable, () => new ComputedCursor(values().GetEnumerator()));

  /// <summary>The elements of a list as .NET enumerates them: a change of the list traps at the next step, also after the last element.</summary>
  private static IEnumerable<object> ListElements(ListValue list) {
    var version = list.Version;
    for (var index = 0; ; ++index) {
      if (list.Version != version)
        throw new IrTrapException(IrTrap.CollectionModified);

      if (index >= list.Items.Count)
        yield break;

      yield return Copied(list.Items[index]);
    }
  }

  /// <summary>The numbers of a range.</summary>
  private static IEnumerable<object> RangeElements(long start, long count) {
    for (var index = 0L; index < count; ++index)
      yield return start + index;
  }

  /// <summary>
  /// The order of two keys of an ordering: by <see cref="IrGenericCompare"/>'s rule for a number and a
  /// truth value, by code unit for a string.
  /// </summary>
  private static int KeyOrder(IrType key, object left, object right)
    => key.IsString ? Math.Sign(string.CompareOrdinal((string)left, (string)right)) : Math.Sign(GenericCompare(key, left, right));

  private sealed partial class Machine {

    /// <summary>
    /// An operation on lazy sequences as LINQ to Objects defines it, computed here without it: a source
    /// or a lazy operation is a restartable sequence that runs the operation on every enumeration, a
    /// terminal enumerates its sequence at once.
    /// </summary>
    private object SequenceOperation(IrSequence operation, Frame frame) {
      var arguments = operation.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
      switch (operation.Op) {
        case IrSequenceOp.FromArray:
          return ArraySequence((Array)NotNull(arguments[0]));
        case IrSequenceOp.FromList:
          var list = (ListValue)arguments[0];
          return Computed(true, () => ListElements(list));
        case IrSequenceOp.Range:
          var (start, count) = ((long)arguments[0], (long)arguments[1]);
          if (count < 0 || start + count - 1 > int.MaxValue)
            throw new IrTrapException(IrTrap.ArgumentOutOfRange);

          return Computed(true, () => RangeElements(start, count));
      }

      var source = (SequenceValue)arguments[0];
      var restartable = operation.Arguments[0].Type.Restartable;
      if (IrSequenceOps.IsOrdering(operation.Op)) {
        var level = new OrderingLevel(operation.Arguments[1].Type.Signature!.Result, arguments[1], operation.Op is IrSequenceOp.OrderByDescending or IrSequenceOp.ThenByDescending);
        var ordering = operation.Op is IrSequenceOp.OrderBy or IrSequenceOp.OrderByDescending
          ? new Ordering(source, [level])
          : new Ordering(source.Ordering!.Source, [.. source.Ordering.Levels, level]);
        return new SequenceValue(restartable, () => new ComputedCursor(this.Sorted(ordering).GetEnumerator())) { Ordering = ordering };
      }

      if (!operation.IsTerminal)
        return Computed(restartable, () => this.Lazy(operation, source, arguments));

      return this.Terminal(operation, Values(source), arguments);
    }

    /// <summary>One enumeration of a lazy operation.</summary>
    private IEnumerable<object> Lazy(IrSequence operation, SequenceValue source, object[] arguments) {
      var function = arguments[1];
      switch (operation.Op) {
        case IrSequenceOp.Select:
          return Values(source).Select(element => this.Call(function, [element])!);
        case IrSequenceOp.SelectIndexed:
          return this.Indexed(source, function, false);
        case IrSequenceOp.Where:
          return Values(source).Where(element => (bool)this.Call(function, [element])!);
        case IrSequenceOp.WhereIndexed:
          return this.Indexed(source, function, true);
        case IrSequenceOp.SelectMany or IrSequenceOp.SelectManyResult:
          return this.Flattened(source, function, operation.Op == IrSequenceOp.SelectManyResult ? arguments[2] : null);
        case IrSequenceOp.Take:
          return Taken(source, (long)function);
        case IrSequenceOp.Skip:
          return Skipped(source, (long)function);
        default:
          return this.Zipped(source, (SequenceValue)function, arguments[2]);
      }
    }

    private IEnumerable<object> Indexed(SequenceValue source, object function, bool filters) {
      var index = -1L;
      foreach (var element in Values(source)) {
        index = Fit(IrType.I32, index + 1);
        var result = this.Call(function, [element, index])!;
        if (!filters)
          yield return result;
        else if ((bool)result)
          yield return element;
      }
    }

    private IEnumerable<object> Flattened(SequenceValue source, object selector, object? combine) {
      foreach (var element in Values(source))
        foreach (var inner in Values((SequenceValue)NotNull(this.Call(selector, [element])!)))
          yield return combine is null ? inner : this.Call(combine, [element, inner])!;
    }

    private static IEnumerable<object> Taken(SequenceValue source, long count) {
      if (count <= 0)
        yield break;

      foreach (var element in Values(source)) {
        yield return element;
        if (--count == 0)
          yield break;
      }
    }

    private static IEnumerable<object> Skipped(SequenceValue source, long count) {
      foreach (var element in Values(source))
        if (count > 0)
          --count;
        else
          yield return element;
    }

    private IEnumerable<object> Zipped(SequenceValue first, SequenceValue second, object combine) {
      var cursor = first.Open();
      try {
        var other = second.Open();
        try {
          while (cursor.MoveNext() && other.MoveNext())
            yield return this.Call(combine, [cursor.Current, other.Current])!;
        } finally {
          other.Close();
        }
      } finally {
        cursor.Close();
      }
    }

    /// <summary>
    /// One enumeration of an ordering with its further keys: every element is pulled, then the keys of
    /// the first level are computed for every element in the order pulled, then those of the next
    /// level, and the elements are sorted stably by them.
    /// </summary>
    private IEnumerable<object> Sorted(Ordering ordering) {
      var levels = ordering.Levels;
      var elements = Values(ordering.Source).ToList();
      var keys = levels.Select(level => elements.Select(element => this.Call(level.Function, [element])!).ToList()).ToList();
      var order = Enumerable.Range(0, elements.Count).ToArray();
      MergeSort(order, (a, b) => {
        for (var level = 0; level < levels.Count; ++level) {
          var compared = KeyOrder(levels[level].Key, keys[level][a], keys[level][b]);
          if (compared != 0)
            return levels[level].Descending ? -compared : compared;
        }

        return 0;
      });
      foreach (var index in order)
        yield return elements[index];
    }

    /// <summary>A terminal: one enumeration of its sequence, as far as it needs.</summary>
    private object Terminal(IrSequence operation, IEnumerable<object> elements, object[] arguments) {
      var element = operation.Arguments[0].Type.Element!;
      bool Test(object value) => (bool)this.Call(arguments[1], [value])!;
      switch (operation.Op) {
        case IrSequenceOp.ToArray:
          return ArrayOf(element, elements.ToList());
        case IrSequenceOp.ToList:
          var list = new ListValue();
          list.Items.AddRange(elements);
          return list;
        case IrSequenceOp.Count or IrSequenceOp.CountWhere:
          var count = 0L;
          foreach (var value in elements)
            if (operation.Op == IrSequenceOp.Count || Test(value))
              count = Fit(IrType.I32, count + 1);

          return count;
        case IrSequenceOp.Any:
          using (var enumerator = elements.GetEnumerator())
            return enumerator.MoveNext();
        case IrSequenceOp.AnyWhere:
          foreach (var value in elements)
            if (Test(value))
              return true;

          return false;
        case IrSequenceOp.All:
          foreach (var value in elements)
            if (!Test(value))
              return false;

          return true;
        case IrSequenceOp.First or IrSequenceOp.FirstWhere or IrSequenceOp.FirstOrDefault or IrSequenceOp.FirstOrDefaultWhere:
          foreach (var value in elements)
            if (operation.Op is IrSequenceOp.First or IrSequenceOp.FirstOrDefault || Test(value))
              return value;

          return operation.Op switch {
            IrSequenceOp.First => throw Empty(IrSequenceOps.NoElements),
            IrSequenceOp.FirstWhere => throw Empty(IrSequenceOps.NoMatch),
            _ => Constant(element, 0)
          };
        case IrSequenceOp.Sum:
          return Sum(element, elements);
        case IrSequenceOp.Min or IrSequenceOp.Max:
          return Extreme(element, elements, operation.Op == IrSequenceOp.Max);
        case IrSequenceOp.Aggregate or IrSequenceOp.AggregateSeed:
          var seeded = operation.Op == IrSequenceOp.AggregateSeed;
          var step = arguments[seeded ? 2 : 1];
          object? result = seeded ? arguments[1] : null;
          var first = !seeded;
          foreach (var value in elements)
            if (first) {
              (result, first) = (value, false);
            } else {
              result = this.Call(step, [result!, value]);
            }

          return first ? throw Empty(IrSequenceOps.NoElements) : result!;
        case IrSequenceOp.Contains:
          foreach (var value in elements)
            if (value.Equals(arguments[1]))
              return true;

          return false;
        default:
          // IndexOf and FindIndex.
          var index = 0L;
          foreach (var value in elements) {
            if (operation.Op == IrSequenceOp.IndexOf ? value.Equals(arguments[1]) : Test(value))
              return index;

            ++index;
          }

          return -1L;
      }
    }

    /// <summary>The sum: a checked fold of integers, an <c>f32</c> sum accumulated in <c>f64</c>, an <c>f64</c> fold from <c>+0.0</c>.</summary>
    private static object Sum(IrType element, IEnumerable<object> elements) {
      switch (element.Kind) {
        case IrTypeKind.F32:
          return (float)elements.Aggregate(0.0, (sum, value) => sum + (float)value);
        case IrTypeKind.F64:
          return elements.Aggregate(0.0, (sum, value) => sum + (double)value);
        default:
          return elements.Aggregate(0L, (sum, value) => Fit(element, (Int128)sum + (long)value));
      }
    }

    /// <summary>The smallest or the largest element, as .NET's <c>Min</c> and <c>Max</c> find it.</summary>
    private static object Extreme(IrType element, IEnumerable<object> elements, bool largest) {
      object? best = null;
      foreach (var value in elements) {
        if (best is null) {
          best = value;
          if (!largest && IsNaN(value))
            return value;

          continue;
        }

        if (element.IsFloat) {
          var (candidate, current) = (System.Convert.ToDouble(value), System.Convert.ToDouble(best));
          if (largest ? candidate > current || double.IsNaN(current) : candidate < current)
            best = value;
          else if (!largest && double.IsNaN(candidate))
            return value;
        } else if (Math.Sign(GenericCompare(element, value, best)) == (largest ? 1 : -1)) {
          best = value;
        }
      }

      return best ?? throw Empty(IrSequenceOps.NoElements);
    }

    private static bool IsNaN(object value) => value is double number ? double.IsNaN(number) : value is float single && float.IsNaN(single);

  }

  /// <summary>The <c>InvalidOperationException</c> a terminal throws for a sequence without the element it needs, with .NET's message.</summary>
  private static IrException Empty(string message)
    => IrException.Thrown(NewException(IrExceptionClass.InvalidOperationException, message), IrExceptionClass.InvalidOperationException, message);

  /// <summary>
  /// Sorts stably by a comparison: the bottom-up merge sort of Sedgewick and Wayne, <i>Algorithms</i>,
  /// 4th ed., §2.2, which takes the element of the left run on a tie.
  /// </summary>
  private static void MergeSort(int[] items, Comparison<int> compare) {
    var buffer = new int[items.Length];
    for (var width = 1; width < items.Length; width *= 2)
      for (var low = 0; low < items.Length - width; low += 2 * width) {
        var middle = low + width;
        var high = Math.Min(low + 2 * width, items.Length);
        Array.Copy(items, low, buffer, low, high - low);
        var (left, right) = (low, middle);
        for (var index = low; index < high; ++index)
          items[index] = left >= middle ? buffer[right++]
            : right >= high ? buffer[left++]
            : compare(buffer[right], buffer[left]) < 0 ? buffer[right++]
            : buffer[left++];
      }
  }

  /// <summary>A key of an ordering: its type, its function and whether it descends.</summary>
  private sealed record OrderingLevel(IrType Key, object Function, bool Descending);

  /// <summary>An ordering as the interpreter holds it beside its sequence, so that a further key finds the source and the keys before it.</summary>
  private sealed record Ordering(SequenceValue Source, IReadOnlyList<OrderingLevel> Levels);

}
