using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Tests.Helpers.Support;
using Polypiler.Tests.Precomputed;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The integer operations with their contract on the node (#118): the scenarios of
/// <c>tests/features/ir/ir-int.feature</c>, every opcode and contract checked exhaustively at 8 bits
/// against a reference written from the SMT-LIB bit-vector definitions, the boundaries of every
/// width, the verifier's rules, the binary form, and the lowering every backend gets, which must
/// compute what the node computes.
/// </summary>
[TestFixture]
public sealed class IntegerContractTests {

  private static readonly IrType[] _types = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  // ---------------------------------------------------------------- building and running

  private static IrIntegerOp Node(IrIntegerOpcode op, IrType type, IrIntegerContract contract, params IReadOnlyList<IrExpr> operands) => new(op, type, operands, contract);

  /// <summary>A module whose function <c>F</c> returns the operation on its parameters.</summary>
  private static IrModule Module(IrIntegerOpcode op, IrType type, IrIntegerContract contract) {
    var parameters = Enumerable.Range(0, op == IrIntegerOpcode.Neg ? 1 : 2).Select(index => new IrLocal("p" + index, type)).ToList();
    var node = Node(op, type, contract, [.. parameters.Select(parameter => (IrExpr)new IrLocalRef(parameter))]);
    return new IrModule("M", [new IrFunction("F", true, type, parameters, [], new IrBlock([new IrReturn(node)]))]);
  }

  /// <summary>The outcome of the interpreter: the value, carried as the IR carries it, or the trap.</summary>
  private static string Run(IrModule module, IrType type, params long[] operands) {
    try {
      var value = IrInterpreter.Call(module, "F", [.. operands.Select(operand => HelperHarness.Host(type, operand))]);
      return Carried(type, value!).ToString(System.Globalization.CultureInfo.InvariantCulture);
    } catch (IrTrapException trap) {
      return "trap " + trap.Trap;
    } catch (IrUndefinedBehaviourException) {
      return "undefined";
    } catch (IrException exception) {
      return "throws " + exception.Class.Name;
    }
  }

  private static long Carried(IrType type, object value) => value switch {
    sbyte v => v,
    byte v => v,
    short v => v,
    ushort v => v,
    int v => v,
    uint v => v,
    long v => v,
    ulong v => unchecked((long)v),
    _ => throw new ArgumentException("no integer", nameof(value))
  };

  /// <summary>The outcome of evaluating a node directly.</summary>
  private static string Evaluated(IrIntegerOp node, params long[] operands) {
    try {
      return IrIntegerContracts.Evaluate(node, operands).ToString(System.Globalization.CultureInfo.InvariantCulture);
    } catch (IrTrapException trap) {
      return "trap " + trap.Trap;
    } catch (IrUndefinedBehaviourException) {
      return "undefined";
    }
  }

  // ---------------------------------------------------------------- the reference

  /// <summary>
  /// The outcome the definitions give, computed on exact integers: SMT-LIB's <c>bvadd</c>, <c>bvmul</c>,
  /// <c>bvudiv</c>, <c>bvsdiv</c>, <c>bvsrem</c>, <c>bvshl</c>, <c>bvlshr</c> and <c>bvashr</c> where the
  /// contract leaves the result defined, the contract where it does not.
  /// </summary>
  private static string Reference(IrIntegerOpcode op, IrType type, IrIntegerContract contract, long a, long b) {
    var width = type.Bits;
    var modulus = BigInteger.One << width;
    BigInteger Bits(long value) => ((value % modulus) + modulus) % modulus;
    BigInteger Signed(long value) => Bits(value) >= modulus / 2 ? Bits(value) - modulus : Bits(value);
    BigInteger Mathematical(long value) => type.IsSigned ? Signed(value) : Bits(value);
    var minimum = type.IsSigned ? -(modulus / 2) : BigInteger.Zero;
    var maximum = type.IsSigned ? modulus / 2 - 1 : modulus - 1;
    string Of(BigInteger number) {
      var bits = ((number % modulus) + modulus) % modulus;
      var value = type.IsSigned && bits >= modulus / 2 ? bits - modulus : bits;
      return (type.Kind == IrTypeKind.U64 ? unchecked((long)(ulong)value) : (long)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    switch (op) {
      case IrIntegerOpcode.Add or IrIntegerOpcode.Sub or IrIntegerOpcode.Mul or IrIntegerOpcode.Neg: {
        var exact = op switch {
          IrIntegerOpcode.Add => Mathematical(a) + Mathematical(b),
          IrIntegerOpcode.Sub => Mathematical(a) - Mathematical(b),
          IrIntegerOpcode.Mul => Mathematical(a) * Mathematical(b),
          _ => -Mathematical(a)
        };
        if (exact >= minimum && exact <= maximum)
          return Of(exact);

        return contract.Overflow!.Kind switch {
          IrOverflowKind.Wrap => Of(exact),
          IrOverflowKind.Saturate => Of(exact < minimum ? minimum : maximum),
          IrOverflowKind.Undefined => "undefined",
          _ => "trap " + contract.Overflow.Label
        };
      }
      case IrIntegerOpcode.UDiv or IrIntegerOpcode.URem or IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem: {
        var signed = op is IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem;
        var (x, y) = signed ? (Signed(a), Signed(b)) : (Bits(a), Bits(b));
        var quotient = op is IrIntegerOpcode.UDiv or IrIntegerOpcode.SDiv;
        if (y.IsZero)
          return contract.Divisor!.Kind switch {
            IrDivisorKind.Zero => "0",
            IrDivisorKind.Undefined => "undefined",
            _ => "trap " + contract.Divisor.Label
          };

        if (signed && x == -(modulus / 2) && y == -1)
          return contract.Overflow!.Kind switch {
            IrOverflowKind.Wrap => Of(quotient ? x : 0),
            IrOverflowKind.Undefined => "undefined",
            _ => "trap " + contract.Overflow.Label
          };

        return Of(quotient ? BigInteger.Divide(x, y) : BigInteger.Remainder(x, y));
      }
      default: {
        var amount = Bits(b);
        if (amount >= width) {
          var shift = contract.Shift!;
          switch (shift.Kind) {
            case IrShiftKind.Mask:
              amount %= width;
              break;
            case IrShiftKind.Unbounded:
              return Of(op == IrIntegerOpcode.AShr && Signed(a) < 0 ? -1 : 0);
            case IrShiftKind.Trap:
              return "trap " + shift.Label;
            case IrShiftKind.Undefined:
              return "undefined";
            default:
              return "trap InvalidOperation";
          }
        }

        var count = (int)amount;
        return op switch {
          IrIntegerOpcode.Shl => Of(Bits(a) << count),
          IrIntegerOpcode.LShr => Of(Bits(a) >> count),
          _ => Of(Signed(a) >> count)
        };
      }
    }
  }

  // ---------------------------------------------------------------- the cases

  private static IEnumerable<IrIntegerContract> Contracts(IrIntegerOpcode op) => op switch {
    IrIntegerOpcode.Add or IrIntegerOpcode.Sub or IrIntegerOpcode.Mul or IrIntegerOpcode.Neg => [IrOverflowContract.Wrap, IrOverflowContract.Trap(), IrOverflowContract.Saturate, IrOverflowContract.Undefined],
    IrIntegerOpcode.UDiv or IrIntegerOpcode.URem => [IrDivisorContract.Trap(), IrDivisorContract.Zero, IrDivisorContract.Undefined],
    IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem => [
      new(IrOverflowContract.Wrap, IrDivisorContract.Trap()), new(IrOverflowContract.Trap(), IrDivisorContract.Trap()),
      new(IrOverflowContract.Wrap, IrDivisorContract.Zero), new(IrOverflowContract.Trap(), IrDivisorContract.Zero),
      new(IrOverflowContract.Undefined, IrDivisorContract.Undefined)
    ],
    _ => [IrShiftContract.Mask, IrShiftContract.Unbounded, IrShiftContract.Trap(), IrShiftContract.Strict, IrShiftContract.Undefined]
  };

  private static IEnumerable<(IrIntegerOpcode Op, IrType Type, IrIntegerContract Contract)> Combinations(IEnumerable<IrType> types)
    => from op in Enum.GetValues<IrIntegerOpcode>() from type in types from contract in Contracts(op) select (op, type, contract);

  private static IEnumerable<TestCaseData> EightBit() => Combinations([IrType.I8, IrType.U8])
    .Select(entry => new TestCaseData(entry.Op, entry.Type, entry.Contract).SetArgDisplayNames($"{entry.Op}.{entry.Type}{entry.Contract}"));

  private static IEnumerable<TestCaseData> Every() => Combinations(_types)
    .Select(entry => new TestCaseData(entry.Op, entry.Type, entry.Contract).SetArgDisplayNames($"{entry.Op}.{entry.Type}{entry.Contract}"));

  /// <summary>The boundaries of a type, and the shift amounts around its width, as carried values.</summary>
  private static long[] Edges(IrType type) {
    var (minimum, maximum) = (IrOperators.Minimum(type), IrOperators.Maximum(type));
    long Carry(Int128 number) => type.Kind == IrTypeKind.U64 ? unchecked((long)(ulong)number) : (long)number;
    var width = type.Bits;
    return [.. new[] { minimum, minimum + 1, -1, 0, 1, 2, 3, 7, width - 1, width, width + 1, maximum - 1, maximum }
      .Where(number => number >= minimum && number <= maximum).Select(Carry).Distinct()];
  }

  // ---------------------------------------------------------------- the scenarios

  [TestCase(IrIntegerOpcode.Add, "u8", "wrap", 200, 100, "44")]
  [TestCase(IrIntegerOpcode.Add, "i8", "trap", 100, 100, "trap IntegerOverflow")]
  [TestCase(IrIntegerOpcode.Add, "i8", "sat", 100, 100, "127")]
  [TestCase(IrIntegerOpcode.Sub, "u8", "sat", 5, 10, "0")]
  [TestCase(IrIntegerOpcode.Mul, "u16", "wrap", 300, 300, "24464")]
  [TestCase(IrIntegerOpcode.Mul, "i32", "trap", 65536, 32768, "trap IntegerOverflow")]
  [TestCase(IrIntegerOpcode.Add, "u32", "wrap", 0xFFFFFFFF, 1, "0")]
  public void Arithmetic_FollowsItsOverflowContract(IrIntegerOpcode op, string type, string contract, long a, long b, string expected) {
    var overflow = contract switch { "wrap" => IrOverflowContract.Wrap, "sat" => IrOverflowContract.Saturate, _ => IrOverflowContract.Trap() };
    var irType = _types.Single(candidate => candidate.ToString() == type);
    Assert.That(Run(Module(op, irType, overflow), irType, a, b), Is.EqualTo(expected));
  }

  [Test]
  public void Neg_OfAnUnsignedValue_SaturatesToZeroAndWrapsTheSmallestSignedValue() {
    Assert.That(Run(Module(IrIntegerOpcode.Neg, IrType.U8, IrOverflowContract.Saturate), IrType.U8, 5), Is.EqualTo("0"));
    Assert.That(Run(Module(IrIntegerOpcode.Neg, IrType.U8, IrOverflowContract.Saturate), IrType.U8, 0), Is.EqualTo("0"));
    Assert.That(Run(Module(IrIntegerOpcode.Neg, IrType.I8, IrOverflowContract.Wrap), IrType.I8, -128), Is.EqualTo("-128"));
    Assert.That(Run(Module(IrIntegerOpcode.Neg, IrType.I8, IrOverflowContract.Trap()), IrType.I8, -128), Is.EqualTo("trap IntegerOverflow"));
  }

  private static IEnumerable<TestCaseData> Divisions() => [
    new TestCaseData(IrIntegerOpcode.SDiv, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Trap()), -7L, 2L, "-3"),
    new TestCaseData(IrIntegerOpcode.SRem, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Trap()), -7L, 2L, "-1"),
    new TestCaseData(IrIntegerOpcode.UDiv, (IrIntegerContract)IrDivisorContract.Trap(), 7L, 0L, "trap DivideByZero"),
    new TestCaseData(IrIntegerOpcode.UDiv, (IrIntegerContract)IrDivisorContract.Zero, 7L, 0L, "0"),
    new TestCaseData(IrIntegerOpcode.URem, (IrIntegerContract)IrDivisorContract.Zero, 7L, 0L, "0"),
    new TestCaseData(IrIntegerOpcode.SRem, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Zero), 7L, 0L, "0"),
    new TestCaseData(IrIntegerOpcode.SDiv, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Trap()), -2147483648L, -1L, "-2147483648"),
    new TestCaseData(IrIntegerOpcode.SDiv, new IrIntegerContract(IrOverflowContract.Trap(), IrDivisorContract.Trap()), -2147483648L, -1L, "trap IntegerOverflow"),
    new TestCaseData(IrIntegerOpcode.SRem, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Trap()), -2147483648L, -1L, "0"),
    new TestCaseData(IrIntegerOpcode.SRem, new IrIntegerContract(IrOverflowContract.Trap(), IrDivisorContract.Trap()), -2147483648L, -1L, "trap IntegerOverflow"),
    new TestCaseData(IrIntegerOpcode.SDiv, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Trap()), 0L, 5L, "0")
  ];

  [TestCaseSource(nameof(Divisions))]
  public void Division_TruncatesAndGuardsItsEdgeCases(IrIntegerOpcode op, IrIntegerContract contract, long a, long b, string expected) {
    var type = op is IrIntegerOpcode.UDiv or IrIntegerOpcode.URem ? IrType.U32 : IrType.I32;
    Assert.That(Run(Module(op, type, contract), type, a, b), Is.EqualTo(expected));
  }

  private static IEnumerable<TestCaseData> Shifts() => [
    new TestCaseData(IrIntegerOpcode.Shl, IrType.U32, IrShiftContract.Mask, 1L, 33L, "2"),
    new TestCaseData(IrIntegerOpcode.Shl, IrType.I32, IrShiftContract.Mask, 1L, -1L, "-2147483648"),
    new TestCaseData(IrIntegerOpcode.Shl, IrType.U32, IrShiftContract.Unbounded, 1L, 32L, "0"),
    new TestCaseData(IrIntegerOpcode.LShr, IrType.U32, IrShiftContract.Unbounded, 8L, 33L, "0"),
    new TestCaseData(IrIntegerOpcode.AShr, IrType.I32, IrShiftContract.Unbounded, -8L, 33L, "-1"),
    new TestCaseData(IrIntegerOpcode.Shl, IrType.I32, IrShiftContract.Unbounded, 1L, -1L, "0"),
    new TestCaseData(IrIntegerOpcode.Shl, IrType.U32, IrShiftContract.Trap(), 1L, 31L, "2147483648"),
    new TestCaseData(IrIntegerOpcode.Shl, IrType.U32, IrShiftContract.Trap(), 1L, 32L, "trap IntegerOverflow"),
    new TestCaseData(IrIntegerOpcode.Shl, IrType.U32, IrShiftContract.Strict, 1L, 31L, "2147483648"),
    new TestCaseData(IrIntegerOpcode.Shl, IrType.U32, IrShiftContract.Strict, 1L, 32L, "trap InvalidOperation"),
    new TestCaseData(IrIntegerOpcode.AShr, IrType.I32, IrShiftContract.Mask, -8L, 1L, "-4"),
    new TestCaseData(IrIntegerOpcode.LShr, IrType.I32, IrShiftContract.Mask, -8L, 1L, "2147483644"),
    new TestCaseData(IrIntegerOpcode.AShr, IrType.U8, IrShiftContract.Mask, 128L, 7L, "255")
  ];

  [TestCaseSource(nameof(Shifts))]
  public void Shift_HonoursItsAmountContract(IrIntegerOpcode op, IrType type, IrShiftContract contract, long x, long n, string expected)
    => Assert.That(Run(Module(op, type, contract), type, x, n), Is.EqualTo(expected));

  // ---------------------------------------------------------------- the tables

  /// <summary>Test designs 2 and 3: every opcode and contract, on <c>u8</c> and <c>i8</c>, for all 65536 operand pairs.</summary>
  [TestCaseSource(nameof(EightBit))]
  public void EveryPairAtEightBits_IsWhatTheDefinitionsGive(IrIntegerOpcode op, IrType type, IrIntegerContract contract) {
    var node = Node(op, type, contract, op == IrIntegerOpcode.Neg ? [new IrConst(type, 0)] : [new IrConst(type, 0), new IrConst(type, 0)]);
    var mismatches = new List<string>();
    var (minimum, maximum) = ((long)IrOperators.Minimum(type), (long)IrOperators.Maximum(type));
    for (var a = minimum; a <= maximum; ++a)
      for (var b = minimum; b <= maximum; ++b) {
        var expected = Reference(op, type, contract, a, b);
        var actual = op == IrIntegerOpcode.Neg ? Evaluated(node, a) : Evaluated(node, a, b);
        if (actual != expected && mismatches.Count < 5)
          mismatches.Add($"{a}, {b}: {actual}, expected {expected}");
      }

    Assert.That(mismatches, Is.Empty);
  }

  /// <summary>
  /// Test designs 6 and 7: on every width the boundaries of the operands and the shift amounts around the
  /// width give what the definitions give, in the interpreter and after the lowering every backend gets.
  /// </summary>
  [TestCaseSource(nameof(Every))]
  public void TheBoundariesOfEveryWidth_AreWhatTheDefinitionsGive_AlsoLowered(IrIntegerOpcode op, IrType type, IrIntegerContract contract) {
    var module = Module(op, type, contract);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var lowered = HelperHarness.Legalized(module);
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    Assert.That(lowered.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrIntegerOp>(), Is.Empty, "no backend sees the node");
    var edges = Edges(type);
    var mismatches = new List<string>();
    foreach (var a in edges)
      foreach (var b in op == IrIntegerOpcode.Neg ? [0L] : edges) {
        var expected = Reference(op, type, contract, a, b);
        var operands = op == IrIntegerOpcode.Neg ? new[] { a } : [a, b];
        var (direct, legal) = (Run(module, type, operands), Run(lowered, type, operands));
        // Nothing may rely on the value the lowered code gives where the contract is [undefined].
        if ((direct != expected || (expected != "undefined" && Lowered(legal) != Lowered(expected))) && mismatches.Count < 5)
          mismatches.Add($"{a}, {b}: {direct} and lowered {legal}, expected {expected}");
      }

    Assert.That(mismatches, Is.Empty);
  }

  /// <summary>An outcome as the exception class a trap throws: a helper throws the class, an operator raises the trap.</summary>
  private static string Lowered(string outcome) => outcome switch {
    "trap IntegerOverflow" => "throws OverflowException",
    "trap DivideByZero" => "throws DivideByZeroException",
    "trap InvalidOperation" => "throws ArithmeticException",
    _ => outcome
  };

  // ---------------------------------------------------------------- the verifier

  private static IReadOnlyList<Diagnostic> Verify(IrExpr node) {
    var x = new IrLocal("x", node.Type.IsInteger ? node.Type : IrType.I32);
    return IrVerifier.Verify(new IrModule("M", [new IrFunction("F", true, node.Type, [x], [], new IrBlock([new IrReturn(node)]))]));
  }

  private static IrExpr X(IrType type) => new IrLocalRef(new IrLocal("x", type));

  [Test]
  public void AMissingContract_IsPLY0127() {
    var u32 = IrType.U32;
    var cases = new (IrIntegerOp Node, string Wording)[] {
      (Node(IrIntegerOpcode.Add, u32, new IrIntegerContract(), X(u32), X(u32)), "'Add.u32' requires an overflow contract"),
      (Node(IrIntegerOpcode.Shl, u32, new IrIntegerContract(), X(u32), X(u32)), "'Shl.u32' requires a shift-amount contract"),
      (Node(IrIntegerOpcode.UDiv, u32, new IrIntegerContract(), X(u32), X(u32)), "'UDiv.u32' requires a zero-divisor contract"),
      (Node(IrIntegerOpcode.SDiv, IrType.I32, IrDivisorContract.Trap(), X(IrType.I32), X(IrType.I32)), "'SDiv.i32' requires an ovf contract"),
      (Node(IrIntegerOpcode.SRem, IrType.I32, IrDivisorContract.Trap(), X(IrType.I32), X(IrType.I32)), "'SRem.i32' requires an ovf contract"),
      (Node(IrIntegerOpcode.SDiv, IrType.I32, new IrIntegerContract(IrOverflowContract.Saturate, IrDivisorContract.Trap()), X(IrType.I32), X(IrType.I32)), "not ovf=sat")
    };
    foreach (var (node, wording) in cases)
      Assert.That(Verify(node), Has.Some.Matches<Diagnostic>(diagnostic => diagnostic.Code == "PLY0127" && diagnostic.Message.Contains(wording, StringComparison.Ordinal)), wording);
  }

  [TestCase(31L, false)]
  [TestCase(32L, true)]
  [TestCase(33L, true)]
  [TestCase(-1L, true)]
  public void AConstantStrictAmount_NotBelowTheWidth_IsPLY0126(long amount, bool reported) {
    var node = Node(IrIntegerOpcode.Shl, IrType.I32, IrShiftContract.Strict, X(IrType.I32), new IrConst(IrType.I32, amount));
    var diagnostics = Verify(node);
    Assert.That(diagnostics.Any(diagnostic => diagnostic.Code == "PLY0126"), Is.EqualTo(reported));
    Assert.That(diagnostics.Where(diagnostic => diagnostic.Code != "PLY0126"), Is.Empty);
    if (reported)
      Assert.That(diagnostics[0].Message, Does.Contain($"shift amount {unchecked((uint)amount)} is not below the width 32 of a [strict] shift"));
  }

  [Test]
  public void AVariableStrictAmountAndOtherShiftContracts_AreNoError() {
    Assert.That(Verify(Node(IrIntegerOpcode.Shl, IrType.I32, IrShiftContract.Strict, X(IrType.I32), X(IrType.I32))), Is.Empty);
    Assert.That(Verify(Node(IrIntegerOpcode.Shl, IrType.I32, IrShiftContract.Mask, X(IrType.I32), new IrConst(IrType.I32, 40))), Is.Empty);
  }

  [Test]
  public void AMalformedNode_IsPLY0100() {
    var i32 = IrType.I32;
    var cases = new (IrIntegerOp Node, string Wording)[] {
      (Node(IrIntegerOpcode.Add, i32, IrOverflowContract.Wrap, X(i32)), "Add takes 2 operands, not 1"),
      (Node(IrIntegerOpcode.Neg, i32, IrOverflowContract.Wrap, X(i32), X(i32)), "Neg takes 1 operands, not 2"),
      (Node(IrIntegerOpcode.Add, i32, IrOverflowContract.Wrap, X(i32), new IrConst(IrType.I64, 1)), "an operand of Add.i32 has the type i64"),
      (Node(IrIntegerOpcode.Add, IrType.F64, IrOverflowContract.Wrap, new IrConst(IrType.F64, 0), new IrConst(IrType.F64, 0)), "no integer type"),
      (Node(IrIntegerOpcode.Add, i32, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Zero), X(i32), X(i32)), "Add takes no zero-divisor contract"),
      (Node(IrIntegerOpcode.Add, i32, new IrIntegerContract(IrOverflowContract.Wrap, Shift: IrShiftContract.Mask), X(i32), X(i32)), "Add takes no shift-amount contract"),
      (Node(IrIntegerOpcode.UDiv, i32, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Zero), X(i32), X(i32)), "UDiv takes no ovf contract"),
      (Node(IrIntegerOpcode.SDiv, i32, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Zero, IrShiftContract.Mask), X(i32), X(i32)), "SDiv takes no shift-amount contract"),
      (Node(IrIntegerOpcode.Shl, i32, new IrIntegerContract(IrOverflowContract.Wrap, Shift: IrShiftContract.Mask), X(i32), X(i32)), "Shl takes no overflow contract"),
      (Node(IrIntegerOpcode.Shl, i32, new IrIntegerContract(null, IrDivisorContract.Zero, IrShiftContract.Mask), X(i32), X(i32)), "Shl takes no zero-divisor contract")
    };
    foreach (var (node, wording) in cases)
      Assert.That(Verify(node), Has.Some.Matches<Diagnostic>(diagnostic => diagnostic.Code == "PLY0100" && diagnostic.Message.Contains(wording, StringComparison.Ordinal)), wording);
  }

  [Test]
  public void AnEnumType_IsNoIntegerOfTheOperations() {
    var named = new IrEnumType("E", IrType.I32, []);
    Assert.That(IrIntegerContracts.IsInteger(IrType.Enum(named)), Is.False);
    Assert.That(IrIntegerContracts.IsInteger(IrType.I8), Is.True);
    Assert.That(IrIntegerContracts.IsInteger(IrType.Bool), Is.False);
  }

  [Test]
  public void TheEntryPoints_RefuseNull() {
    Assert.That(() => IrIntegerContracts.Problems(null!), Throws.ArgumentNullException);
    Assert.That(() => IrIntegerContracts.Evaluate(null!, []), Throws.ArgumentNullException);
    Assert.That(() => IrIntegerContracts.Evaluate(Node(IrIntegerOpcode.Neg, IrType.I32, IrOverflowContract.Wrap, X(IrType.I32)), null!), Throws.ArgumentNullException);
    Assert.That(() => IntegerContractLegalization.Rewrite(X(IrType.I32), null!), Throws.ArgumentNullException);
  }

  // ---------------------------------------------------------------- the text form

  [Test]
  public void TheTextForm_SpellsOutEveryContract() {
    var (i32, u32) = (IrType.I32, IrType.U32);
    Assert.That(Node(IrIntegerOpcode.Add, u32, IrOverflowContract.Wrap).Text, Is.EqualTo("Add.u32[wrap]"));
    Assert.That(Node(IrIntegerOpcode.Add, IrType.I8, IrOverflowContract.Saturate).Text, Is.EqualTo("Add.i8[sat]"));
    Assert.That(Node(IrIntegerOpcode.Mul, i32, IrOverflowContract.Trap()).Text, Is.EqualTo("Mul.i32[trap IntegerOverflow]"));
    Assert.That(Node(IrIntegerOpcode.UDiv, u32, IrDivisorContract.Trap()).Text, Is.EqualTo("UDiv.u32[trap DivideByZero]"));
    Assert.That(Node(IrIntegerOpcode.SDiv, i32, new IrIntegerContract(IrOverflowContract.Trap(), IrDivisorContract.Trap())).Text,
      Is.EqualTo("SDiv.i32[trap DivideByZero, ovf=trap IntegerOverflow]"));
    Assert.That(Node(IrIntegerOpcode.SRem, i32, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Zero)).Text, Is.EqualTo("SRem.i32[divzero=zero, ovf=wrap]"));
    Assert.That(Node(IrIntegerOpcode.URem, u32, IrDivisorContract.Zero).Text, Is.EqualTo("URem.u32[divzero=zero]"));
    Assert.That(Node(IrIntegerOpcode.Shl, u32, IrShiftContract.Mask).Text, Is.EqualTo("Shl.u32[mask]"));
    Assert.That(Node(IrIntegerOpcode.LShr, u32, IrShiftContract.Unbounded).Text, Is.EqualTo("LShr.u32[unbounded]"));
    Assert.That(Node(IrIntegerOpcode.AShr, i32, IrShiftContract.Strict).Text, Is.EqualTo("AShr.i32[strict]"));
    Assert.That(Node(IrIntegerOpcode.Shl, u32, IrShiftContract.Trap(IrTrap.InvalidOperation)).Text, Is.EqualTo("Shl.u32[trap InvalidOperation]"));
    Assert.That(new IrIntegerContract().ToString(), Is.EqualTo("[]"));
  }

  [Test]
  public void AContract_IsAValue() {
    var label = IrTrap.IndexOutOfRange;
    Assert.That(IrOverflowContract.Trap(label), Is.EqualTo(IrOverflowContract.Trap(IrTrap.IndexOutOfRange)));
    Assert.That(IrOverflowContract.Trap(), Is.Not.EqualTo(IrOverflowContract.Wrap));
    Assert.That(IrOverflowContract.Wrap.Label, Is.EqualTo(IrTrap.IntegerOverflow));
    Assert.That(IrDivisorContract.Zero.Label, Is.EqualTo(IrTrap.DivideByZero));
    Assert.That(IrShiftContract.Mask.Label, Is.EqualTo(IrTrap.InvalidOperation));
    Assert.That(IrShiftContract.Trap().Label, Is.EqualTo(IrTrap.IntegerOverflow));
  }

  // ---------------------------------------------------------------- the tree and the binary form

  [Test]
  public void TheTree_ListsAndReplacesTheOperands() {
    var node = Node(IrIntegerOpcode.Sub, IrType.I32, IrOverflowContract.Wrap, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2));
    Assert.That(IrTree.Operands(node), Is.EqualTo(node.Operands));
    var replaced = (IrIntegerOp)IrTree.With(node, [new IrConst(IrType.I32, 5), new IrConst(IrType.I32, 6)]);
    Assert.That(replaced.Operands[1], Is.EqualTo(new IrConst(IrType.I32, 6)));
    Assert.That(replaced.Contract, Is.SameAs(node.Contract));
  }

  [Test]
  public void EveryContract_RoundTripsThroughTheBinaryForm() {
    var statements = Combinations(_types).Select(entry => (IrStmt)new IrExprStmt(Node(entry.Op, entry.Type, entry.Contract,
      entry.Op == IrIntegerOpcode.Neg ? [new IrConst(entry.Type, 1)] : [new IrConst(entry.Type, 1), new IrConst(entry.Type, 2)])))
      .Append(new IrExprStmt(Node(IrIntegerOpcode.Add, IrType.I32, IrOverflowContract.Trap(IrTrap.IndexOutOfRange), new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2))))
      .Append(new IrReturn(null)).ToList();
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock(statements))]);
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    var nodes = read.Functions[0].Body.Statements.OfType<IrExprStmt>().Select(statement => (IrIntegerOp)statement.Expression).ToList();
    var originals = statements.OfType<IrExprStmt>().Select(statement => (IrIntegerOp)statement.Expression).ToList();
    Assert.That(nodes.Select(node => node.Text), Is.EqualTo(originals.Select(node => node.Text)));
    Assert.That(nodes.Select(node => node.Contract), Is.EqualTo(originals.Select(node => node.Contract)));
  }

  private static byte[] Bytes(params int[] parts) => IrModuleFormatTests.Expression(writer => {
    writer.Write(IrModuleFormatTests.IntegerOp);
    writer.Write(IrModuleFormatTests.I32);
    foreach (var part in parts)
      writer.Write7BitEncodedInt(part);
  });

  [Test]
  public void MalformedBytes_AreRefused() {
    Assert.That(() => IrModuleFormat.Read(Bytes(99)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("99 is no IrIntegerOpcode"));
    Assert.That(() => IrModuleFormat.Read(Bytes(0, 9)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("8 is no IrOverflowKind"));
    Assert.That(() => IrModuleFormat.Read(Bytes(0, 1, 99)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("99 is no IrTrap"));
  }

  // ---------------------------------------------------------------- the lowering

  [Test]
  public void ATrapLabelNoOperatorOrHelperRaises_StaysForTheBackendToRefuse() {
    var (i32, i8) = (IrType.I32, IrType.I8);
    var label = IrTrap.IndexOutOfRange;
    IrIntegerOp[] nodes = [
      Node(IrIntegerOpcode.Add, i32, IrOverflowContract.Trap(label), X(i32), X(i32)),
      Node(IrIntegerOpcode.UDiv, IrType.U32, IrDivisorContract.Trap(label), X(IrType.U32), X(IrType.U32)),
      Node(IrIntegerOpcode.SDiv, i32, new IrIntegerContract(IrOverflowContract.Trap(label), IrDivisorContract.Trap()), X(i32), X(i32)),
      Node(IrIntegerOpcode.UDiv, i8, IrDivisorContract.Trap(label), X(i8), X(i8)),
      Node(IrIntegerOpcode.Shl, i32, IrShiftContract.Trap(label), X(i32), X(i32))
    ];
    foreach (var node in nodes) {
      var module = new IrModule("M", [new IrFunction("F", true, node.Type, [new IrLocal("x", node.Type)], [], new IrBlock([new IrReturn(node)]))]);
      Assert.That(HelperHarness.Legalized(module), Is.SameAs(module), node.Text);
    }
  }

  [TestCase(IrTrap.DivideByZero, "DivideByZeroException")]
  [TestCase(IrTrap.InvalidOperation, "ArithmeticException")]
  public void AShiftTrapOfAnotherLabel_RaisesIt(IrTrap label, string thrown) {
    foreach (var type in new[] { IrType.U8, IrType.I64 }) {
      var lowered = HelperHarness.Legalized(Module(IrIntegerOpcode.Shl, type, IrShiftContract.Trap(label)));
      Assert.That(Run(lowered, type, 1, 64), Is.EqualTo("throws " + thrown));
      Assert.That(Run(lowered, type, 1, 3), Is.EqualTo("8"));
    }
  }

  /// <summary>Every backend legalizes through <see cref="LibraryLegalization.Lower"/>, so each writes a module of the nodes without knowing them.</summary>
  [Test]
  public void EveryBackend_WritesTheLoweredNodes() {
    var (i32, u8) = (IrType.I32, IrType.U8);
    var x = new IrLocal("x", i32);
    var y = new IrLocal("y", u8);
    IrExpr Of(IrLocal local) => new IrLocalRef(local);
    var body = new IrBlock([
      new IrReturn(Node(IrIntegerOpcode.Add, i32, IrOverflowContract.Wrap,
        Node(IrIntegerOpcode.SDiv, i32, new IrIntegerContract(IrOverflowContract.Wrap, IrDivisorContract.Zero), Of(x), Of(x)),
        new IrConvert(i32, Node(IrIntegerOpcode.Shl, u8, IrShiftContract.Unbounded, Of(y), Of(y)))))
    ]);
    var module = new IrModule("M", [new IrFunction("F", true, i32, [x, y], [], body)]);
    IrModule Library() => HelperHarness.Library;
    var texts = new[] {
      Polypiler.Core.Backends.CSharp.CSharpEmitter.Emit(Polypiler.Core.Backends.CSharp.CSharpEmitter.Legalize(module, Library, Polypiler.Core.Backends.CSharp.CSharpProfile.Default, FloatMode.Accurate), Polypiler.Core.Backends.CSharp.CSharpProfile.Default)[0].Content,
      Polypiler.Core.Backends.JavaScript.JavaScriptEmitter.Emit(Polypiler.Core.Backends.JavaScript.JavaScriptEmitter.Legalize(module, Library, Polypiler.Core.Backends.JavaScript.JavaScriptProfile.Default, FloatMode.Accurate), Polypiler.Core.Backends.JavaScript.JavaScriptProfile.Default)[0].Content,
      Polypiler.Core.Backends.Python.PythonEmitter.Emit(Polypiler.Core.Backends.Python.PythonEmitter.Legalize(module, Library, Polypiler.Core.Backends.Python.PythonProfile.Default, FloatMode.Accurate), Polypiler.Core.Backends.Python.PythonProfile.Default)[0].Content,
      Polypiler.Core.Backends.Rust.RustEmitter.Emit(Polypiler.Core.Backends.Rust.RustEmitter.Legalize(module, Library, Polypiler.Core.Backends.Rust.RustProfile.Default), Polypiler.Core.Backends.Rust.RustProfile.Default)
        .Single(file => file.Path.EndsWith(".rs", StringComparison.Ordinal) && file.Content.Contains("fn f(", StringComparison.Ordinal)).Content
    };
    // Python and Rust write the helpers' names in snake case.
    var names = texts.Select(text => text.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant()).ToList();
    Assert.That(names, Has.All.Contains("dividecontract32").And.All.Contains("shiftleftbounded32"));
  }

  [Test]
  public void AnotherExpression_IsNoBusinessOfTheLowering() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]))]);
    Assert.That(HelperHarness.Legalized(module), Is.SameAs(module));
  }

}
