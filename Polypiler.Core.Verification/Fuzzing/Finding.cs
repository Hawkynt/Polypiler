namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>What went wrong on an input.</summary>
public enum FindingKind {

  /// <summary>An exception escaped the target, or the worker process died.</summary>
  Crash,

  /// <summary>The input did not finish within <see cref="FuzzBudget.PerInput"/>.</summary>
  Hang,

  /// <summary>The input allocated more than <see cref="FuzzBudget.MaxAllocatedBytes"/>.</summary>
  Allocation,

  /// <summary>A differential target threw <see cref="FuzzMismatchException"/>.</summary>
  Mismatch

}

/// <summary>One de-duplicated finding of a fuzz run.</summary>
/// <param name="Target">The ID of the target.</param>
/// <param name="Kind">What went wrong.</param>
/// <param name="Signature">What identifies the finding among others of the target; see <see cref="FuzzOutcome.Signature"/>.</param>
/// <param name="Input">The input as it was executed.</param>
/// <param name="Minimized">The 1-minimal input that still shows the same signature.</param>
/// <param name="Seed">The seed of the run.</param>
/// <param name="Iteration">The first iteration that showed the signature.</param>
/// <param name="CorpusEntry">The corpus entry the input came from, relative to the corpus directory; <see langword="null"/> when it came from elsewhere.</param>
/// <param name="CorpusEntrySha256">The SHA-256 of that entry, lower-case hex; <see langword="null"/> without an entry.</param>
public sealed record Finding(string Target, FindingKind Kind, string Signature, byte[] Input, byte[] Minimized, ulong Seed, long Iteration, string? CorpusEntry, string? CorpusEntrySha256);
