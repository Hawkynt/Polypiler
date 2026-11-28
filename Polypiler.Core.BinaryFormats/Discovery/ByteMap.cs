using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.BinaryFormats.Discovery;

/// <summary>What discovery made of a byte.</summary>
public enum ByteClass {

  /// <summary>No traversed control flow reached it.</summary>
  Undecoded,

  /// <summary>Part of a decoded instruction.</summary>
  Code,

  /// <summary>Part of a validated jump table.</summary>
  Data,

  /// <summary>Part of two instructions that start at different addresses.</summary>
  Conflict

}

/// <summary>The class of every byte: <see cref="ByteClass.Undecoded"/> unless discovery says otherwise.</summary>
public sealed class ByteMap : IEquatable<ByteMap> {

  private readonly SortedDictionary<ulong, ByteClass> _classes = [];

  /// <summary>The class of the byte at an address.</summary>
  /// <param name="address">The address.</param>
  public ByteClass this[ulong address] => this._classes.GetValueOrDefault(address);

  /// <summary>Every byte that is not undecoded, with its class, by address.</summary>
  public IEnumerable<KeyValuePair<ulong, ByteClass>> Classified => this._classes;

  /// <summary>The number of bytes of a class among those discovery classified; undecoded ones are not counted.</summary>
  /// <param name="kind">The class.</param>
  public int Count(ByteClass kind) => this._classes.Values.Count(value => value == kind);

  /// <summary>Sets the class of the bytes from an address; a conflict stays a conflict, and data stays data unless it becomes a conflict.</summary>
  internal void Mark(ulong address, int count, ByteClass kind) {
    for (var offset = 0UL; offset < (ulong)count; ++offset) {
      var at = address + offset;
      var current = this[at];
      if (current == ByteClass.Conflict || current == ByteClass.Data && kind == ByteClass.Code)
        continue;

      this._classes[at] = kind;
    }
  }

  /// <inheritdoc/>
  public bool Equals(ByteMap? other) => other is not null && this._classes.SequenceEqual(other._classes);

  /// <inheritdoc/>
  public override bool Equals(object? obj) => this.Equals(obj as ByteMap);

  /// <inheritdoc/>
  public override int GetHashCode() => this._classes.Count;

}
