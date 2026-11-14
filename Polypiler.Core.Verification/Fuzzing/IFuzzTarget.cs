using System;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>
/// Code under fuzzing: a parser, a decoder or a command line that is fed arbitrary bytes. A target
/// returns normally for every input it accepts or cleanly refuses — diagnostics are values, not
/// exceptions. Any exception escaping <see cref="Execute"/> is a crash; a differential target signals
/// that two implementations disagree by throwing <see cref="FuzzMismatchException"/>. A public,
/// non-abstract target with a parameterless constructor is found by <see cref="FuzzTargets.Discover"/>.
/// </summary>
public interface IFuzzTarget {

  /// <summary>The ID the target is registered and invoked by; its corpus is <c>tests/fixtures/fuzz/&lt;id&gt;/</c>.</summary>
  string Id { get; }

  /// <summary>Runs the code under test on one input.</summary>
  void Execute(ReadOnlySpan<byte> input);

}

/// <summary>Thrown by a differential target when two implementations disagree on an input.</summary>
public sealed class FuzzMismatchException : Exception {

  /// <summary>Creates the exception with what differed.</summary>
  /// <param name="detail">What the implementations computed.</param>
  public FuzzMismatchException(string detail) : base(detail) { }

}
