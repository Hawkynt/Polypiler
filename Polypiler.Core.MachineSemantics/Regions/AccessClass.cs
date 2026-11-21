using System;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Regions;

/// <summary>The class of one memory access, the effect channels it is on, and the evidence for both.</summary>
/// <param name="Class">The class.</param>
/// <param name="Channels">The effect channels, in the order of the evidence; never empty.</param>
/// <param name="Reason">The rule's evidence, such as <c>map range 'vga'</c>, <c>private allocation 'buf'</c> or <c>no provenance</c>.</param>
public sealed record AccessClass(MemoryClass Class, ImmutableArray<string> Channels, string Reason) {

  /// <inheritdoc/>
  public bool Equals(AccessClass? other)
    => other is not null && this.Class == other.Class && this.Channels.SequenceEqual(other.Channels) && this.Reason == other.Reason;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Class, this.Channels.Length, this.Reason);

  /// <inheritdoc/>
  public override string ToString() => $"{this.Class} [{string.Join(", ", this.Channels)}] ({this.Reason})";

}

/// <summary>
/// The spelling of the effect channels of memory classes. They are plain strings until the effect
/// channels of the tree IR exist; the spelling is theirs.
/// </summary>
public static class MemoryChannels {

  /// <summary>The channel of unknown memory.</summary>
  public const string Unknown = "Memory.Unknown";

  /// <summary>The channel of a memory-mapped range.</summary>
  /// <param name="range">The range's name.</param>
  /// <returns><c>Device:mmio.&lt;range&gt;</c>.</returns>
  public static string Device(string range) => "Device:mmio." + range;

  /// <summary>The channel of a write into an executable range.</summary>
  /// <param name="range">The range's name.</param>
  /// <returns><c>Memory.ExecutableWrite:&lt;range&gt;</c>.</returns>
  public static string ExecutableWrite(string range) => "Memory.ExecutableWrite:" + range;

  /// <summary>The channel of a range or allocation that is ordinary memory.</summary>
  /// <param name="name">The range or allocation.</param>
  /// <returns><c>Memory:&lt;name&gt;</c>.</returns>
  public static string Memory(string name) => "Memory:" + name;

  /// <summary>The channel of a caller-supplied buffer.</summary>
  /// <param name="name">The buffer's name in the contract.</param>
  /// <returns><c>Memory:ext.&lt;name&gt;</c>.</returns>
  public static string External(string name) => "Memory:ext." + name;

  /// <summary>The channel of runtime-owned storage.</summary>
  /// <param name="runtime">The runtime.</param>
  /// <returns><c>Runtime.State:&lt;runtime&gt;</c>.</returns>
  public static string Runtime(string runtime) => "Runtime.State:" + runtime;

}
