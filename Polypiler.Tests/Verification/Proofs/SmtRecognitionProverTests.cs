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

  [TestCase(8)]
  [TestCase(16)]
  [TestCase(32)]
  [TestCase(64)]
  public void UnsignedMagnitude_IsIdentity(int width) {
    var type = width switch { 8 => IrType.U8, 16 => IrType.U16, 32 => IrType.U32, _ => IrType.U64 };
    var x = new IrLocal("x", type);
    var value = new IrLocalRef(x);
    var function = new IrFunction("U", true, type, [x], [], new([new IrReturn(value)]));
    var context = new RuleContext(function, RuleTarget.Neutral);
    var absolute = new IrIntrinsic(IrIntrinsicOp.AbsWrap, type, [value]);
    Assert.That(new SmtRecognitionProver().Prove("O0121", value, absolute, context), Is.EqualTo(new RecognitionProof(true, "smt-bitvector")));
  }

  [TestCase(true)]
  [TestCase(false)]
  public void BooleanConstants_SelectTheActualArm(bool selected) {
    var negated = new IrUnary(IrUnaryOp.Negate, _value);
    var before = new IrConditional(new IrConst(IrType.Bool, selected ? 1 : 0), _value, negated);
    Assert.That(new SmtRecognitionProver().Prove("O0121", before, selected ? _value : negated, _context).Proved, Is.True);
  }

  [TestCase(IrBinaryOp.Less)]
  [TestCase(IrBinaryOp.LessOrEqual)]
  [TestCase(IrBinaryOp.Greater)]
  [TestCase(IrBinaryOp.GreaterOrEqual)]
  public void AnUnsignedZeroComparison_DoesNotProveSignedMagnitude(IrBinaryOp comparison) {
    var condition = new IrBinary(comparison, new IrConvert(IrType.U32, _value), new IrConst(IrType.U32, 0));
    var before = new IrConditional(condition, new IrUnary(IrUnaryOp.Negate, _value), _value);
    var after = new IrIntrinsic(IrIntrinsicOp.AbsWrap, IrType.I32, [_value]);
    Assert.That(new SmtRecognitionProver().Prove("O0121", before, after, _context), Is.EqualTo(new RecognitionProof(false, "not equivalent")));
  }

  [Test]
  public void UnsignedMagnitude_IsNotASignedMagnitudeOnTheSameBits() {
    var value = new IrConvert(IrType.U32, _value);
    var condition = new IrBinary(IrBinaryOp.Less, _value, new IrConst(IrType.I32, 0));
    var signedMagnitude = new IrConditional(condition, new IrBinary(IrBinaryOp.Subtract, new IrConst(IrType.U32, 0), value), value);
    var unsignedMagnitude = new IrIntrinsic(IrIntrinsicOp.AbsWrap, IrType.U32, [value]);
    Assert.That(new SmtRecognitionProver().Prove("O0121", signedMagnitude, unsignedMagnitude, _context), Is.EqualTo(new RecognitionProof(false, "not equivalent")));
  }

  [Test]
  public void AnIndependentBooleanInput_KeepsItsBooleanType() {
    var flag = new IrLocalRef(new IrLocal("flag", IrType.Bool));
    var choice = new IrConditional(flag, _value, _value);
    Assert.That(new SmtRecognitionProver().Prove("O0121", choice, _value, _context).Proved, Is.True);
  }

  private static readonly IrExpr _position = new IrLocalRef(new IrLocal("n", IrType.I32));

  private static IrExpr Masked(int width) => new IrBinary(IrBinaryOp.And, _position, new IrConst(IrType.I32, width - 1));

  [TestCase(IrIntrinsicOp.SetBit, IrBinaryOp.Or)]
  [TestCase(IrIntrinsicOp.ToggleBit, IrBinaryOp.Xor)]
  public void SetAndToggle_AreTheirSingleBitDefinitions(IrIntrinsicOp op, IrBinaryOp combine) {
    var single = new IrBinary(IrBinaryOp.ShiftLeft, new IrConst(IrType.I32, 1), _position);
    var before = new IrBinary(combine, _value, single);
    var after = new IrIntrinsic(op, IrType.I32, [_value, Masked(32)]);
    Assert.That(new SmtRecognitionProver().Prove("O0108", before, after, _context).Proved, Is.True);
    var wrong = new IrBinary(combine, _value, new IrBinary(IrBinaryOp.ShiftLeft, new IrConst(IrType.I32, 2), _position));
    Assert.That(new SmtRecognitionProver().Prove("O0108", wrong, after, _context), Is.EqualTo(new RecognitionProof(false, "not equivalent")));
  }

  [Test]
  public void ClearAndTest_AreTheirSingleBitDefinitions() {
    var single = new IrBinary(IrBinaryOp.ShiftLeft, new IrConst(IrType.I32, 1), _position);
    var cleared = new IrBinary(IrBinaryOp.And, _value, new IrUnary(IrUnaryOp.BitNot, single));
    Assert.That(new SmtRecognitionProver().Prove("O0108", cleared, new IrIntrinsic(IrIntrinsicOp.ClearBit, IrType.I32, [_value, Masked(32)]), _context).Proved, Is.True);
    var tested = new IrBinary(IrBinaryOp.NotEqual, new IrBinary(IrBinaryOp.And, _value, single), new IrConst(IrType.I32, 0));
    var test = new IrIntrinsic(IrIntrinsicOp.TestBit, IrType.Bool, [_value, Masked(32)]);
    var prover = new SmtRecognitionProver();
    Assert.That(prover.Prove("O0108", tested, test, _context).Proved, Is.True);
    Assert.That(prover.Prove("O0108", tested, new IrUnary(IrUnaryOp.LogicalNot, test), _context), Is.EqualTo(new RecognitionProof(false, "not equivalent")));
  }

  [Test]
  public void AnOperationThatMayTrapOnItsPosition_IsNotEncoded() {
    var unmasked = new IrIntrinsic(IrIntrinsicOp.TestBit, IrType.Bool, [_value, _position]);
    Assert.That(new SmtRecognitionProver().Prove("O0108", unmasked, unmasked, _context), Is.EqualTo(new RecognitionProof(false, "unsupported proof expression")));
  }
}
