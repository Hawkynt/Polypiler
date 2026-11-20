using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>The verdict on a candidate rewrite; only a proved verdict permits replacement.</summary>
/// <param name="Proved">Whether equivalence was established.</param>
/// <param name="Detail">The accepted proof class, or the reason nothing was proved.</param>
public sealed record RecognitionProof(bool Proved, string Detail);

/// <summary>An equivalence service supplied by the host, without making recognition depend on verification.</summary>
public interface IRecognitionProver {

  /// <summary>Checks the actual candidate and replacement, including definitions of stable locals.</summary>
  /// <param name="id">The rule requesting the proof.</param>
  /// <param name="before">The candidate.</param>
  /// <param name="after">The replacement.</param>
  /// <param name="context">The stable definitions in scope.</param>
  /// <returns>The verdict; unknown, unsupported and timeout are refusals.</returns>
  RecognitionProof Prove(string id, IrExpr before, IrExpr after, RuleContext context);
}
