using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>
/// One observable thing a routine did, in the order it did it (PRD §23.1): a memory write, a device
/// access, an external call, a trap or the return.
/// </summary>
public abstract record TraceEvent {

  /// <summary>The kind <c>memory-write</c>, as a projection and a vector file name it.</summary>
  public const string MemoryWriteKind = "memory-write";

  /// <summary>The kind <c>device</c>.</summary>
  public const string DeviceKind = "device";

  /// <summary>The kind <c>external</c>.</summary>
  public const string ExternalKind = "external";

  /// <summary>The kind <c>trap</c>.</summary>
  public const string TrapKind = "trap";

  /// <summary>The kind <c>return</c>.</summary>
  public const string ReturnKind = "return";

  /// <summary>Every event kind, in the order the vector file lists them.</summary>
  public static ImmutableArray<string> Kinds { get; } = [DeviceKind, ExternalKind, TrapKind, MemoryWriteKind, ReturnKind];

  /// <summary>The event's kind, one of <see cref="Kinds"/>.</summary>
  public abstract string EventKind { get; }

  /// <summary>The event as one line of text, as a difference names it.</summary>
  public abstract string Text { get; }

  /// <summary>A number as <c>0x</c> and upper-case hexadecimal digits without leading zeros.</summary>
  /// <param name="value">The number.</param>
  /// <returns>The text.</returns>
  public static string Hex(ulong value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

  /// <summary>An optional number as <see cref="Hex(ulong)"/>, or <c>none</c>.</summary>
  /// <param name="value">The number.</param>
  /// <returns>The text.</returns>
  public static string Hex(ulong? value) => value is { } some ? Hex(some) : "none";

}

/// <summary>Bytes written to memory, lowest address first.</summary>
/// <param name="Address">The first address written.</param>
/// <param name="Bytes">The bytes, at least one.</param>
public sealed record MemoryWrite(ulong Address, ImmutableArray<byte> Bytes) : TraceEvent {

  /// <inheritdoc/>
  public override string EventKind => MemoryWriteKind;

  /// <inheritdoc/>
  public override string Text => $"memory-write {Hex(this.Address)} = {Convert.ToHexString(this.Bytes.AsSpan())}";

  /// <inheritdoc/>
  public bool Equals(MemoryWrite? other) => other is not null && this.Address == other.Address && this.Bytes.SequenceEqual(other.Bytes);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Address, this.Bytes.Length);

}

/// <summary>A read or write of a device register: an I/O port or memory-mapped I/O.</summary>
/// <param name="Space">The address space, for example <c>io</c> or <c>mmio</c>.</param>
/// <param name="Address">The device register's address.</param>
/// <param name="Width">The access width in bits.</param>
/// <param name="IsWrite">Whether the routine wrote; otherwise it read.</param>
/// <param name="Value">The value written or read.</param>
public sealed record DeviceAccess(string Space, ulong Address, int Width, bool IsWrite, ulong Value) : TraceEvent {

  /// <inheritdoc/>
  public override string EventKind => DeviceKind;

  /// <inheritdoc/>
  public override string Text => $"device-{(this.IsWrite ? "write" : "read")} {this.Space} {Hex(this.Address)} u{this.Width} = {Hex(this.Value)}";

}

/// <summary>A call out of the routine to code that is not part of it.</summary>
/// <param name="Symbol">The callee.</param>
/// <param name="Abi">The calling convention, or <see langword="null"/> when the reference does not name one.</param>
/// <param name="Arguments">The integer arguments, in order.</param>
/// <param name="Result">The value the callee returned, or <see langword="null"/>.</param>
public sealed record ExternalCall(string Symbol, string? Abi, ImmutableArray<ulong> Arguments, ulong? Result) : TraceEvent {

  /// <inheritdoc/>
  public override string EventKind => ExternalKind;

  /// <inheritdoc/>
  public override string Text
    => $"external {this.Symbol}{(this.Abi is null ? "" : " " + this.Abi)}({string.Join(", ", this.Arguments.Select(argument => Hex(argument)))}) -> {Hex(this.Result)}";

  /// <inheritdoc/>
  public bool Equals(ExternalCall? other)
    => other is not null && this.Symbol == other.Symbol && this.Abi == other.Abi && this.Arguments.SequenceEqual(other.Arguments) && this.Result == other.Result;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Symbol, this.Abi, this.Arguments.Length, this.Result);

}

/// <summary>A trap: an exception, an interrupt or a fault the routine raised.</summary>
/// <param name="Kind">The trap's kind, for example <c>divide-error</c>.</param>
/// <param name="Vector">The interrupt vector, or <see langword="null"/>.</param>
/// <param name="At">The address of the instruction that trapped, or <see langword="null"/>.</param>
public sealed record Trap(string Kind, int? Vector, ulong? At) : TraceEvent {

  /// <inheritdoc/>
  public override string EventKind => TrapKind;

  /// <inheritdoc/>
  public override string Text
    => $"trap {this.Kind} vector {(this.Vector is { } vector ? vector.ToString(CultureInfo.InvariantCulture) : "none")} at {Hex(this.At)}";

}

/// <summary>The return to the caller.</summary>
/// <param name="Value">The value returned, or <see langword="null"/> when the routine returns none.</param>
public sealed record Return(ulong? Value) : TraceEvent {

  /// <inheritdoc/>
  public override string EventKind => ReturnKind;

  /// <inheritdoc/>
  public override string Text => $"return {Hex(this.Value)}";

}
