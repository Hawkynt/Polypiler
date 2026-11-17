using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;

namespace Polypiler.Tests.Optimize;

/// <summary>
/// O0389 on hand-built IR (#1158): the scenarios of <c>opt-ipa-localglobal.feature</c> and the rows of
/// the test design - a scratch global of one function becomes its variable, and every case where the
/// function could read a value an earlier or an outer activation left keeps the global - each with the
/// module computing the same before and after, the function called twice in a row.
/// </summary>
[TestFixture]
public sealed class O0389GlobalLocalizationTests {

  private static readonly IrGlobal _temp = new("temp", IrType.I32, new IrConst(IrType.I32, 0));
  private static readonly IrLocal _i = new("i", IrType.I32);

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrExpr Temp => new IrGlobalRef(_temp);

  private static IrExpr I => new IrLocalRef(_i);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  private static IrExpr Add(IrExpr left, IrExpr right) => new IrBinary(IrBinaryOp.Add, left, right);

  private static IrCall Call(string function, params IrExpr[] arguments) => new(function, IrType.I32, arguments);

  /// <summary>The loop adding <c>i</c> to <c>@temp</c> for <c>i</c> in 0..99.</summary>
  private static IrBlock Loop() => Block(
    new IrDeclare(_i, Int(0)),
    new IrWhile(new IrBinary(IrBinaryOp.Less, I, Int(100)), Block(new IrGlobalAssign(_temp, Add(Temp, I)), new IrAssign(_i, Add(I, Int(1))))));

  /// <summary><c>Work</c>: stores 0 to <c>@temp</c>, adds 0..99 and returns it.</summary>
  private static IrFunction Work(bool isPublic = false, params IrStmt[] first)
    => new("Work", isPublic, IrType.I32, [], [_i], Block([.. first, new IrGlobalAssign(_temp, Int(0)), Loop(), new IrReturn(Temp)]));

  /// <summary><c>Twice</c>: calls a function twice in a row and returns the first result times 10000 plus the second.</summary>
  private static IrFunction Twice(string function = "Work")
    => new("Twice", true, IrType.I32, [], [], Block(new IrReturn(Add(new IrBinary(IrBinaryOp.Multiply, Call(function), Int(10000)), Call(function)))));

  private static IrModule Module(params IrFunction[] functions) => new("M", functions) { Globals = [_temp] };

  private static (IrModule Module, List<string> Applied, List<string> Notes) Run(IrModule module, bool verify = true, Func<IrFunction, bool>? scope = null) {
    var applied = new List<string>();
    var notes = new List<string>();
    var result = new O0389GlobalLocalization().Run(module, scope ?? (_ => true), (function, position) => applied.Add($"{function}@{position}"), notes.Add);
    if (verify)
      Assert.That(IrVerifier.Verify(result), Is.Empty);

    return (result, applied, notes);
  }

  private static IrFunction Function(IrModule module, string name) => module.Functions.Single(function => function.Name == name);

  private static bool NamesTemp(IrFunction function)
    => IrTree.Nodes(function.Body).OfType<IrGlobalRef>().Any() || Polypiler.Core.Recognize.IrStatements.All(function.Body).OfType<IrGlobalAssign>().Any();

  private static void Kept(IrModule module, string reason) {
    var (result, applied, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(applied, Is.Empty);
    Assert.That(notes, Does.Contain($"O0389 not applied: global @{reason}"));
  }

  private static object? Interpret(IrModule module, string function, params object[] arguments) => IrInterpreter.Call(module, function, arguments);

  // ---------------------------------------------------------------- the scenarios

  [Test]
  public void AScratchGlobalOfOneFunction_BecomesItsLocal() {
    var module = Module(Work(), Twice());
    var (result, applied, notes) = Run(module);
    var work = Function(result, "Work");
    Assert.That(NamesTemp(work), Is.False);
    Assert.That(work.Body.Statements[0], Is.EqualTo(new IrDeclare(new IrLocal("temp", IrType.I32), Int(0))));
    Assert.That(work.Locals, Does.Contain(new IrLocal("temp", IrType.I32)));
    Assert.That(result.Globals, Is.Empty, "the declaration nothing names any more is dropped");
    Assert.That(applied, Is.EqualTo(new[] { "Work@1" }));
    Assert.That(notes, Does.Contain("O0389 applied: global @temp localized into @Work [proof: structural+alias-facts]"));
    Assert.That(Interpret(module, "Twice"), Is.EqualTo(49504950));
    Assert.That(Interpret(result, "Twice"), Is.EqualTo(49504950));
  }

  [Test]
  public void ARecursiveFunction_KeepsItsGlobal() {
    var g = new IrGlobal("g", IrType.I32, null);
    var n = new IrLocal("n", IrType.I32);
    var f = new IrFunction("f", true, IrType.I32, [n], [], Block(
      new IrGlobalAssign(g, new IrLocalRef(n)),
      new IrIf(new IrBinary(IrBinaryOp.Greater, new IrLocalRef(n), Int(0)), Block(new IrExprStmt(Call("f", new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(n), Int(1))))), null),
      new IrReturn(new IrGlobalRef(g))));
    var module = new IrModule("M", [f]) { Globals = [g] };
    Kept(module, "g: @f is recursive");
    Assert.That(Interpret(module, "f", 3), Is.EqualTo(0));
  }

  [Test]
  public void AValueThatSurvivesBetweenCalls_KeepsItsGlobal() {
    var calls = new IrGlobal("calls", IrType.I32, null);
    var tick = new IrFunction("tick", false, IrType.I32, [], [], Block(
      new IrGlobalAssign(calls, Add(new IrGlobalRef(calls), Int(1))),
      new IrReturn(new IrGlobalRef(calls))));
    var module = new IrModule("M", [tick, Twice("tick")]) { Globals = [calls] };
    Kept(module, "calls: load before the first store");
    Assert.That(Interpret(module, "Twice"), Is.EqualTo(10002));
  }

  [Test]
  public void AGlobalUsedByTwoFunctions_IsNotLocalized() {
    var other = new IrFunction("Other", false, IrType.I32, [], [], Block(new IrGlobalAssign(_temp, Int(5)), new IrReturn(Temp)));
    Kept(Module(Work(), other, Twice()), "temp: used by 2 functions");
  }

  [Test]
  public void TheSwitch_DisablesTheRule() {
    var module = Module(Work(), Twice());
    var disabled = IrPassPipeline.Default.Run(module, new OptimizerOptions { Verify = true, Disabled = new HashSet<string> { "O0389" } });
    Assert.That(disabled.Applications.Select(application => application.Id), Has.None.EqualTo("O0389"));
    Assert.That(disabled.Notes, Has.None.Contains("O0389"));
    Assert.That(disabled.Module.Globals, Has.Count.EqualTo(1));
    var none = IrPassPipeline.Default.Run(module, new OptimizerOptions { Level = OptimizationLevel.None });
    Assert.That(none.Module, Is.SameAs(module));
    var enabled = IrPassPipeline.Default.Run(module, new OptimizerOptions { Verify = true });
    Assert.That(enabled.Applications.Select(application => application.Id), Does.Contain("O0389"));
    Assert.That(enabled.Module.Globals, Is.Empty);
    Assert.That(Interpret(enabled.Module, "Twice"), Is.EqualTo(49504950));
  }

  // ---------------------------------------------------------------- the test design

  /// <summary>Row 2: a public function is localized all the same; its signature is unchanged.</summary>
  [Test]
  public void AnExportedUser_IsLocalized() {
    var module = Module(Work(isPublic: true));
    var (result, _, _) = Run(module);
    Assert.That(NamesTemp(Function(result, "Work")), Is.False);
    Assert.That(Interpret(result, "Work"), Is.EqualTo(Interpret(module, "Work")));
  }

  /// <summary>Row 3: each scalar type.</summary>
  [TestCase("u8", 200L)]
  [TestCase("i64", -5_000_000_000L)]
  [TestCase("f64", 0x4004000000000000L)]
  [TestCase("bool", 1L)]
  public void EachScalarType_IsLocalized(string kind, long value) {
    var type = kind switch { "u8" => IrType.U8, "i64" => IrType.I64, "f64" => IrType.F64, _ => IrType.Bool };
    var g = new IrGlobal("g", type, null);
    var f = new IrFunction("F", true, type, [], [], Block(new IrGlobalAssign(g, new IrConst(type, value)), new IrReturn(new IrGlobalRef(g))));
    var module = new IrModule("M", [f]) { Globals = [g] };
    var (result, _, _) = Run(module);
    Assert.That(result.Globals, Is.Empty);
    Assert.That(Interpret(result, "F"), Is.EqualTo(Interpret(module, "F")));
  }

  /// <summary>Row 4 and 8: pure statements and a call of a helper that cannot reach the function stand before the store.</summary>
  [Test]
  public void StatementsAndCallsBeforeTheStore_AreFine() {
    var k = new IrLocal("k", IrType.I32);
    var p = new IrLocal("p", IrType.I32);
    var helper = new IrFunction("Helper", false, IrType.I32, [p], [], Block(new IrReturn(Add(new IrLocalRef(p), Int(1)))));
    var work = new IrFunction("Work", false, IrType.I32, [], [k, _i], Block(
      new IrDeclare(k, Call("Helper", Int(4))),
      new IrGlobalAssign(_temp, new IrLocalRef(k)),
      Loop(),
      new IrReturn(Add(Temp, Call("Helper", Int(0))))));
    var module = Module(work, helper, Twice());
    var (result, applied, _) = Run(module);
    Assert.That(NamesTemp(Function(result, "Work")), Is.False);
    Assert.That(applied, Is.EqualTo(new[] { "Work@2" }));
    Assert.That(Interpret(result, "Twice"), Is.EqualTo(Interpret(module, "Twice")));
  }

  /// <summary>Row 6: the store first is localized; a read of the global before it is not.</summary>
  [Test]
  public void ALoadBeforeTheStore_KeepsTheGlobal() {
    var x = new IrLocal("x", IrType.I32);
    var work = new IrFunction("Work", false, IrType.I32, [], [x, _i], Block(
      new IrDeclare(x, Temp), new IrGlobalAssign(_temp, Int(0)), Loop(), new IrReturn(Add(Temp, new IrLocalRef(x)))));
    Kept(Module(work, Twice()), "temp: load before the first store");
  }

  /// <summary>Row 7: the first statement that names the global stores it only within a block of its own.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void AStoreNotAtTheTopLevel_KeepsTheGlobal(bool conditional) {
    IrStmt nested = conditional
      ? new IrIf(new IrConst(IrType.Bool, 1), Block(new IrGlobalAssign(_temp, Int(0))), null)
      : Block(new IrGlobalAssign(_temp, Int(0)));
    var work = new IrFunction("Work", false, IrType.I32, [], [_i], Block(nested, Loop(), new IrReturn(Temp)));
    Kept(Module(work, Twice()), "temp: no store in the entry block");
  }

  /// <summary>A statement that only reads the global within its blocks is a load before the store.</summary>
  [Test]
  public void ANestedLoadBeforeTheStore_KeepsTheGlobal() {
    var work = new IrFunction("Work", false, IrType.I32, [], [_i], Block(
      new IrIf(new IrBinary(IrBinaryOp.Equal, Temp, Int(0)), Block(new IrReturn(Int(1))), null),
      new IrGlobalAssign(_temp, Int(0)), Loop(), new IrReturn(Temp)));
    Kept(Module(work, Twice()), "temp: load before the first store");
  }

  /// <summary>An increment of the global after the store becomes an increment of the variable.</summary>
  [Test]
  public void AStepOfTheGlobal_BecomesAStepOfTheVariable() {
    var work = new IrFunction("Work", false, IrType.I32, [], [], Block(
      new IrGlobalAssign(_temp, Int(41)),
      new IrExprStmt(new IrStep(Temp, true, false, false)),
      new IrReturn(Temp)));
    var module = Module(work, Twice());
    var (result, _, _) = Run(module);
    Assert.That(NamesTemp(Function(result, "Work")), Is.False);
    Assert.That(Interpret(result, "Twice"), Is.EqualTo(420042));
  }

  /// <summary>A variable of the function already has the global's name: the new one gets a fresh one.</summary>
  [Test]
  public void ANameTheFunctionHas_IsNotTakenTwice() {
    var shadow = new IrLocal("temp", IrType.I32);
    var work = new IrFunction("Work", false, IrType.I32, [], [shadow, _i], Block(
      new IrDeclare(shadow, Int(1)), new IrGlobalAssign(_temp, Int(0)), Loop(), new IrReturn(Add(Temp, new IrLocalRef(shadow)))));
    var module = Module(work, Twice());
    var (result, _, _) = Run(module);
    Assert.That(Function(result, "Work").Locals.Select(local => local.Name), Does.Contain("temp2"));
    Assert.That(Interpret(result, "Twice"), Is.EqualTo(Interpret(module, "Twice")));
  }

  /// <summary>Row 10: <c>f</c> calls <c>h</c>, which calls <c>f</c>.</summary>
  [Test]
  public void MutualRecursion_KeepsTheGlobal() {
    var n = new IrLocal("n", IrType.I32);
    var f = new IrFunction("f", false, IrType.I32, [n], [], Block(
      new IrGlobalAssign(_temp, new IrLocalRef(n)),
      new IrIf(new IrBinary(IrBinaryOp.Greater, new IrLocalRef(n), Int(0)), Block(new IrExprStmt(Call("h", new IrLocalRef(n)))), null),
      new IrReturn(Temp)));
    var h = new IrFunction("h", false, IrType.I32, [n], [], Block(new IrReturn(Call("f", new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(n), Int(1))))));
    Kept(Module(f, h), "temp: @f is recursive");
  }

  /// <summary>Row 11: a reference to the function exists.</summary>
  [Test]
  public void AnAddressTakenUser_KeepsTheGlobal() {
    var type = IrType.Function(IrType.I32);
    var holder = new IrLocal("w", type);
    var main = new IrFunction("Main", true, IrType.I32, [], [holder], Block(new IrDeclare(holder, new IrFunctionRef(type, "Work")), new IrReturn(Int(0))));
    Kept(Module(Work(), main), "temp: @Work is address-taken");
  }

  /// <summary>Row 12: the function, or a helper it calls, runs code the module cannot name.</summary>
  [TestCase(false, "temp: @Work may be re-entered through Invoke in @Work")]
  [TestCase(true, "temp: @Work may be re-entered through Invoke in @Helper")]
  public void CodeTheModuleCannotName_KeepsTheGlobal(bool inHelper, string reason) {
    var type = IrType.Function(IrType.I32);
    var callback = new IrLocal("callback", type);
    var invoke = new IrInvoke(IrType.I32, new IrLocalRef(callback), []);
    var helper = new IrFunction("Helper", false, IrType.I32, [callback], [], Block(new IrReturn(invoke)));
    var work = new IrFunction("Work", false, IrType.I32, [callback], [], Block(
      new IrGlobalAssign(_temp, Int(1)),
      new IrExprStmt(inHelper ? Call("Helper", new IrLocalRef(callback)) : invoke),
      new IrReturn(Temp)));
    Kept(Module(work, helper), reason);
  }

  /// <summary>Row 15: an array global is no scalar.</summary>
  [Test]
  public void AnArrayGlobal_IsNotScalar() {
    var buffer = new IrGlobal("buffer", IrType.Array(IrType.U8), new IrNewArray(IrType.Array(IrType.U8), Int(16)));
    var f = new IrFunction("F", true, IrType.U8, [], [], Block(
      new IrStore(new IrGlobalRef(buffer), Int(0), new IrConst(IrType.U8, 1)),
      new IrReturn(new IrArrayLoad(new IrGlobalRef(buffer), Int(0)))));
    Kept(new IrModule("M", [f]) { Globals = [buffer] }, "buffer: not scalar");
  }

  /// <summary>Row 16: the address of the global, and a lambda that names it, let it escape.</summary>
  [TestCase(true, "temp: address escapes at a lambda of @Work")]
  [TestCase(false, "temp: address escapes at AddressOf in @Work")]
  public void AnEscapingGlobal_IsKept(bool lambda, string reason) {
    IrStmt escape = lambda
      ? new IrExprStmt(new IrInvoke(IrType.I32, new IrLambda(IrType.Function(IrType.I32), [], [], [], Block(new IrReturn(Temp))), []))
      : new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [Temp]));
    var work = new IrFunction("Work", false, IrType.I32, [], [], Block(new IrGlobalAssign(_temp, Int(0)), escape, new IrReturn(Temp)));
    var module = Module(work);
    var (result, _, notes) = Run(module, verify: false);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain($"O0389 not applied: global @{reason}"));
  }

  /// <summary>A static field of a class may be reached from outside the module.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void AStaticField_IsExternal(bool oneUser) {
    var type = new IrClassType("C");
    type.Define([]);
    type.DefineMembers([], [], [], [_temp]);
    var other = new IrFunction("Other", false, IrType.I32, [], [], Block(new IrGlobalAssign(_temp, Int(5)), new IrReturn(Temp)));
    var module = (oneUser ? Module(Work()) : Module(Work(), other)) with { Types = [type] };
    var (result, _, notes) = Run(module, verify: false);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0389 not applied: global @temp: external"));
  }

  /// <summary>A global the initializer of another reads keeps its value for that one.</summary>
  [Test]
  public void AGlobalAnInitializerReads_IsKept() {
    var copy = new IrGlobal("copy", IrType.I32, Temp);
    var module = Module(Work(), Twice()) with { Globals = [_temp, copy] };
    var (result, _, notes) = Run(module);
    Assert.That(result, Is.SameAs(module));
    Assert.That(notes, Does.Contain("O0389 not applied: global @temp: read by the initializer of @copy"));
  }

  /// <summary>Async functions and iterators interleave their activations.</summary>
  [Test]
  public void AResumableUser_KeepsTheGlobal() {
    var iterator = new IrFunction("Work", false, IrType.Sequence(IrType.I32, false), [], [], Block(
      new IrGlobalAssign(_temp, Int(0)), new IrYield(Temp)));
    var (result, _, notes) = Run(Module(iterator), verify: false);
    Assert.That(result.Globals, Has.Count.EqualTo(1));
    Assert.That(notes, Does.Contain("O0389 not applied: global @temp: @Work is resumable"));
  }

  /// <summary>A global nothing names is no candidate; a function out of scope is left alone.</summary>
  [Test]
  public void UnusedGlobalsAndFunctionsOutOfScope_AreLeftAlone() {
    var unused = Module(Twice("Twice2"), new IrFunction("Twice2", false, IrType.I32, [], [], Block(new IrReturn(Int(0)))));
    var (result, _, notes) = Run(unused);
    Assert.That(result, Is.SameAs(unused));
    Assert.That(notes, Is.Empty);
    var module = Module(Work(), Twice());
    Assert.That(Run(module, scope: function => function.Name != "Work").Module, Is.SameAs(module));
  }

  [Test]
  public void ArgumentsAreChecked() {
    var pass = new O0389GlobalLocalization();
    var module = Module(Work());
    Assert.That(pass.Id, Is.EqualTo("O0389"));
    Assert.That(pass.Name, Is.EqualTo("global variable localization"));
    Assert.Throws<ArgumentNullException>(() => pass.Run(null!, _ => true, (_, _) => { }));
    Assert.Throws<ArgumentNullException>(() => pass.Run(module, null!, (_, _) => { }));
    Assert.Throws<ArgumentNullException>(() => pass.Run(module, _ => true, null!));
    Assert.Throws<ArgumentNullException>(() => pass.Run(module, _ => true, (_, _) => { }, null!));
    Assert.That(pass.Run(module, _ => true, (_, _) => { }).Globals, Is.Empty);
  }

}
