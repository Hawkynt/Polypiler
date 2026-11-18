using System;
using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;
using Polypiler.Core.Verification.Proofs;

namespace Polypiler.Tests.Verification.Proofs;

/// <summary>The tree-IR proof adapter must distinguish identities, false rewrites and inconclusive solvers.</summary>
[TestFixture]
public sealed class SmtRecognitionProverTests {

  private static readonly IrLocal _x = new("x", IrType.I32);
  private static readonly IrExpr _value = new IrLocalRef(_x);
  private static readonly RuleContext _context = new(new IrFunction("F", true, IrType.I32, [_x], [], new IrBlock([new IrReturn(_value)])), RuleTarget.Neutral);

  private sealed class Solver(SmtStatus status, string? reason = null, bool unavailable = false) : ISmtSolver {
    public string Version => "test";
    public int Calls { get; private set; }
    public SmtAnswer Check(string query, IReadOnlyList<string> constants, TimeSpan timeout) {
      ++this.Calls;
      if (unavailable)
        throw new ProverUnavailableException(Diagnostic.Create(Z3Solver.NotInstalled, null, "test solver unavailable"));

      return new(status, new Dictionary<string, ulong>(), reason);
    }
  }

  [Test]
  public void FalsePermutation_IsRefutedByAConfirmedCounterexample() {
    var reverse = new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.I32, [_value]);
    Assert.That(new SmtRecognitionProver().Prove("O0105", _value, reverse, _context), Is.EqualTo(new RecognitionProof(false, "not equivalent")));
  }

  [TestCase("timeout", "proof timeout")]
  [TestCase("incomplete", "proof unknown")]
  public void UnknownSolver_DoesNotAuthorizeOrCacheARewrite(string reason, string expected) {
    var solver = new Solver(SmtStatus.Unknown, reason);
    var prover = new SmtRecognitionProver(solver);
    Assert.That(prover.Prove("O0105", _value, _value, _context), Is.EqualTo(new RecognitionProof(false, expected)));
    Assert.That(prover.Prove("O0105", _value, _value, _context).Proved, Is.False);
    Assert.That(solver.Calls, Is.EqualTo(2));
  }

  [Test]
  public void MissingNativeSolver_IsARefusal() {
    var prover = new SmtRecognitionProver(new Solver(SmtStatus.Unknown, unavailable: true));
    Assert.That(prover.Prove("O0105", _value, _value, _context), Is.EqualTo(new RecognitionProof(false, "proof unavailable")));
  }

  [Test]
  public void OnlyProvedObligations_AreReused() {
    var solver = new Solver(SmtStatus.Unsat);
    var prover = new SmtRecognitionProver(solver);
    Assert.That(prover.Prove("O0105", _value, _value, _context).Proved, Is.True);
    Assert.That(prover.Prove("O0105", _value, _value, _context).Proved, Is.True);
    Assert.That(solver.Calls, Is.EqualTo(1));
    Assert.That(prover.Prove("O0106", _value, _value, _context).Proved, Is.True);
    Assert.That(solver.Calls, Is.EqualTo(2), "the rule is part of the cached obligation");
  }

  [Test]
  public void FloatingPoint_IsNotPresentedAsABitvectorProof() {
    var value = new IrConst(IrType.F64, 0);
    Assert.That(new SmtRecognitionProver().Prove("O0105", value, value, _context), Is.EqualTo(new RecognitionProof(false, "unsupported proof expression")));
  }

  [TestCase(IrBinaryOp.ShiftRight)]
  [TestCase(IrBinaryOp.ShiftRightLogical)]
  public void ShiftSemantics_AreEncodedWithTheActualSignedness(IrBinaryOp operation) {
    var shifted = new IrBinary(operation, _value, new IrConst(IrType.I32, 31));
    var unsigned = new IrBinary(IrBinaryOp.ShiftRightLogical, _value, new IrConst(IrType.I32, 31));
    var proof = new SmtRecognitionProver().Prove("O0105", shifted, unsigned, _context);
    Assert.That(proof.Proved, Is.EqualTo(operation == IrBinaryOp.ShiftRightLogical));
  }
}
