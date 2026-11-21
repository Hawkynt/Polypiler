using System;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>
/// The formulas every pass prices its alternatives with, so that no pass carries a threshold of its own.
/// All arithmetic is exact 64-bit integer arithmetic; a result beyond the range of <see cref="long"/> is
/// <see cref="long.MaxValue"/>, so the ranking is the same on every host.
/// </summary>
public static class CostFormulas {

  /// <summary>
  /// A shared helper: <c>callSites × call + body</c> — every call site pays the call, the body is counted
  /// once per module.
  /// </summary>
  /// <param name="call">The cost of one call, the body's execution included.</param>
  /// <param name="body">The cost of the helper's body, paid once.</param>
  /// <param name="callSites">The number of call sites.</param>
  /// <returns>The cost.</returns>
  public static Cost Helper(Cost call, Cost body, long callSites) => call * callSites + body;

  /// <summary>A loop: latency <c>trip × (body + overhead)</c>, size <c>body + overhead</c>.</summary>
  /// <param name="body">The cost of one iteration's body.</param>
  /// <param name="overhead">The profile's <c>loop.overhead</c>: the test and the step of one iteration.</param>
  /// <param name="trip">The trip count.</param>
  /// <returns>The cost.</returns>
  public static Cost Loop(Cost body, long overhead, long trip)
    => body with { Latency = Multiply(Add(body.Latency, overhead), trip), Size = Add(body.Size, overhead) };

  /// <summary>The loop unrolled completely: latency and size <c>trip × body</c>.</summary>
  /// <param name="body">The cost of one iteration's body.</param>
  /// <param name="trip">The trip count.</param>
  /// <returns>The cost.</returns>
  public static Cost Unrolled(Cost body, long trip)
    => body with { Latency = Multiply(body.Latency, trip), Size = Multiply(body.Size, trip) };

  /// <summary>
  /// A blocked data path (SWAR or SIMD): latency <c>setup + (n / lanes) × perBlock + (n mod lanes) × scalar</c>
  /// with integer division, the tail element by element; size <c>setup + perBlock + scalar</c>, the code of
  /// the setup, one block and the tail.
  /// </summary>
  /// <param name="setup">The cost of entering the path.</param>
  /// <param name="perBlock">The cost of one block of <paramref name="lanes"/> elements.</param>
  /// <param name="scalar">The cost of one element on the scalar path.</param>
  /// <param name="n">The number of elements, not negative.</param>
  /// <param name="lanes">The elements of one block, at least 1.</param>
  /// <returns>The cost.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="lanes"/> is below 1 or <paramref name="n"/> is negative.</exception>
  public static Cost Blocked(long setup, long perBlock, Cost scalar, long n, long lanes) {
    ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1);
    ArgumentOutOfRangeException.ThrowIfNegative(n);
    var latency = Add(Add(setup, Multiply(n / lanes, perBlock)), Multiply(n % lanes, scalar.Latency));
    return new(latency, Add(Add(setup, perBlock), scalar.Size));
  }

  /// <summary>The work spread over threads: latency <c>spawn + (work + threads − 1) / threads</c>, no code size.</summary>
  /// <param name="spawn">The cost of starting and joining the threads.</param>
  /// <param name="work">The units of work, not negative.</param>
  /// <param name="threads">The number of threads, at least 1.</param>
  /// <returns>The cost.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="threads"/> is below 1 or <paramref name="work"/> is negative.</exception>
  public static Cost Threaded(long spawn, long work, long threads) {
    ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
    ArgumentOutOfRangeException.ThrowIfNegative(work);
    return new(Add(spawn, work / threads + (work % threads == 0 ? 0 : 1)), 0);
  }

  /// <summary>The work moved to an accelerator (GPU, NPU): latency <c>transfer + work × perElement</c>, no code size.</summary>
  /// <param name="transfer">The cost of moving the data there and back.</param>
  /// <param name="perElement">The cost of one unit of work there.</param>
  /// <param name="work">The units of work.</param>
  /// <returns>The cost.</returns>
  public static Cost Offloaded(long transfer, long perElement, long work) => new(Add(transfer, Multiply(work, perElement)), 0);

  /// <summary>An exact sum, <see cref="long.MaxValue"/> where it overflows.</summary>
  /// <param name="left">The first summand.</param>
  /// <param name="right">The second summand.</param>
  /// <returns>The sum.</returns>
  public static long Add(long left, long right) => Clamp((Int128)left + right);

  /// <summary>An exact product, <see cref="long.MaxValue"/> where it overflows.</summary>
  /// <param name="left">The first factor.</param>
  /// <param name="right">The second factor.</param>
  /// <returns>The product.</returns>
  public static long Multiply(long left, long right) => Clamp((Int128)left * right);

  /// <summary>A value of a wider computation as a score: itself when <see cref="long"/> holds it, else <see cref="long.MaxValue"/>.</summary>
  internal static long Clamp(Int128 value) => value > long.MaxValue || value < long.MinValue ? long.MaxValue : (long)value;

}
