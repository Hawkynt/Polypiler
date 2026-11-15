// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The classes a generated class nests where the framework's collections do not do what the IR says
/// (#1328), each written in C# 2 against the base library of .NET Framework 2.0. They follow the
/// specification of <c>docs/reference/collections.md</c>, written for this repository from ECMA-262
/// §24.1 and §24.2 and from the behaviour of .NET's <c>Dictionary</c> (the free list of its slots, the
/// enumeration rules of .NET Core 3.0).
/// </summary>
public static class CSharpCollectionClasses {

  /// <summary>The name of <see cref="KeyedSlots"/>.</summary>
  public const string KeyedSlotsName = "KeyedSlots";

  /// <summary>The name of <see cref="FloatKeys"/>.</summary>
  public const string FloatKeysName = "FloatKeys";

  /// <summary>The name of <see cref="SingleKeys"/>.</summary>
  public const string SingleKeysName = "SingleKeys";

  /// <summary>
  /// A map - a set is one whose values nothing reads - whose entries stand in slots in the order they
  /// are enumerated. With <c>reuse</c> a new key takes the slot freed last, or the next one, and
  /// <c>Clear</c> drops every slot: the order of .NET's <c>Dictionary</c>, whose enumerator checks
  /// <see cref="Version"/>, which only an insertion of a new key changes, as on .NET Core 3.0 and later.
  /// Without it a new key always takes the next slot, a removed one leaves its slot empty and
  /// <c>Clear</c> empties every slot: the order of JavaScript's <c>Map</c>, whose live loop goes on to
  /// what is inserted after it began.
  /// </summary>
  public const string KeyedSlots = """
    private sealed class KeyedSlots<TKey, TValue> {

      private readonly global::System.Collections.Generic.Dictionary<TKey, int> slotOf;
      private readonly global::System.Collections.Generic.List<TKey> keys = new global::System.Collections.Generic.List<TKey>();
      private readonly global::System.Collections.Generic.List<TValue> values = new global::System.Collections.Generic.List<TValue>();
      private readonly global::System.Collections.Generic.List<bool> used = new global::System.Collections.Generic.List<bool>();
      private readonly global::System.Collections.Generic.List<int> free = new global::System.Collections.Generic.List<int>();
      private readonly bool reuse;
      private int version;

      public KeyedSlots(bool reuse, global::System.Collections.Generic.IEqualityComparer<TKey> comparer) {
        this.reuse = reuse;
        this.slotOf = comparer == null ? new global::System.Collections.Generic.Dictionary<TKey, int>() : new global::System.Collections.Generic.Dictionary<TKey, int>(comparer);
      }

      public int Count {
        get {
          return this.slotOf.Count;
        }
      }

      public int Slots {
        get {
          return this.used.Count;
        }
      }

      public int Version {
        get {
          return this.version;
        }
      }

      public bool IsUsed(int slot) {
        return this.used[slot];
      }

      public TKey KeyAt(int slot) {
        return this.keys[slot];
      }

      public TValue ValueAt(int slot) {
        return this.values[slot];
      }

      public void Check(int expected) {
        if (expected != this.version) {
          throw new global::System.InvalidOperationException("Collection was modified; enumeration operation may not execute.");
        }
      }

      public bool ContainsKey(TKey key) {
        return this.slotOf.ContainsKey(key);
      }

      public bool TryGetValue(TKey key, out TValue value) {
        int slot;
        if (this.slotOf.TryGetValue(key, out slot)) {
          value = this.values[slot];
          return true;
        }
        value = default(TValue);
        return false;
      }

      public TValue Get(TKey key) {
        int slot;
        if (!this.slotOf.TryGetValue(key, out slot)) {
          throw new global::System.Collections.Generic.KeyNotFoundException("The given key was not present in the dictionary.");
        }
        return this.values[slot];
      }

      public void Put(TKey key, TValue value) {
        this.Insert(key, value, true);
      }

      public void Add(TKey key, TValue value) {
        if (!this.Insert(key, value, false)) {
          throw new global::System.ArgumentException("An item with the same key has already been added.");
        }
      }

      public bool TryAdd(TKey key, TValue value) {
        return this.Insert(key, value, false);
      }

      private bool Insert(TKey key, TValue value, bool replace) {
        int slot;
        if (this.slotOf.TryGetValue(key, out slot)) {
          if (replace) {
            this.values[slot] = value;
          }
          return false;
        }
        if (this.reuse && this.free.Count > 0) {
          slot = this.free[this.free.Count - 1];
          this.free.RemoveAt(this.free.Count - 1);
          this.keys[slot] = key;
          this.values[slot] = value;
          this.used[slot] = true;
        } else {
          slot = this.used.Count;
          this.keys.Add(key);
          this.values.Add(value);
          this.used.Add(true);
        }
        this.slotOf.Add(key, slot);
        this.version = this.version + 1;
        return true;
      }

      public bool Remove(TKey key) {
        int slot;
        if (!this.slotOf.TryGetValue(key, out slot)) {
          return false;
        }
        this.slotOf.Remove(key);
        this.used[slot] = false;
        this.keys[slot] = default(TKey);
        this.values[slot] = default(TValue);
        if (this.reuse) {
          this.free.Add(slot);
        }
        return true;
      }

      public void Clear() {
        this.slotOf.Clear();
        this.free.Clear();
        if (this.reuse) {
          this.keys.Clear();
          this.values.Clear();
          this.used.Clear();
          return;
        }
        for (int slot = 0; slot < this.used.Count; ++slot) {
          this.used[slot] = false;
          this.keys[slot] = default(TKey);
          this.values[slot] = default(TValue);
        }
      }

    }
    """;

  /// <summary>
  /// The equality of <c>double</c> keys the IR has, with a hash that agrees with it on .NET Framework,
  /// whose <c>double.GetHashCode</c> hashes the NaNs and the two zeros apart: every NaN is one key,
  /// <c>-0</c> and <c>+0</c> are one key.
  /// </summary>
  public const string FloatKeys = """
    private sealed class FloatKeys : global::System.Collections.Generic.IEqualityComparer<double> {

      public bool Equals(double x, double y) {
        return x.Equals(y);
      }

      public int GetHashCode(double value) {
        return double.IsNaN(value) ? -1 : value == 0.0 ? 0 : value.GetHashCode();
      }

    }
    """;

  /// <summary>The equality of <c>float</c> keys, as <see cref="FloatKeys"/>.</summary>
  public const string SingleKeys = """
    private sealed class SingleKeys : global::System.Collections.Generic.IEqualityComparer<float> {

      public bool Equals(float x, float y) {
        return x.Equals(y);
      }

      public int GetHashCode(float value) {
        return float.IsNaN(value) ? -1 : value == 0.0f ? 0 : value.GetHashCode();
      }

    }
    """;

}
