using System;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>How the accesses of a run are checked against the region they reach (#119).</summary>
public enum BoundsMode {

  /// <summary>Each access behaves as its contract says: a checked one traps, one whose contract promises it in bounds is not checked again.</summary>
  Contract,

  /// <summary>Every access is checked, also those whose contract promises them in bounds; a violation ends the run <see cref="ExecutionOutcome.Aborted"/> with <c>PLY0142</c>.</summary>
  CheckAll

}

/// <summary>
/// The options of a run of <see cref="IrInterpreter.Run"/> (#119). The defaults are those of the differential tests:
/// 10 000 000 steps, calls nested 10 000 deep, no external model, accesses as their contracts say, little-endian
/// memory, the flat memory model, the ascending schedule and no recorded values.
/// </summary>
public sealed record InterpreterOptions {

  /// <summary>The default of <see cref="MaxSteps"/>.</summary>
  public const long DefaultMaxSteps = 10_000_000;

  /// <summary>The default of <see cref="MaxCallDepth"/>.</summary>
  public const int DefaultMaxCallDepth = 10_000;

  /// <summary>The options of a run that sets none.</summary>
  public static InterpreterOptions Default { get; } = new();

  /// <summary>
  /// The most steps a run takes: each statement executed is one, and so is each call. The step that would exceed it
  /// ends the run <see cref="ExecutionOutcome.Aborted"/> with <c>PLY0121</c>, after exactly this many steps.
  /// </summary>
  public long MaxSteps { get; init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(this.MaxSteps), value, "not a number of steps"); } = DefaultMaxSteps;

  /// <summary>The most frames a run has at once, the function called being the first: one frame more ends it with <c>PLY0121</c>.</summary>
  public int MaxCallDepth { get; init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(this.MaxCallDepth), value, "a run has at least the frame of the function called"); } = DefaultMaxCallDepth;

  /// <summary>What the foreign functions do; <see langword="null"/> when nothing models them, so that a foreign call aborts with <c>PLY0123</c>.</summary>
  public IExternalModel? Externals { get; init; }

  /// <summary>How accesses are checked (<see cref="BoundsMode"/>).</summary>
  public BoundsMode Bounds { get; init; }

  /// <summary>The byte order of plain multi-byte loads and stores, which an evaluator reads from its context.</summary>
  public ByteOrder ByteOrder { get; init; }

  /// <summary>The mapping between addresses and (allocation, offset) pairs; <see langword="null"/> for <see cref="FlatMemoryModel.Instance"/>.</summary>
  public IMemoryModel? Memory { get; init; }

  /// <summary>The order and chunking of the iterations of a parallel node; <see langword="null"/> for <see cref="Schedules.Ascending"/>.</summary>
  public ISchedule? Schedule { get; init; }

  /// <summary>Whether every value a local takes is recorded, in <see cref="ExecutionResult.RecordedValues"/>.</summary>
  public bool RecordValues { get; init; }

  /// <summary>Evaluators that replace or extend the interpreter's own; <see langword="null"/> for none.</summary>
  public OpcodeEvaluatorRegistry? Evaluators { get; init; }

}

/// <summary>
/// The mapping between the addresses a program computes and the allocations they lie in (#119): the flat model by
/// default, the segmented model of #369 where a target has segments. The tree IR reaches memory through arrays and
/// bounded pointers, not addresses, so the interpreter itself never asks; it hands the model to the evaluators.
/// </summary>
public interface IMemoryModel {

  /// <summary>The address of a byte of an allocation.</summary>
  /// <param name="allocation">The number of the allocation, from 0.</param>
  /// <param name="offset">The offset of the byte in it.</param>
  /// <returns>The address.</returns>
  long Address(long allocation, long offset);

  /// <summary>The allocation and the offset an address names, or <see langword="null"/> when it names none.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The pair, or <see langword="null"/>.</returns>
  (long Allocation, long Offset)? Locate(long address);

}

/// <summary>
/// The flat memory model: allocation <c>n</c> occupies the 4 GiB from <c>(n + 1) * 2^32</c>, so the address 0 and the
/// first 4 GiB name no allocation, and every allocation has room for 2^32 bytes.
/// </summary>
public sealed class FlatMemoryModel : IMemoryModel {

  /// <summary>The bytes of the address space each allocation occupies.</summary>
  public const long Stride = 1L << 32;

  private FlatMemoryModel() { }

  /// <summary>The model.</summary>
  public static FlatMemoryModel Instance { get; } = new();

  /// <inheritdoc/>
  /// <exception cref="ArgumentOutOfRangeException">The allocation is negative or beyond the address space, or the offset outside <c>[0, Stride)</c>.</exception>
  public long Address(long allocation, long offset) {
    if (allocation is < 0 or >= long.MaxValue / Stride)
      throw new ArgumentOutOfRangeException(nameof(allocation), allocation, "not an allocation of the flat model");

    if (offset is < 0 or >= Stride)
      throw new ArgumentOutOfRangeException(nameof(offset), offset, "not an offset in an allocation of the flat model");

    return ((allocation + 1) * Stride) + offset;
  }

  /// <inheritdoc/>
  public (long Allocation, long Offset)? Locate(long address) => address < Stride ? null : ((address / Stride) - 1, address % Stride);

}

/// <summary>The order in which the iterations of a parallel node run, and how they are cut into chunks (#119).</summary>
public interface ISchedule {

  /// <summary>The order of <paramref name="count"/> iterations: every index from 0 to <c>count - 1</c>, once.</summary>
  /// <param name="count">The number of iterations.</param>
  /// <returns>The indices, in the order they run.</returns>
  ImmutableArray<long> Order(long count);

  /// <summary>The chunks of <paramref name="count"/> iterations: ranges that cover every index once, in the order they run.</summary>
  /// <param name="count">The number of iterations.</param>
  /// <returns>The ranges.</returns>
  ImmutableArray<(long Start, long Length)> Chunks(long count);

}

/// <summary>The built-in schedules: the deterministic ones a test chooses an order of iterations with.</summary>
public static class Schedules {

  /// <summary>Every iteration in ascending order, in one chunk.</summary>
  public static ISchedule Ascending { get; } = new Fixed(descending: false);

  /// <summary>Every iteration in descending order, each a chunk of its own, the last first.</summary>
  public static ISchedule Descending { get; } = new Fixed(descending: true);

  /// <summary>
  /// A permutation of the iterations and a cutting into chunks chosen by a seed: the same seed gives the same schedule
  /// on every host, since it draws from SplitMix64 rather than the host's random numbers.
  /// </summary>
  /// <param name="seed">The seed.</param>
  /// <returns>The schedule.</returns>
  public static ISchedule Seeded(ulong seed) => new Shuffled(seed);

  private static void Require(long count) {
    if (count < 0)
      throw new ArgumentOutOfRangeException(nameof(count), count, "not a number of iterations");
  }

  private sealed class Fixed(bool descending) : ISchedule {

    public ImmutableArray<long> Order(long count) {
      Require(count);
      var order = Enumerable.Range(0, checked((int)count)).Select(index => (long)index);
      return [.. descending ? order.Reverse() : order];
    }

    public ImmutableArray<(long Start, long Length)> Chunks(long count) {
      Require(count);
      if (count == 0)
        return [];

      return descending ? [.. this.Order(count).Select(index => (index, 1L))] : [(0L, count)];
    }

  }

  private sealed class Shuffled(ulong seed) : ISchedule {

    /// <summary>The generator SplitMix64 (Steele, Lea, Flood 2014), restarted from the seed for every question.</summary>
    private sealed class SplitMix64(ulong state) {

      public ulong Next() {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
      }

      /// <summary>A number in <c>[0, bound)</c>.</summary>
      public long Below(long bound) => (long)(this.Next() % (ulong)bound);

    }

    public ImmutableArray<long> Order(long count) {
      Require(count);
      var order = Enumerable.Range(0, checked((int)count)).Select(index => (long)index).ToArray();
      var random = new SplitMix64(seed);
      // Fisher-Yates, from the last position down.
      for (var index = order.Length - 1; index > 0; --index) {
        var other = (int)random.Below(index + 1);
        (order[index], order[other]) = (order[other], order[index]);
      }

      return [.. order];
    }

    public ImmutableArray<(long Start, long Length)> Chunks(long count) {
      Require(count);
      var random = new SplitMix64(~seed);
      var chunks = ImmutableArray.CreateBuilder<(long Start, long Length)>();
      for (var start = 0L; start < count;) {
        var length = 1 + random.Below(count - start);
        chunks.Add((start, length));
        start += length;
      }

      // The chunks run in an order of their own, drawn by the same seed.
      var order = this.Order(chunks.Count);
      return [.. order.Select(index => chunks[(int)index])];
    }

  }

}
