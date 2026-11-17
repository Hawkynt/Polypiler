using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The overflow contracts beyond wrap, trap and saturate (#122): <c>[undefined]</c> with the interpreter
/// outcome <see cref="IrUndefinedBehaviourException"/>, the wrapped-value-plus-flag forms, the
/// expansion of <c>checked</c>, and the table of profile contracts - the scenarios of
/// <c>tests/features/ir/ir-overflow.feature</c>. Every opcode under <c>[undefined]</c> is also checked
/// exhaustively at 8 bits and on the boundaries of every width by <see cref="IntegerContractTests"/>.
/// </summary>
[TestFixture]
public sealed class OverflowContractTests {

  private static IrModule Module(IrIntegerOpcode op, IrType type, IrIntegerContract contract) {
    var parameters = new[] { new IrLocal("a", type), new IrLocal("b", type) };
    var node = new IrIntegerOp(op, type, [new IrLocalRef(parameters[0]), new IrLocalRef(parameters[1])], contract);
    return new IrModule("M", [new IrFunction("F", true, type, parameters, [], new IrBlock([new IrReturn(node)]))]);
  }

  private static string Outcome(IrModule module, IrType type, long a, long b) {
    try {
      return Convert.ToString(IrInterpreter.Call(module, "F", [HelperHarness.Host(type, a), HelperHarness.Host(type, b)]), System.Globalization.CultureInfo.InvariantCulture)!;
    } catch (IrUndefinedBehaviourException reached) {
      return "UndefinedBehaviourReached " + reached.Node.Text;
    }
  }

  private static IEnumerable<TestCaseData> Undefined() => [
    new TestCaseData(IrIntegerOpcode.Add, (IrIntegerContract)IrOverflowContract.Undefined, 2147483646L, 1L, "2147483647"),
    new TestCaseData(IrIntegerOpcode.Add, (IrIntegerContract)IrOverflowContract.Undefined, 2147483647L, 1L, "UndefinedBehaviourReached Add.i32[undefined]"),
    new TestCaseData(IrIntegerOpcode.Mul, (IrIntegerContract)IrOverflowContract.Undefined, 65536L, 32768L, "UndefinedBehaviourReached Mul.i32[undefined]"),
    new TestCaseData(IrIntegerOpcode.Shl, (IrIntegerContract)IrShiftContract.Undefined, 1L, 31L, "-2147483648"),
    new TestCaseData(IrIntegerOpcode.Shl, (IrIntegerContract)IrShiftContract.Undefined, 1L, 32L, "UndefinedBehaviourReached Shl.i32[undefined]"),
    new TestCaseData(IrIntegerOpcode.SDiv, new IrIntegerContract(IrOverflowContract.Undefined, IrDivisorContract.Undefined), 7L, 0L,
      "UndefinedBehaviourReached SDiv.i32[divzero=undefined, ovf=undefined]"),
    new TestCaseData(IrIntegerOpcode.SDiv, new IrIntegerContract(IrOverflowContract.Undefined, IrDivisorContract.Undefined), -2147483648L, -1L,
      "UndefinedBehaviourReached SDiv.i32[divzero=undefined, ovf=undefined]"),
    new TestCaseData(IrIntegerOpcode.SRem, new IrIntegerContract(IrOverflowContract.Undefined, IrDivisorContract.Undefined), -7L, 2L, "-1")
  ];

  [TestCaseSource(nameof(Undefined))]
  public void AnUndefinedContract_IsTheWrapResultInRangeAndNoResultOutOfRange(IrIntegerOpcode op, IrIntegerContract contract, long a, long b, string expected)
    => Assert.That(Outcome(Module(op, IrType.I32, contract), IrType.I32, a, b), Is.EqualTo(expected));

  [Test]
  public void UndefinedBehaviour_IsNoExceptionOfTheProgram() {
    var node = new IrIntegerOp(IrIntegerOpcode.Add, IrType.I8, [new IrConst(IrType.I8, 127), new IrConst(IrType.I8, 1)], IrOverflowContract.Undefined);
    var reached = new IrUndefinedBehaviourException(node);
    Assert.That(reached, Is.Not.InstanceOf<IrException>());
    Assert.That(reached.Message, Is.EqualTo("undefined behaviour reached at 'Add.i8[undefined]'"));
    Assert.That(reached.Node, Is.SameAs(node));
    Assert.That(() => new IrUndefinedBehaviourException(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void TheUndefinedContracts_PrintAsTheTextFormWritesThem() {
    Assert.That(IrOverflowContract.Undefined.ToString(), Is.EqualTo("undefined"));
    Assert.That(IrDivisorContract.Undefined.ToString(), Is.EqualTo("divzero=undefined"));
    Assert.That(IrShiftContract.Undefined.ToString(), Is.EqualTo("undefined"));
    Assert.That(new IrIntegerContract(Divisor: IrDivisorContract.Undefined).ToString(), Is.EqualTo("[divzero=undefined]"));
  }

  // ---------------------------------------------------------------- the profile

  [Test]
  public void AnUndefinedContract_IsPermittedOnlyWhereTheProfileDeclaresIt() {
    var module = Module(IrIntegerOpcode.Add, IrType.I32, IrOverflowContract.Undefined);
    var diagnostics = ProfileContracts.Check(module, "java/21");
    Assert.That(diagnostics, Has.Count.EqualTo(1));
    Assert.That(diagnostics[0].Code, Is.EqualTo("PLY0127"));
    Assert.That(diagnostics[0].Message, Is.EqualTo("'Add.i32[undefined]' is not permitted: source profile 'java/21' does not declare undefinedOverflow in 'F'"));
    Assert.That(ProfileContracts.Check(module, "c/23"), Is.Empty);
    Assert.That(ProfileContracts.Check(Module(IrIntegerOpcode.Add, IrType.I32, IrOverflowContract.Wrap), "java/21"), Is.Empty);
    Assert.That(ProfileContracts.Check(Module(IrIntegerOpcode.UDiv, IrType.U32, IrDivisorContract.Undefined), "rust/2024"), Has.Count.EqualTo(1));
    Assert.That(ProfileContracts.Check(Module(IrIntegerOpcode.LShr, IrType.U32, IrShiftContract.Undefined), "csharp"), Has.Count.EqualTo(1));
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the verifier accepts the contract; the profile decides");
    Assert.That(() => ProfileContracts.Check(null!, "c/23"), Throws.ArgumentNullException);
    Assert.That(() => ProfileContracts.Check(module, ""), Throws.ArgumentException);
  }

  private static IEnumerable<TestCaseData> Rows() => [
    new TestCaseData("c/23", IrIntegerOpcode.Add, IrType.I32, null, "[undefined]"),
    new TestCaseData("c/23", IrIntegerOpcode.Add, IrType.U32, null, "[wrap]"),
    new TestCaseData("c/23", IrIntegerOpcode.Shl, IrType.U32, null, "[undefined]"),
    new TestCaseData("c/23", IrIntegerOpcode.SDiv, IrType.I32, null, "[divzero=undefined, ovf=undefined]"),
    new TestCaseData("c/23", IrIntegerOpcode.UDiv, IrType.U32, null, "[divzero=undefined]"),
    new TestCaseData("csharp/net10.0", IrIntegerOpcode.Add, IrType.I32, null, "[wrap]"),
    new TestCaseData("csharp/net10.0", IrIntegerOpcode.Add, IrType.I32, ProfileContracts.Unchecked, "[wrap]"),
    new TestCaseData("csharp/net10.0", IrIntegerOpcode.Mul, IrType.I64, ProfileContracts.Checked, "[trap IntegerOverflow]"),
    new TestCaseData("csharp/net10.0", IrIntegerOpcode.Shl, IrType.I32, ProfileContracts.Checked, "[mask]"),
    new TestCaseData("csharp/net10.0", IrIntegerOpcode.SRem, IrType.I32, ProfileContracts.Unchecked, "[trap DivideByZero, ovf=trap IntegerOverflow]"),
    new TestCaseData("rust/2024", IrIntegerOpcode.Add, IrType.U8, null, "[trap IntegerOverflow]"),
    new TestCaseData("rust/2024", IrIntegerOpcode.Sub, IrType.U8, ProfileContracts.Release, "[wrap]"),
    new TestCaseData("rust/2024", IrIntegerOpcode.Shl, IrType.U32, ProfileContracts.Debug, "[trap IntegerOverflow]"),
    new TestCaseData("rust/2024", IrIntegerOpcode.AShr, IrType.I32, ProfileContracts.Release, "[mask]"),
    new TestCaseData("rust/2024", IrIntegerOpcode.SDiv, IrType.I32, ProfileContracts.Release, "[trap DivideByZero, ovf=trap IntegerOverflow]"),
    new TestCaseData("java/21", IrIntegerOpcode.Neg, IrType.I32, null, "[wrap]"),
    new TestCaseData("java/21", IrIntegerOpcode.SDiv, IrType.I32, null, "[trap DivideByZero, ovf=wrap]"),
    new TestCaseData("java/21", IrIntegerOpcode.LShr, IrType.I64, null, "[mask]"),
    new TestCaseData("perl/5.40", IrIntegerOpcode.Add, IrType.I64, null, ProfileContracts.PromoteToFloat),
    new TestCaseData("perl/5.40", IrIntegerOpcode.Mul, IrType.I64, null, ProfileContracts.PromoteToFloat)
  ];

  [TestCaseSource(nameof(Rows))]
  public void EachProfile_LooksItsOperatorUpInOneTable(string profile, IrIntegerOpcode op, IrType type, string? context, string expected)
    => Assert.That(ProfileContracts.For(profile, op, type, context)?.ToString(), Is.EqualTo(expected));

  [Test]
  public void TheTable_HasNoRowWhereNoneIsShipped_AndRefusesAnUnknownContext() {
    Assert.That(ProfileContracts.For("perl/5.40", IrIntegerOpcode.Shl, IrType.I64), Is.Null);
    Assert.That(ProfileContracts.For("perl/5.40", IrIntegerOpcode.Neg, IrType.I64), Is.Null);
    Assert.That(ProfileContracts.For("go/1.23", IrIntegerOpcode.Add, IrType.I64), Is.Null);
    Assert.That(ProfileContracts.For("perl", IrIntegerOpcode.Add, IrType.I64)!.Contract, Is.Null);
    Assert.That(ProfileContracts.For("java", IrIntegerOpcode.Add, IrType.I64)!.Recipe, Is.Null);
    Assert.That(() => ProfileContracts.For("rust/2024", IrIntegerOpcode.Add, IrType.I32, ProfileContracts.Checked),
      Throws.ArgumentException.With.Message.Contains("'checked' is no context of the profile 'rust/2024'; it has debug, release"));
    Assert.That(() => ProfileContracts.For("c/23", IrIntegerOpcode.Add, IrType.I32, ProfileContracts.Debug),
      Throws.ArgumentException.With.Message.Contains("it has none"));
    Assert.That(() => ProfileContracts.For("c/23", IrIntegerOpcode.Add, null!), Throws.ArgumentNullException);
    Assert.That(() => ProfileContracts.For(null!, IrIntegerOpcode.Add, IrType.I32), Throws.ArgumentNullException);
    Assert.That(ProfileContracts.Languages, Is.EquivalentTo(new[] { "c", "csharp", "rust", "java", "perl" }));
    Assert.That(ProfileContracts.DeclaresUndefinedOverflow("c"), Is.True);
    Assert.That(ProfileContracts.DeclaresUndefinedOverflow("csharp/net10.0"), Is.False);
  }

  // ---------------------------------------------------------------- the flag forms

  private static IEnumerable<TestCaseData> Flags() => [
    new TestCaseData(IrIntegerOpcode.Add, IrType.I32, 2147483647L, 1L, -2147483648L, true),
    new TestCaseData(IrIntegerOpcode.Add, IrType.U8, 200L, 100L, 44L, true),
    new TestCaseData(IrIntegerOpcode.Sub, IrType.U8, 5L, 10L, 251L, true),
    new TestCaseData(IrIntegerOpcode.Mul, IrType.I64, 3L, 4L, 12L, false),
    new TestCaseData(IrIntegerOpcode.Mul, IrType.U64, -1L, 2L, -2L, true),
    new TestCaseData(IrIntegerOpcode.Sub, IrType.I16, -32768L, -1L, -32767L, false)
  ];

  /// <summary>The value is the <c>[wrap]</c> result and the flag exactly the <c>[trap]</c> condition, evaluated and as the two expressions interpreted.</summary>
  [TestCaseSource(nameof(Flags))]
  public void TheFlagForms_ReturnTheWrappedValueAndWhetherItOverflowed(IrIntegerOpcode op, IrType type, long a, long b, long value, bool flag) {
    Assert.That(OverflowFlagForms.Evaluate(op, type, a, b), Is.EqualTo((value, flag)));
    var parameters = new[] { new IrLocal("a", type), new IrLocal("b", type) };
    var (wrapped, overflow) = OverflowFlagForms.Of(op, new IrLocalRef(parameters[0]), new IrLocalRef(parameters[1]));
    IrModule Returning(IrExpr expression) => new("M", [new IrFunction("F", true, expression.Type, parameters, [], new IrBlock([new IrReturn(expression)]))]);
    object[] arguments = [HelperHarness.Host(type, a), HelperHarness.Host(type, b)];
    Assert.That(IrInterpreter.Call(Returning(wrapped), "F", arguments), Is.EqualTo(HelperHarness.Host(type, value)));
    Assert.That(IrInterpreter.Call(Returning(overflow), "F", arguments), Is.EqualTo(flag));
    Assert.That(IrVerifier.Verify(Returning(wrapped)).Concat(IrVerifier.Verify(Returning(overflow))), Is.Empty);
  }

  [Test]
  public void AFlagForm_RefusesWhatItCannotBuild() {
    var i32 = new IrConst(IrType.I32, 1);
    Assert.That(() => OverflowFlagForms.Of(IrIntegerOpcode.Neg, i32, i32), Throws.ArgumentException.With.Message.Contains("Neg has no flag form"));
    Assert.That(() => OverflowFlagForms.Of(IrIntegerOpcode.Add, new IrCall("G", IrType.I32, []), i32), Throws.ArgumentException.With.Message.Contains("a constant or a variable"));
    Assert.That(() => OverflowFlagForms.Of(IrIntegerOpcode.Add, i32, new IrCall("G", IrType.I32, [])), Throws.ArgumentException.With.Message.Contains("a constant or a variable"));
    Assert.That(() => OverflowFlagForms.Of(IrIntegerOpcode.Add, i32, new IrConst(IrType.I64, 1)), Throws.ArgumentException.With.Message.Contains("not i32 and i64"));
    Assert.That(() => OverflowFlagForms.Of(IrIntegerOpcode.Add, new IrConst(IrType.F64, 0), new IrConst(IrType.F64, 0)), Throws.ArgumentException);
    Assert.That(() => OverflowFlagForms.Of(IrIntegerOpcode.Add, null!, i32), Throws.ArgumentNullException);
    Assert.That(() => OverflowFlagForms.Of(IrIntegerOpcode.Add, i32, null!), Throws.ArgumentNullException);
    Assert.That(() => OverflowFlagForms.Evaluate(IrIntegerOpcode.UDiv, IrType.I32, 1, 1), Throws.ArgumentException);
    Assert.That(() => OverflowFlagForms.Evaluate(IrIntegerOpcode.Add, null!, 1, 1), Throws.ArgumentNullException);
  }

  // ---------------------------------------------------------------- checked

  [Test]
  public void Checked_ExpandsPerOpcode() {
    Assert.That(IrIntegerContract.Checked(IrIntegerOpcode.Add).ToString(), Is.EqualTo("[trap IntegerOverflow]"));
    Assert.That(IrIntegerContract.Checked(IrIntegerOpcode.Neg).ToString(), Is.EqualTo("[trap IntegerOverflow]"));
    Assert.That(IrIntegerContract.Checked(IrIntegerOpcode.SDiv).ToString(), Is.EqualTo("[trap DivideByZero, ovf=trap IntegerOverflow]"));
    Assert.That(IrIntegerContract.Checked(IrIntegerOpcode.URem).ToString(), Is.EqualTo("[trap DivideByZero]"));
    Assert.That(() => IrIntegerContract.Checked(IrIntegerOpcode.Shl), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

}
