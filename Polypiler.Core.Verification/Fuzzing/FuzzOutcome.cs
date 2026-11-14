using System;
using System.Diagnostics;
using System.Reflection;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>
/// What one execution of a target showed: whether it threw, how much it allocated and how long it
/// took; after <see cref="Judge"/>, also whether it exceeded its budget.
/// </summary>
/// <param name="Kind">The finding; <see langword="null"/> for an input the target handled.</param>
/// <param name="Signature">
/// <c>&lt;kind&gt;:&lt;exception type&gt;@&lt;first stack frame inside the target's assembly&gt;</c> for a
/// crash or a mismatch, <c>process-exit:&lt;code&gt;</c> for a worker that died, <c>hang</c> and
/// <c>allocation</c> otherwise; empty for an input the target handled.
/// </param>
/// <param name="AllocatedBytes">The bytes the execution allocated on its thread.</param>
/// <param name="Elapsed">How long the execution took.</param>
public sealed record FuzzOutcome(FindingKind? Kind, string Signature, long AllocatedBytes, TimeSpan Elapsed) {

  /// <summary>The signature of every <see cref="FindingKind.Hang"/>.</summary>
  public const string HangSignature = "hang";

  /// <summary>The signature of every <see cref="FindingKind.Allocation"/>.</summary>
  public const string AllocationSignature = "allocation";

  /// <summary>Whether the outcome is a finding.</summary>
  public bool IsFinding => this.Kind.HasValue;

  /// <summary>
  /// The outcome measured against a budget: a crash or a mismatch stays what it is; an execution that
  /// took longer than <see cref="FuzzBudget.PerInput"/> is a hang, one that allocated more than
  /// <see cref="FuzzBudget.MaxAllocatedBytes"/> an allocation finding. Exactly the budget is within it.
  /// </summary>
  public FuzzOutcome Judge(FuzzBudget budget) {
    ArgumentNullException.ThrowIfNull(budget);
    if (this.IsFinding)
      return this;
    if (this.Elapsed > budget.PerInput)
      return this with { Kind = FindingKind.Hang, Signature = HangSignature };
    if (this.AllocatedBytes > budget.MaxAllocatedBytes)
      return this with { Kind = FindingKind.Allocation, Signature = AllocationSignature };
    return this;
  }

  /// <summary>
  /// The signature of an exception that escaped a target: <c>mismatch:</c> for a
  /// <see cref="FuzzMismatchException"/>, <c>crash:</c> for anything else, then the exception's type and
  /// the innermost stack frame whose method belongs to <paramref name="targetAssembly"/>; <c>?</c> when
  /// no frame does.
  /// </summary>
  public static FuzzOutcome Thrown(Exception exception, Assembly targetAssembly, long allocatedBytes, TimeSpan elapsed) {
    ArgumentNullException.ThrowIfNull(exception);
    ArgumentNullException.ThrowIfNull(targetAssembly);
    var kind = exception is FuzzMismatchException ? FindingKind.Mismatch : FindingKind.Crash;
    var frame = "?";
    foreach (var candidate in new StackTrace(exception, false).GetFrames()) {
      var type = candidate.GetMethod()?.DeclaringType;
      if (type?.Assembly != targetAssembly)
        continue;

      frame = $"{type.FullName}.{candidate.GetMethod()!.Name}";
      break;
    }

    return new(kind, $"{(kind == FindingKind.Mismatch ? "mismatch" : "crash")}:{exception.GetType().FullName}@{frame}", allocatedBytes, elapsed);
  }

}
