using System;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>
/// The safety limits of one input: how long it may run, how much it may allocate and how long it may
/// be. They turn a hang or a runaway allocation into a finding; they are not performance targets.
/// </summary>
/// <param name="PerInput">The time one input may take before it counts as a <see cref="FindingKind.Hang"/>.</param>
/// <param name="MaxAllocatedBytes">The bytes one input may allocate before it counts as an <see cref="FindingKind.Allocation"/>.</param>
/// <param name="MaxInputLength">The longest input executed; longer inputs are truncated to this length.</param>
public sealed record FuzzBudget(TimeSpan PerInput, long MaxAllocatedBytes, int MaxInputLength) {

  /// <summary>The defaults: 1 s, 64 MiB and 65 536 bytes.</summary>
  public static FuzzBudget Default { get; } = new(TimeSpan.FromSeconds(1), 64L * 1024 * 1024, 65536);

  /// <summary>The time one input may take; positive.</summary>
  public TimeSpan PerInput { get; } = PerInput > TimeSpan.Zero ? PerInput : throw new ArgumentOutOfRangeException(nameof(PerInput), PerInput, "the time per input must be positive");

  /// <summary>The bytes one input may allocate; not negative.</summary>
  public long MaxAllocatedBytes { get; } = MaxAllocatedBytes >= 0 ? MaxAllocatedBytes : throw new ArgumentOutOfRangeException(nameof(MaxAllocatedBytes), MaxAllocatedBytes, "the allocation budget must not be negative");

  /// <summary>The longest input executed; not negative.</summary>
  public int MaxInputLength { get; } = MaxInputLength >= 0 ? MaxInputLength : throw new ArgumentOutOfRangeException(nameof(MaxInputLength), MaxInputLength, "the input length must not be negative");

}
