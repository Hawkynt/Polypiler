namespace Polypiler.Core.MachineSemantics.Regions;

/// <summary>
/// What a machine memory access touches, as evidence proves it (PRD §17.2). Each class names the effect
/// channels <see cref="RegionClassifier"/> gives the access; an access without evidence is
/// <see cref="UnknownMemory"/>, never a variable.
/// </summary>
public enum MemoryClass {

  /// <summary>An allocation private to the function: channel <c>Memory:&lt;allocation&gt;</c>.</summary>
  ProvenLocal,

  /// <summary>Memory others may see — a RAM or ROM range of the map, or a global allocation: channel <c>Memory:&lt;name&gt;</c>.</summary>
  ProvenShared,

  /// <summary>A buffer the caller passes under a declared contract: channel <c>Memory:ext.&lt;name&gt;</c>.</summary>
  ExternalMemory,

  /// <summary>Device registers of a memory-mapped range: channel <c>Device:mmio.&lt;range&gt;</c>; every access is observable.</summary>
  MemoryMappedIO,

  /// <summary>Nothing is known: channel <c>Memory.Unknown</c>, which interferes with every <c>Memory:*</c> channel.</summary>
  UnknownMemory,

  /// <summary>A write into an executable range: channel <c>Memory.ExecutableWrite:&lt;range&gt;</c>.</summary>
  ExecutableMemory,

  /// <summary>Storage a runtime owns: channel <c>Runtime.State:&lt;runtime&gt;</c>.</summary>
  RuntimeState

}
