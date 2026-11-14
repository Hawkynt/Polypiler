using System;
using System.Threading;
using Polypiler.Core.Verification.Fuzzing;

namespace Polypiler.Tests.Verification.Fuzzing;

// The fuzz targets whose behaviour the fuzzing harness is tested against: each fails on inputs known
// by construction, so the finding, its kind and its minimized input are known before the run. They
// are public with a parameterless constructor, so FuzzTargets.Discover finds them in the test
// assembly, and a worker process started with that assembly runs them. The acceptance tests compile
// this file too.

/// <summary><c>demo-marker</c>: throws <see cref="InvalidOperationException"/> when the input contains <c>50 4C 59</c> ("PLY").</summary>
public sealed class DemoMarkerTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-marker";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) {
    if (input.IndexOf("PLY"u8) >= 0)
      throw new InvalidOperationException("the marker PLY");
  }

}

/// <summary><c>demo-zero</c>: throws when the first byte is <c>00</c>.</summary>
public sealed class DemoZeroTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-zero";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) {
    if (input.Length > 0 && input[0] == 0)
      throw new ArgumentException("a leading zero byte");
  }

}

/// <summary><c>demo-hang</c>: never returns when the first byte is <c>FF</c>.</summary>
public sealed class DemoHangTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-hang";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) {
    if (input.Length > 0 && input[0] == 0xFF)
      while (true)
        Thread.Sleep(10);
  }

}

/// <summary><c>demo-alloc</c>: allocates 128 MiB when the first byte is <c>01</c>.</summary>
public sealed class DemoAllocTarget : IFuzzTarget {

  /// <summary>What an input starting with <c>01</c> allocates.</summary>
  public const int Allocation = 128 * 1024 * 1024;

  /// <inheritdoc />
  public string Id => "demo-alloc";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) {
    if (input.Length > 0 && input[0] == 1)
      GC.KeepAlive(new byte[Allocation]);
  }

}

/// <summary><c>demo-clean</c>: accepts every input.</summary>
public sealed class DemoCleanTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-clean";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) { }

}

/// <summary><c>demo-mismatch</c>: a differential target whose two sides disagree when the input contains <c>4D</c> ("M").</summary>
public sealed class DemoMismatchTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-mismatch";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) {
    if (input.IndexOf((byte)'M') >= 0)
      throw new FuzzMismatchException("left 1, right 2");
  }

}

/// <summary><c>demo-always</c>: throws on every input, the same way.</summary>
public sealed class DemoAlwaysTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-always";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) => throw new NotSupportedException("always");

}

/// <summary><c>demo-failfast</c>: ends its process when the first byte is <c>46</c> ("F").</summary>
public sealed class DemoFailFastTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-failfast";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) {
    if (input.Length > 0 && input[0] == (byte)'F')
      Environment.FailFast("demo-failfast");
  }

}

/// <summary>
/// <c>demo-dying</c>: when the first byte is <c>68</c> ("D"), writes to standard error, waits a second
/// and ends its process with exit code 3, as a runtime that announced a fail-fast and is held by an
/// error report.
/// </summary>
public sealed class DemoDyingTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-dying";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) {
    if (input.Length == 0 || input[0] != (byte)'D')
      return;

    Console.Error.WriteLine("demo-dying");
    Thread.Sleep(1000);
    Environment.Exit(3);
  }

}

/// <summary><c>demo-sleep</c>: sleeps ten milliseconds for each unit of the first byte.</summary>
public sealed class DemoSleepTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "demo-sleep";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) {
    if (input.Length > 0)
      Thread.Sleep(input[0] * 10);
  }

}
