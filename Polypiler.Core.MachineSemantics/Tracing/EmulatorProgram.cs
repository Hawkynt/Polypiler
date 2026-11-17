using System;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>A machine-code image an emulator oracle loads and runs (PRD §21.4).</summary>
/// <param name="Image">The bytes of the image.</param>
/// <param name="LoadAddress">The address the first byte is loaded at.</param>
/// <param name="Entry">The address execution starts at.</param>
/// <param name="Mode">The processor mode, for example <c>real</c>, <c>protected</c> or <c>long</c>.</param>
public sealed record EmulatorProgram(ReadOnlyMemory<byte> Image, ulong LoadAddress, ulong Entry, string Mode);
