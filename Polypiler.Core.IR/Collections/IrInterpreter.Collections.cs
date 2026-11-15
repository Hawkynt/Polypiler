using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>A list: its elements and the count of its changes, which a checked loop compares.</summary>
  private sealed class ListValue {

    public List<object> Items { get; } = [];

    public int Version { get; set; }

  }

  /// <summary>
  /// A map or a set (whose values are all <see langword="null"/>): its entries in slots, in the order
  /// they are enumerated. A key takes a slot as its order says - the most recently freed one or the
  /// next unused one for <see cref="MapOrder.SlotReuse"/>, always the next unused one for
  /// <see cref="MapOrder.Insertion"/>. A removed entry leaves its slot empty; a clear of a slot-reuse
  /// map drops every slot, as .NET resets its count, and one of an insertion-ordered map empties them,
  /// so that a live loop goes on to the keys inserted after it.
  /// </summary>
  private sealed class TableValue(MapOrder order) {

    private readonly Dictionary<object, int> _index = new(KeyEquality.Default);
    private readonly Stack<int> _free = [];

    public List<Entry?> Slots { get; } = [];

    /// <summary>The count of insertions of new keys, which a checked loop compares.</summary>
    public int Version { get; private set; }

    public int Count => this._index.Count;

    public bool TryGet(object key, out object? value) {
      var found = this._index.TryGetValue(key, out var slot);
      value = found ? this.Slots[slot]!.Value : null;
      return found;
    }

    public bool Contains(object key) => this._index.ContainsKey(key);

    /// <summary>Inserts a key with its value, or replaces the value of the key present when <paramref name="replace"/> says so; says whether the key was new.</summary>
    public bool Put(object key, object? value, bool replace) {
      if (this._index.TryGetValue(key, out var present)) {
        if (replace)
          this.Slots[present]!.Value = value;

        return false;
      }

      var entry = new Entry(key, value);
      int slot;
      if (order == MapOrder.SlotReuse && this._free.Count > 0) {
        slot = this._free.Pop();
        this.Slots[slot] = entry;
      } else {
        slot = this.Slots.Count;
        this.Slots.Add(entry);
      }

      this._index[key] = slot;
      ++this.Version;
      return true;
    }

    public bool Remove(object key) {
      if (!this._index.Remove(key, out var slot))
        return false;

      this.Slots[slot] = null;
      if (order == MapOrder.SlotReuse)
        this._free.Push(slot);

      return true;
    }

    public void Clear() {
      this._index.Clear();
      this._free.Clear();
      if (order == MapOrder.SlotReuse)
        this.Slots.Clear();
      else
        for (var slot = 0; slot < this.Slots.Count; ++slot)
          this.Slots[slot] = null;
    }

  }

  /// <summary>An entry of a map or a set: the key it was inserted with and its value.</summary>
  private sealed class Entry(object key, object? value) {

    public object Key { get; } = key;

    public object? Value { get; set; } = value;

  }

  /// <summary>
  /// The equality of keys: integers, truth values and strings by value, references by identity, and
  /// floating-point numbers by <c>Equals</c>, under which every NaN is one key and <c>-0</c> is <c>+0</c>.
  /// </summary>
  private sealed class KeyEquality : IEqualityComparer<object> {

    public static KeyEquality Default { get; } = new();

    public new bool Equals(object? x, object? y) => x is Instance || y is Instance || x is NullReference || y is NullReference ? ReferenceEquals(x, y) : object.Equals(x, y);

    public int GetHashCode(object value) => value switch {
      double number => double.IsNaN(number) ? -1 : number == 0 ? 0 : number.GetHashCode(),
      float number => float.IsNaN(number) ? -1 : number == 0 ? 0 : number.GetHashCode(),
      Instance or NullReference => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value),
      _ => value.GetHashCode()
    };

  }

  /// <summary>An element a list searches by <c>Equals</c>: a struct by its fields, a record class by its fields, any other reference by identity.</summary>
  private static bool ElementEquals(IrType type, object element, object value)
    => type.IsStruct ? Same(element, value) : GenericEquals(type, element, value);

  /// <summary>An element JavaScript compares with <c>===</c>: a NaN equals nothing, <c>-0</c> equals <c>0</c>, a reference only itself.</summary>
  private static bool StrictEquals(object element, object value) => element switch {
    double number => value is double other && number == other,
    float number => value is float other && number == other,
    Instance or NullReference => ReferenceEquals(element, value),
    Record => Same(element, value),
    _ => element.Equals(value)
  };

  private static ListValue AsList(object value) => (ListValue)value;

  private static TableValue AsTable(object value) => (TableValue)value;

  /// <summary>An index of a list checked against its count, or the count itself where <paramref name="end"/> admits it.</summary>
  private static int ListIndex(ListValue list, object index, bool end = false) {
    var position = (long)index;
    return position < 0 || position > list.Items.Count || (position == list.Items.Count && !end) ? throw new IrTrapException(IrTrap.ArgumentOutOfRange) : (int)position;
  }

  private sealed partial class Machine {

    /// <summary>An operation on a collection; <see langword="null"/> for one without a value.</summary>
    private object Collection(IrCollection operation, Frame frame) {
      var arguments = operation.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
      var collection = IrCollections.Constructs(operation.Op) ? operation.Type : operation.Arguments[0].Type;
      switch (operation.Op) {
        case IrCollectionOp.ListNew:
          return new ListValue();
        case IrCollectionOp.ListFromArray:
          var made = new ListValue();
          var array = (Array)NotNull(arguments[0]);
          for (var index = 0; index < array.Length; ++index)
            made.Items.Add(Copied(LoadElement(array, index)));

          return made;
        case IrCollectionOp.MapNew or IrCollectionOp.SetNew:
          return new TableValue(operation.Type.Order);
        case IrCollectionOp.ListCount:
          return (long)AsList(arguments[0]).Items.Count;
        case IrCollectionOp.MapCount or IrCollectionOp.SetCount:
          return (long)AsTable(arguments[0]).Count;
      }

      if (operation.Op <= IrCollectionOp.ListFromArray)
        return this.ListOperation(operation.Op, collection.Element!, AsList(arguments[0]), arguments);

      return TableOperation(operation.Op, collection, AsTable(arguments[0]), arguments);
    }

    private object ListOperation(IrCollectionOp op, IrType element, ListValue list, object[] arguments) {
      var items = list.Items;
      switch (op) {
        case IrCollectionOp.ListGet:
          return Copied(items[ListIndex(list, arguments[1])]);
        case IrCollectionOp.ListSet:
          items[ListIndex(list, arguments[1])] = arguments[2];
          ++list.Version;
          return null!;
        case IrCollectionOp.ListAdd:
          items.Add(arguments[1]);
          ++list.Version;
          return null!;
        case IrCollectionOp.ListInsert:
          items.Insert(ListIndex(list, arguments[1], true), arguments[2]);
          ++list.Version;
          return null!;
        case IrCollectionOp.ListRemoveAt:
          items.RemoveAt(ListIndex(list, arguments[1]));
          ++list.Version;
          return null!;
        case IrCollectionOp.ListRemoveLast:
          if (items.Count == 0)
            return element.IsObject ? NullReference.Value : Absent.Value;

          var last = items[^1];
          items.RemoveAt(items.Count - 1);
          ++list.Version;
          return last;
        case IrCollectionOp.ListClear:
          items.Clear();
          ++list.Version;
          return null!;
        case IrCollectionOp.ListContains:
          return items.Exists(item => ElementEquals(element, item, arguments[1]));
        case IrCollectionOp.ListIndexOfEquals:
          return (long)items.FindIndex(item => ElementEquals(element, item, arguments[1]));
        case IrCollectionOp.ListIndexOfStrict:
          return (long)items.FindIndex(item => StrictEquals(item, arguments[1]));
        case IrCollectionOp.ListIncludesSameValueZero:
          return items.Exists(item => element.IsObject ? ReferenceEquals(item, arguments[1]) : ElementEquals(element, item, arguments[1]));
        default:
          var copy = Array.CreateInstance(HostType(element), items.Count);
          for (var index = 0; index < items.Count; ++index)
            copy.SetValue(External(copy.GetType().GetElementType()!, Copied(items[index])), index);

          return copy;
      }
    }

    private static object TableOperation(IrCollectionOp op, IrType type, TableValue table, object[] arguments) {
      switch (op) {
        case IrCollectionOp.MapGet:
          return table.TryGet(arguments[1], out var value) ? Copied(value!) : throw new IrTrapException(IrTrap.KeyNotFound);
        case IrCollectionOp.MapTryGet:
          return table.TryGet(arguments[1], out var held) ? Copied(held!) : type.Element!.IsObject ? NullReference.Value : Absent.Value;
        case IrCollectionOp.MapContainsKey or IrCollectionOp.SetContains:
          return table.Contains(arguments[1]);
        case IrCollectionOp.MapPut:
          table.Put(arguments[1], arguments[2], true);
          return null!;
        case IrCollectionOp.MapAdd:
          return table.Put(arguments[1], arguments[2], false) ? null! : throw new IrTrapException(IrTrap.DuplicateKey);
        case IrCollectionOp.MapTryAdd:
          return table.Put(arguments[1], arguments[2], false);
        case IrCollectionOp.SetAdd:
          return table.Put(arguments[1], null, false);
        case IrCollectionOp.MapRemove or IrCollectionOp.SetRemove:
          return table.Remove(arguments[1]);
        default:
          table.Clear();
          return null!;
      }
    }

    /// <summary>
    /// A loop over a collection. A checked loop compares the count of changes before every step, the
    /// one that would end the loop included; a live one reads the length, or the slots, at every step.
    /// </summary>
    private Flow ForEachCollection(IrForEachCollection loop, Frame frame) {
      var collection = this.Evaluate(loop.Collection, frame);
      var checks = loop.Mode == EnumerationMode.Checked;
      if (collection is ListValue list) {
        var version = list.Version;
        for (var index = 0; ; ++index) {
          if (checks && list.Version != version)
            throw new IrTrapException(IrTrap.CollectionModified);

          if (index >= list.Items.Count)
            return Flow.Next;

          frame.Locals[loop.Element!] = Copied(list.Items[index]);
          var pass = this.Run(loop.Body, frame);
          if (pass is Flow.Break or Flow.Return)
            return pass & Flow.Return;
        }
      }

      var table = AsTable(collection);
      var inserted = table.Version;
      for (var slot = 0; ; ++slot) {
        if (checks && table.Version != inserted)
          throw new IrTrapException(IrTrap.CollectionModified);

        if (slot >= table.Slots.Count)
          return Flow.Next;

        if (table.Slots[slot] is not { } entry)
          continue;

        if (loop.Element is { } element)
          frame.Locals[element] = entry.Key;

        if (loop.Value is { } value)
          frame.Locals[value] = Copied(entry.Value!);

        var pass = this.Run(loop.Body, frame);
        if (pass is Flow.Break or Flow.Return)
          return pass & Flow.Return;
      }
    }

  }

}
