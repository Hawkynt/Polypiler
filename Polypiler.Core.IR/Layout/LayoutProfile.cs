using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>The order in which the bytes of a value of more than one byte lie in memory (#113).</summary>
public enum IrByteOrder {

  /// <summary>The lowest byte first, at the lowest address.</summary>
  Little,

  /// <summary>The highest byte first, at the lowest address.</summary>
  Big

}

/// <summary>
/// The data model of a target as far as the bytes of a value are concerned (#110, #113): how large a pointer
/// is, how far a number is aligned at most, and in which order the bytes of a number lie in memory. A
/// <see cref="RecordLayout"/> of a record whose rule is not profile-independent, and every plain load or
/// store through a pointer that reinterprets bytes, depend on it - the only place where host byte order
/// exists in the IR.
/// <para>
/// A number of <c>n</c> bytes is aligned to <c>min(n, MaxAlignment)</c>: that is the System V AMD64 ABI
/// (§3.1.2) for <see cref="Lp64SysV"/>, the System V i386 ABI for <see cref="Ilp32SysV"/>, whose
/// <c>double</c> and <c>long long</c> are aligned to 4 inside a struct, and the 16-bit DOS compilers'
/// word alignment for <see cref="Dos16"/>.
/// </para>
/// </summary>
public sealed record LayoutProfile {

  private readonly string _name = "";
  private readonly int _pointerSize = 8;
  private readonly int _maxAlignment = 8;

  /// <summary>Makes a profile.</summary>
  /// <param name="name">The name, such as <c>lp64-sysv</c>.</param>
  /// <param name="pointerSize">The size of a pointer in bytes: 2, 4 or 8.</param>
  /// <param name="maxAlignment">The largest alignment of a number, a power of two.</param>
  /// <param name="byteOrder">The byte order of memory.</param>
  /// <exception cref="ArgumentException">The name is empty, or a size or an alignment is not one a profile can have.</exception>
  public LayoutProfile(string name, int pointerSize, int maxAlignment, IrByteOrder byteOrder) {
    this.Name = name;
    this.PointerSize = pointerSize;
    this.MaxAlignment = maxAlignment;
    this.ByteOrder = byteOrder;
  }

  /// <summary>The name, such as <c>lp64-sysv</c>.</summary>
  /// <exception cref="ArgumentException">The name is empty.</exception>
  public string Name {
    get => this._name;
    init => this._name = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("a layout profile needs a name", nameof(value)) : value;
  }

  /// <summary>The size of a pointer in bytes: 2, 4 or 8.</summary>
  /// <exception cref="ArgumentException">The size is none of them.</exception>
  public int PointerSize {
    get => this._pointerSize;
    init => this._pointerSize = value is 2 or 4 or 8 ? value : throw new ArgumentException($"a pointer of {value} bytes", nameof(value));
  }

  /// <summary>The largest alignment of a number, a power of two from 1 to 16.</summary>
  /// <exception cref="ArgumentException">The alignment is none of them.</exception>
  public int MaxAlignment {
    get => this._maxAlignment;
    init => this._maxAlignment = value is >= 1 and <= 16 && BitOperations.IsPow2(value) ? value : throw new ArgumentException($"an alignment of {value}", nameof(value));
  }

  /// <summary>The byte order of memory, which a plain load or store through a pointer uses.</summary>
  public IrByteOrder ByteOrder { get; init; }

  /// <summary>Linux, the BSDs and macOS on x86-64 and AArch64: 8-byte pointers, numbers aligned to their size, little-endian.</summary>
  public static LayoutProfile Lp64SysV { get; } = new("lp64-sysv", 8, 8, IrByteOrder.Little);

  /// <summary>Linux on i386: 4-byte pointers, numbers aligned to their size but at most 4, little-endian.</summary>
  public static LayoutProfile Ilp32SysV { get; } = new("ilp32-sysv", 4, 4, IrByteOrder.Little);

  /// <summary>16-bit DOS with near pointers: 2-byte pointers, numbers aligned to at most a word, little-endian.</summary>
  public static LayoutProfile Dos16 { get; } = new("dos16", 2, 2, IrByteOrder.Little);

  /// <summary>The profiles Polypiler knows by name, in the order of their pointer sizes, largest first.</summary>
  public static IReadOnlyList<LayoutProfile> Standard { get; } = [Lp64SysV, Ilp32SysV, Dos16];

  /// <summary>The standard profile of a name.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The profile, or <see langword="null"/> when no standard profile has the name.</returns>
  public static LayoutProfile? Find(string name) => Standard.FirstOrDefault(profile => profile.Name == name);

  /// <summary>The alignment of a number of a size: the size, but at most <see cref="MaxAlignment"/>.</summary>
  /// <param name="size">The size in bytes, at least 1.</param>
  /// <returns>The alignment.</returns>
  public int AlignmentOf(int size) => Math.Min(size, this.MaxAlignment);

  /// <summary>The profile as its name.</summary>
  public override string ToString() => this.Name;

}
