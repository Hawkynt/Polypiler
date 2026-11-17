using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Exceptions;

/// <summary>
/// Trap kinds, trap labels and the handlers that select them (#115), in the tree form of ADR 0009: the
/// table of <c>spec/ir/traps.yaml</c> against the code, the trap table of a module and the naming rule
/// (<c>PLY0178</c>), the assertion, the trap attribute and the test of a handler's labels in the
/// verifier, the interpreter and the binary form. The scenarios of <c>tests/features/ir/ir-except.feature</c>
/// and the rows of the test design are named where they are checked.
/// </summary>
[TestFixture]
public sealed class TrapLabelTests {

  private static readonly TrapLabel OverflowException = new("OverflowException", TrapKind.Overflow, "System.OverflowException");

  private static readonly TrapLabel OutOfRange = new("std::out_of_range", TrapKind.IndexOutOfRange, "std::out_of_range");

  private static readonly IrLocal Caught = new("e", IrExceptionClass.Reference);

  private static IrConst I(long value) => new(IrType.I32, value);

  private static IrConst Bool(bool value) => new(IrType.Bool, value ? 1 : 0);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  private static IrFunction F(IrType result, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] statements)
    => new("F", true, result, parameters, locals, new IrBlock(statements));

  private static IrModule Module(params IrFunction[] functions) => new("M", functions, []) { Traps = [OverflowException, OutOfRange] };

  private static IReadOnlyList<Polypiler.Core.Diagnostic> Problems(IrModule module) => IrVerifier.Verify(module);

  private static object? Run(IrModule module, params object[] arguments) => IrInterpreter.Call(module, "F", arguments);

  /// <summary>A handler: a catch clause of every exception whose filter tests the labels.</summary>
  private static IrCatch Handler(IReadOnlyList<TrapLabel> labels, params IrStmt[] body)
    => new(IrExceptionClass.Exception, Caught, new IrTrapMatches(labels), Block(body));

  private static IrTry Protected(IrBlock body, params IrCatch[] handlers) => new(body, handlers, null);

  private static IrException Trapped(IrModule module, params object[] arguments) => Assert.Throws(Is.InstanceOf<IrException>(), () => Run(module, arguments)) as IrException ?? throw new AssertionException("no trap");

  // ---------------------------------------------------------------- spec/ir/traps.yaml

  private static JsonObject Spec() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "ir", "traps.yaml");
    return (JsonObject)SpecDocument.Parse("ir/traps.yaml", File.ReadAllText(path)).Body!;
  }

  /// <summary>Measurement "trap kinds": the 16 kinds of the specification, under exactly its names and in its order.</summary>
  [Test]
  public void Kinds_AreThoseOfTheSpecification() {
    var names = ((JsonArray)Spec()["kinds"]!).Select(kind => (string)kind!["name"]!).ToList();
    Assert.That(TrapKinds.All.Select(kind => kind.ToString()), Is.EqualTo(names));
    Assert.That(names, Is.EqualTo(new[] {
      "Overflow", "DivideByZero", "IndexOutOfRange", "LengthMismatch", "EmptySequence", "ShapeMismatch", "OverlappingCopy", "NullReference",
      "InvalidCast", "InvalidOperation", "AssertionFailed", "ContractViolation", "OutOfMemory", "StackOverflow", "Unreachable", "User"
    }));
  }

  /// <summary>Every trap of the IR has the kind the specification lists it under, and each kind the class it names.</summary>
  [Test]
  public void KindOfEachTrapAndClassOfEachKind_AreThoseOfTheSpecification() {
    var listed = new Dictionary<IrTrap, TrapKind>();
    foreach (var entry in (JsonArray)Spec()["kinds"]!) {
      var kind = Enum.Parse<TrapKind>((string)entry!["name"]!);
      Assert.That(TrapKinds.Class(kind).Name, Is.EqualTo((string)entry["class"]!), kind.ToString());
      foreach (var trap in (JsonArray)entry["irTraps"]!)
        listed.Add(Enum.Parse<IrTrap>((string)trap!), kind);
    }

    Assert.That(listed.Keys, Is.EquivalentTo(Enum.GetValues<IrTrap>()), "every trap of the IR is listed once");
    foreach (var (trap, kind) in listed)
      Assert.That(TrapKinds.Of(trap), Is.EqualTo(kind), trap.ToString());
  }

  /// <summary>The built-in labels: every kind's name, then the aliases of the specification.</summary>
  [Test]
  public void BuiltInLabels_AreTheKindsAndTheAliasesOfTheSpecification() {
    var aliases = ((JsonArray)Spec()["aliases"]!).Select(alias => new TrapLabel((string)alias!["name"]!, Enum.Parse<TrapKind>((string)alias["kind"]!)));
    Assert.That(TrapLabel.BuiltIn, Is.EqualTo(TrapKinds.All.Select(TrapLabel.Of).Concat(aliases)));
    Assert.That(TrapLabel.BuiltIn, Has.Count.EqualTo(18));
    Assert.That(TrapLabel.BuiltInNamed("ArithmeticOverflow"), Is.EqualTo(new TrapLabel("ArithmeticOverflow", TrapKind.Overflow)));
    Assert.That(TrapLabel.BuiltInNamed("DivisionByZero")!.Kind, Is.EqualTo(TrapKind.DivideByZero));
    Assert.That(TrapLabel.BuiltInNamed("OverflowException"), Is.Null);
  }

  // ---------------------------------------------------------------- the model

  [Test]
  public void KindOfAnUnknownTrap_IsRefused()
    => Assert.That(() => TrapKinds.Of((IrTrap)99), Throws.TypeOf<ArgumentOutOfRangeException>());

  /// <summary>An explicit trap of a kind fails as the operation of its kind does where there is one, with a class and a message of its own otherwise.</summary>
  [Test]
  public void CanonicalTrapClassAndMessage_OfEveryKind() {
    Assert.That(TrapKinds.All.Where(kind => TrapKinds.Canonical(kind) is not null),
      Is.EqualTo(new[] { TrapKind.Overflow, TrapKind.DivideByZero, TrapKind.IndexOutOfRange, TrapKind.NullReference, TrapKind.InvalidCast }));
    foreach (var kind in TrapKinds.All.Where(kind => TrapKinds.Canonical(kind) is { } trap)) {
      Assert.That(TrapKinds.Of(TrapKinds.Canonical(kind)!.Value), Is.EqualTo(kind));
      Assert.That(TrapKinds.Message(kind), Is.EqualTo(IrTrapException.Describe(TrapKinds.Canonical(kind)!.Value)));
    }

    Assert.That(TrapKinds.All.Select(TrapKinds.Message).Distinct().Count(), Is.EqualTo(16), "every kind has a message of its own");
    Assert.That(TrapKinds.Message(TrapKind.EmptySequence), Is.EqualTo("empty sequence"));
    Assert.That(TrapKinds.Message(TrapKind.User), Is.EqualTo("user trap"));
    Assert.That(TrapKinds.Class(TrapKind.OutOfMemory), Is.SameAs(IrExceptionClass.Exception));
    Assert.That(TrapKinds.IsKindName("Overflow"), Is.True);
    Assert.That(TrapKinds.IsKindName("overflow"), Is.False);
  }

  /// <summary>The kinds an operation can fail with itself, one case per class of operation.</summary>
  [Test]
  public void PossibleKinds_OfEachClassOfOperation() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var number = new IrLocal("n", IrType.I32);
    var optional = new IrLocal("o", IrType.Optional(IrType.I32));
    var big = new IrLocal("b", IrType.BigInt);
    var holder = new IrClassType("Holder");
    var field = new IrField("x", IrType.I32, false);
    IReadOnlySet<TrapKind> Of(IrExpr operation) => TrapKinds.Possible(operation);
    Assert.That(Of(new IrBinary(IrBinaryOp.AddChecked, I(1), I(2))), Is.EquivalentTo(new[] { TrapKind.Overflow }));
    Assert.That(Of(new IrBinary(IrBinaryOp.Divide, I(1), I(2))), Is.EquivalentTo(new[] { TrapKind.DivideByZero, TrapKind.Overflow }));
    Assert.That(Of(new IrBinary(IrBinaryOp.Remainder, new IrLocalRef(big), new IrLocalRef(big))), Is.EquivalentTo(new[] { TrapKind.DivideByZero }));
    Assert.That(Of(new IrBinary(IrBinaryOp.Divide, new IrConst(IrType.F64, 0), new IrConst(IrType.F64, 0))), Is.Empty, "floating-point division never traps");
    Assert.That(Of(new IrBinary(IrBinaryOp.Add, I(1), I(2))), Is.Empty);
    Assert.That(Of(new IrUnary(IrUnaryOp.NegateChecked, I(1))), Is.EquivalentTo(new[] { TrapKind.Overflow }));
    Assert.That(Of(new IrConvert(IrType.U8, I(1), IrConversion.Trap)), Is.EquivalentTo(new[] { TrapKind.Overflow }));
    Assert.That(Of(new IrConvert(IrType.U8, I(1), IrConversion.Wrap)), Is.Empty);
    Assert.That(Of(new IrStep(new IrLocalRef(number), true, false, true)), Is.EquivalentTo(new[] { TrapKind.Overflow }));
    Assert.That(Of(new IrStep(new IrLocalRef(number), true, false, false)), Is.Empty);
    Assert.That(Of(new IrArrayLoad(new IrLocalRef(array), I(0))), Is.EquivalentTo(new[] { TrapKind.IndexOutOfRange, TrapKind.NullReference }));
    Assert.That(Of(new IrArrayLength(new IrLocalRef(array))), Is.EquivalentTo(new[] { TrapKind.NullReference }));
    Assert.That(Of(new IrFieldLoad(new IrNull(IrType.Object(holder)), field)), Is.EquivalentTo(new[] { TrapKind.NullReference }));
    Assert.That(Of(new IrNewArray(IrType.Array(IrType.I32), I(1))), Is.EquivalentTo(new[] { TrapKind.Overflow, TrapKind.OutOfMemory }));
    Assert.That(Of(new IrUnwrap(new IrLocalRef(optional))), Is.EquivalentTo(new[] { TrapKind.NullReference }));
    Assert.That(Of(new IrCast(IrType.Object(holder), new IrNull(IrExceptionClass.Reference))), Is.EquivalentTo(new[] { TrapKind.InvalidCast }));
    Assert.That(Of(new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrStrConst("x")])), Has.Count.EqualTo(16));
    Assert.That(Of(new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [new IrLocalRef(number)])), Is.Empty);
    Assert.That(Of(new IrCollection(IrCollectionOp.ListCount, IrType.I32, [])), Has.Count.EqualTo(16));
    Assert.That(Of(new IrConditional(Bool(true), I(1), I(2))), Is.Empty, "a select cannot fail");
    Assert.That(Of(new IrCall("G", IrType.I32, [])), Is.Empty, "a call fails under its callee's labels");
    Assert.That(() => TrapKinds.Possible(null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 16: the naming rule - a declared source type name is a label, an invented spelling is none.</summary>
  [TestCase("OverflowException", null)]
  [TestCase("std::out_of_range", null)]
  [TestCase("ZeroDivisionError", null)]
  [TestCase("ArrayIndexOutOfBoundsException", null)]
  [TestCase("panic", null)]
  [TestCase("Overflow", "is built in")]
  [TestCase("ArithmeticOverflow", "is built in")]
  [TestCase("PanicOverflow", "invented spelling")]
  [TestCase("Overflow_Checked", "invented spelling")]
  [TestCase("Checked_DivideByZero", "invented spelling")]
  [TestCase("", "is empty or holds white space")]
  [TestCase("Out Of Range", "is empty or holds white space")]
  public void Undeclarable_FollowsTheNamingRule(string name, string? reason) {
    var found = TrapLabel.Undeclarable(name);
    if (reason is null)
      Assert.That(found, Is.Null);
    else
      Assert.That(found, Does.Contain(reason));
  }

  [Test]
  public void Resolve_FindsBuiltInThenDeclaredLabels() {
    var module = Module();
    Assert.That(TrapLabel.Resolve(module, "Overflow"), Is.EqualTo(TrapLabel.Of(TrapKind.Overflow)));
    Assert.That(TrapLabel.Resolve(module, "OverflowException"), Is.EqualTo(OverflowException));
    Assert.That(TrapLabel.Resolve(module, "FancyError"), Is.Null);
    Assert.That(() => TrapLabel.Resolve(null!, "x"), Throws.ArgumentNullException);
    Assert.That(() => TrapLabel.Resolve(module, null!), Throws.ArgumentNullException);
    Assert.That(OverflowException.IsDeclaredIn(module), Is.True);
    Assert.That(TrapLabel.Of(TrapKind.User).IsDeclaredIn(module), Is.True);
    Assert.That((OverflowException with { Kind = TrapKind.User }).IsDeclaredIn(module), Is.False, "the name with another kind is another label");
    Assert.That(() => OverflowException.IsDeclaredIn(null!), Throws.ArgumentNullException);
    Assert.That(() => TrapLabel.Undeclarable(null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 3 and 5: a built-in label selects its kind, a declared one only itself.</summary>
  [Test]
  public void Selects_ByKindForBuiltInLabelsAndByNameOtherwise() {
    var overflow = TrapLabel.Of(TrapKind.Overflow);
    var arithmetic = TrapLabel.BuiltInNamed("ArithmeticOverflow")!;
    Assert.That(overflow.Selects(OverflowException), Is.True);
    Assert.That(arithmetic.Selects(overflow), Is.True);
    Assert.That(overflow.Selects(arithmetic), Is.True);
    Assert.That(OverflowException.Selects(OverflowException), Is.True);
    Assert.That(OverflowException.Selects(overflow), Is.False);
    Assert.That(overflow.Selects(TrapLabel.Of(TrapKind.DivideByZero)), Is.False);
    Assert.That(() => overflow.Selects(null!), Throws.ArgumentNullException);
  }

  // ---------------------------------------------------------------- the interpreter

  /// <summary>Scenario "An assertion that fails traps with its label"; test design 1.</summary>
  [Test]
  public void Assert_ContinuesWhenTrueAndTrapsWithItsLabelWhenFalse() {
    var condition = new IrLocal("c", IrType.Bool);
    var module = Module(F(IrType.I32, [condition], [], new IrAssert(new IrLocalRef(condition), TrapLabel.Of(TrapKind.IndexOutOfRange)), new IrReturn(I(1))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Run(module, true), Is.EqualTo(1));
    var trapped = (IrTrapException)Trapped(module, false);
    Assert.That((trapped.Trap, trapped.Label), Is.EqualTo((IrTrap.IndexOutOfRange, TrapLabel.Of(TrapKind.IndexOutOfRange))));
  }

  /// <summary>Test design 2: an explicit trap of each of the 16 kinds is reported with that kind, its class and its message.</summary>
  [Test]
  public void Fail_OfEveryKind_ReportsThatKind() {
    foreach (var kind in TrapKinds.All) {
      var trapped = Trapped(Module(F(IrType.Void, [], [], IrAssert.Fail(TrapLabel.Of(kind)))));
      Assert.That(trapped.Label!.Kind, Is.EqualTo(kind));
      Assert.That((trapped.Class, trapped.Message), Is.EqualTo((TrapKinds.Class(kind), TrapKinds.Message(kind))), kind.ToString());
      if (TrapKinds.Canonical(kind) is { } trap)
        Assert.That(((IrTrapException)trapped).Trap, Is.EqualTo(trap));
      else
        Assert.That(((IrKindTrapException)trapped).Kind, Is.EqualTo(kind));
    }
  }

  /// <summary>Test design 4: an alias label keeps its kind and its exception type in the result.</summary>
  [Test]
  public void AliasLabel_KeepsItsKindAndExceptionType() {
    var trapped = Trapped(Module(F(IrType.Void, [], [], IrAssert.Fail(OverflowException))));
    Assert.That(trapped.Label, Is.EqualTo(OverflowException));
    Assert.That(trapped.Label!.ExceptionType, Is.EqualTo("System.OverflowException"));
    Assert.That(((IrTrapException)trapped).Trap, Is.EqualTo(IrTrap.IntegerOverflow));
  }

  /// <summary>
  /// Scenario "Effects before a trap are kept, effects after it never happen" and test design 15: a
  /// write, the trap, a write - the C# twin <c>a[0] = 1; throw new OverflowException(); a[1] = 2;</c>
  /// leaves <c>a[0] == 1</c> and <c>a[1] == 0</c> when the exception is caught.
  /// </summary>
  [Test]
  public void EffectsBeforeATrap_AreKeptAndThoseAfterItNeverHappen() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    IrExpr A() => new IrLocalRef(array);
    var module = Module(F(IrType.I32, [], [array, Caught],
      new IrDeclare(array, new IrNewArray(IrType.Array(IrType.I32), I(2))),
      Protected(Block(new IrStore(A(), I(0), I(1)), IrAssert.Fail(TrapLabel.Of(TrapKind.Overflow)), new IrStore(A(), I(1), I(2))),
        Handler([TrapLabel.Of(TrapKind.Overflow)])),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, new IrArrayLoad(A(), I(0)), I(10)), new IrArrayLoad(A(), I(1))))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Run(module), Is.EqualTo(10));
  }

  /// <summary>Test design 9: a trap as the first and as the last statement of a function.</summary>
  [Test]
  public void Trap_AsTheFirstAndAsTheLastStatement() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var first = Module(F(IrType.Void, [array], [], IrAssert.Fail(TrapLabel.Of(TrapKind.User)), new IrStore(new IrLocalRef(array), I(0), I(7))));
    var values = new int[1];
    Assert.That(Trapped(first, (object)values).Label!.Kind, Is.EqualTo(TrapKind.User));
    var last = Module(F(IrType.Void, [array], [], new IrStore(new IrLocalRef(array), I(0), I(7)), IrAssert.Fail(TrapLabel.Of(TrapKind.User))));
    Assert.That(Trapped(last, (object)values).Label!.Kind, Is.EqualTo(TrapKind.User));
  }

  /// <summary>Scenario "A handler catches the matching label and not another".</summary>
  [Test]
  public void Handler_CatchesTheMatchingLabelAndNotAnother() {
    var k = new IrLocal("k", IrType.Bool);
    var module = Module(F(IrType.I32, [k], [Caught],
      Protected(Block(new IrIf(new IrLocalRef(k), Block(IrAssert.Fail(OverflowException)), Block(IrAssert.Fail(TrapLabel.Of(TrapKind.DivideByZero))))),
        Handler([TrapLabel.Of(TrapKind.Overflow)], new IrReturn(I(99)))),
      new IrReturn(I(0))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Run(module, true), Is.EqualTo(99));
    Assert.That(Trapped(module, false).Label, Is.EqualTo(TrapLabel.Of(TrapKind.DivideByZero)));
  }

  private static object? Selected(TrapLabel thrown, params TrapLabel[] handler)
    => Run(Module(F(IrType.I32, [], [Caught], Protected(Block(IrAssert.Fail(thrown)), Handler(handler, new IrReturn(I(1)))), new IrReturn(I(0)))));

  /// <summary>Test design 3, 5 and 10: by kind, by built-in alias, by declared label, catch-all; a list that does not match lets the trap through.</summary>
  [Test]
  public void Handler_MatchesByKindByAliasCatchAllOrNotAtAll() {
    Assert.That(Selected(TrapLabel.BuiltInNamed("ArithmeticOverflow")!, TrapLabel.Of(TrapKind.Overflow)), Is.EqualTo(1));
    Assert.That(Selected(TrapLabel.BuiltInNamed("DivisionByZero")!, TrapLabel.Of(TrapKind.DivideByZero)), Is.EqualTo(1));
    Assert.That(Selected(TrapLabel.Of(TrapKind.DivideByZero), TrapLabel.BuiltInNamed("DivisionByZero")!), Is.EqualTo(1));
    Assert.That(Selected(OverflowException, OverflowException), Is.EqualTo(1));
    Assert.That(Selected(TrapLabel.Of(TrapKind.EmptySequence)), Is.EqualTo(1), "an empty list catches every trap");
    Assert.That(Selected(OutOfRange, TrapLabel.Of(TrapKind.User), TrapLabel.Of(TrapKind.IndexOutOfRange)), Is.EqualTo(1), "the second label of the list");
    Assert.That(() => Selected(TrapLabel.Of(TrapKind.Overflow), OverflowException), Throws.InstanceOf<IrException>(), "a declared label selects only itself");
    Assert.That(() => Selected(TrapLabel.Of(TrapKind.User), TrapLabel.Of(TrapKind.Overflow)), Throws.InstanceOf<IrException>());
  }

  /// <summary>An exception the program throws is no trap: no list of labels selects it, the empty one neither.</summary>
  [Test]
  public void ThrownException_IsSelectedByNoLabels() {
    var thrown = new IrThrow(new IrNew(IrExceptionClass.Reference, IrExceptionClass.Constructor(IrExceptionClass.Exception, 0), []));
    var module = Module(F(IrType.I32, [], [Caught], Protected(Block(thrown), Handler([], new IrReturn(I(1)))), new IrReturn(I(0))));
    var escaped = Trapped(module);
    Assert.That(escaped.Label, Is.Null);
  }

  /// <summary>Test design 6: a trap in a callee is handled by the caller's handler around the call - the tree form of an unwind edge.</summary>
  [Test]
  public void TrapInACallee_IsHandledInTheCaller() {
    var callee = new IrFunction("G", false, IrType.I32, [], [], Block(IrAssert.Fail(OutOfRange), new IrReturn(I(5))));
    var caller = F(IrType.I32, [], [Caught], Protected(Block(new IrReturn(new IrCall("G", IrType.I32, []))), Handler([OutOfRange], new IrReturn(I(42)))), new IrReturn(I(0)));
    var module = Module(caller, callee);
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Run(module), Is.EqualTo(42));
  }

  /// <summary>Test design 7: a trap caught and thrown again propagates with the same label.</summary>
  [Test]
  public void Rethrow_KeepsTheLabel() {
    var inner = Protected(Block(IrAssert.Fail(OverflowException)), Handler([TrapLabel.Of(TrapKind.Overflow)], new IrRethrow()));
    var module = Module(F(IrType.I32, [], [Caught], Protected(Block(inner), new IrCatch(IrExceptionClass.Exception, null, new IrTrapMatches([OverflowException]), Block(new IrReturn(I(3))))), new IrReturn(I(0))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Run(module), Is.EqualTo(3));
    var escaping = Module(F(IrType.Void, [], [Caught], inner));
    Assert.That(Trapped(escaping).Label, Is.EqualTo(OverflowException));
  }

  /// <summary>Test design 8: nested handlers 1, 2 and 16 deep whose inner ones do not match - the outermost runs.</summary>
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(16)]
  public void NestedHandlers_TheOuterRunsWhenTheInnerDoNotMatch(int depth) {
    IrStmt body = IrAssert.Fail(TrapLabel.Of(TrapKind.ShapeMismatch));
    for (var level = 1; level < depth; ++level)
      body = Protected(Block(body), Handler([TrapLabel.Of(TrapKind.LengthMismatch)], new IrReturn(I(-level))));

    var module = Module(F(IrType.I32, [], [Caught], Protected(Block(body), Handler([TrapLabel.Of(TrapKind.ShapeMismatch)], new IrReturn(I(depth)))), new IrReturn(I(0))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Run(module), Is.EqualTo(depth));
  }

  /// <summary>Scenario "The first of two failing instructions wins".</summary>
  [Test]
  public void FirstOfTwoFailingAssertions_Wins() {
    var p = new IrLocal("p", IrType.Bool);
    var q = new IrLocal("q", IrType.Bool);
    var module = Module(F(IrType.Void, [p, q], [],
      new IrAssert(new IrLocalRef(p), TrapLabel.Of(TrapKind.NullReference)), new IrAssert(new IrLocalRef(q), TrapLabel.Of(TrapKind.IndexOutOfRange))));
    Assert.That(Trapped(module, false, false).Label!.Kind, Is.EqualTo(TrapKind.NullReference));
    Assert.That(Trapped(module, true, false).Label!.Kind, Is.EqualTo(TrapKind.IndexOutOfRange));
    Assert.That(Run(module, true, true), Is.Null);
  }

  /// <summary>A trap attribute names the operation's own trap of its kind; the value of an operation that does not fail is its own.</summary>
  [Test]
  public void TrapAttribute_LabelsTheOperationsOwnTrap() {
    var a = new IrLocal("a", IrType.I32);
    var add = new IrTrapAttribute(new IrBinary(IrBinaryOp.AddChecked, new IrLocalRef(a), I(1)), OverflowException);
    var module = Module(F(IrType.I32, [a], [], new IrReturn(add)));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Run(module, 41), Is.EqualTo(42));
    Assert.That(Trapped(module, int.MaxValue).Label, Is.EqualTo(OverflowException));
  }

  /// <summary>A trap of an operand keeps its label, and so does a trap of another kind than the attribute's.</summary>
  [Test]
  public void TrapAttribute_LeavesTheTrapsOfOperandsAndOfOtherKinds() {
    var a = new IrLocal("a", IrType.I32);
    var b = new IrLocal("b", IrType.I32);
    var inner = new IrBinary(IrBinaryOp.AddChecked, new IrLocalRef(a), I(1));
    var outer = Module(F(IrType.I32, [a, b], [], new IrReturn(new IrTrapAttribute(new IrBinary(IrBinaryOp.AddChecked, inner, new IrLocalRef(b)), OverflowException))));
    Assert.That(Problems(outer), Is.Empty);
    Assert.That(Trapped(outer, int.MaxValue, 0).Label, Is.EqualTo(TrapLabel.Of(TrapKind.Overflow)), "the operand overflowed");
    Assert.That(Trapped(outer, 0, int.MaxValue).Label, Is.EqualTo(OverflowException), "the attributed addition overflowed");
    var division = Module(F(IrType.I32, [a, b], [], new IrReturn(new IrTrapAttribute(new IrBinary(IrBinaryOp.Divide, new IrLocalRef(a), new IrLocalRef(b)), OverflowException))));
    Assert.That(Trapped(division, 1, 0).Label, Is.EqualTo(TrapLabel.Of(TrapKind.DivideByZero)));
    Assert.That(Trapped(division, int.MinValue, -1).Label, Is.EqualTo(OverflowException));
  }

  /// <summary>An attribute on an element access, on a step of an element and on a step of a variable; the temporaries are gone after it.</summary>
  [Test]
  public void TrapAttribute_OnElementsAndSteps() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var index = new IrLocal("i", IrType.I32);
    var load = Module(F(IrType.I32, [array, index], [], new IrReturn(new IrTrapAttribute(new IrArrayLoad(new IrLocalRef(array), new IrLocalRef(index)), OutOfRange))));
    Assert.That(Problems(load), Is.Empty);
    Assert.That(Run(load, new[] { 4, 5 }, 1), Is.EqualTo(5));
    Assert.That(Trapped(load, new[] { 4, 5 }, 2).Label, Is.EqualTo(OutOfRange));
    var step = Module(F(IrType.I32, [array, index], [],
      new IrExprStmt(new IrTrapAttribute(new IrStep(new IrArrayLoad(new IrLocalRef(array), new IrLocalRef(index)), true, false, true), OverflowException)),
      new IrReturn(new IrArrayLoad(new IrLocalRef(array), new IrLocalRef(index)))));
    Assert.That(Problems(step), Is.Empty);
    Assert.That(Run(step, new[] { 4, 5 }, 0), Is.EqualTo(5));
    Assert.That(Trapped(step, new[] { int.MaxValue }, 0).Label, Is.EqualTo(OverflowException));
    Assert.That(Trapped(step, new[] { 1 }, 3).Label, Is.EqualTo(TrapLabel.Of(TrapKind.IndexOutOfRange)), "the index of the place is no trap of the step");
    var counter = new IrLocal("c", IrType.I32);
    var local = Module(F(IrType.I32, [counter], [],
      new IrExprStmt(new IrTrapAttribute(new IrStep(new IrLocalRef(counter), true, false, true), OverflowException)), new IrReturn(new IrLocalRef(counter))));
    Assert.That(Problems(local), Is.Empty);
    Assert.That(Run(local, 1), Is.EqualTo(2));
    Assert.That(Trapped(local, int.MaxValue).Label, Is.EqualTo(OverflowException));
  }

  // ---------------------------------------------------------------- the verifier

  private static IReadOnlyList<string> Codes(IrModule module) => [.. Problems(module).Select(problem => $"{problem.Code}: {problem.Message}")];

  /// <summary>Scenario "An undeclared trap label is rejected"; test design 11 and 16.</summary>
  [Test]
  public void UndeclaredLabel_IsRefusedWithPly0178() {
    var condition = new IrLocal("c", IrType.Bool);
    var fancy = Module(F(IrType.Void, [condition], [], new IrAssert(new IrLocalRef(condition), new TrapLabel("FancyError", TrapKind.User))));
    Assert.That(Codes(fancy), Is.EqualTo(new[] { "PLY0178: trap label 'FancyError' is not declared in module 'M', in 'F'" }));
    var panic = Module(F(IrType.Void, [], [], IrAssert.Fail(new TrapLabel("PanicOverflow", TrapKind.Overflow))));
    Assert.That(Codes(panic).Single(), Does.StartWith("PLY0178: trap label 'PanicOverflow' is not declared"));
    var declared = Module(F(IrType.Void, [], [], IrAssert.Fail(OverflowException), IrAssert.Fail(OutOfRange), IrAssert.Fail(TrapLabel.Of(TrapKind.Overflow))));
    Assert.That(Codes(declared), Is.Empty, "declared, declared, built in");
    var handler = Module(F(IrType.Void, [], [Caught], Protected(Block(), Handler([new TrapLabel("Nope", TrapKind.User)]))));
    Assert.That(Codes(handler).Single(), Does.Contain("trap label 'Nope' is not declared"));
    var attribute = Module(F(IrType.I32, [], [], new IrReturn(new IrTrapAttribute(new IrBinary(IrBinaryOp.AddChecked, I(1), I(1)), new TrapLabel("Nope", TrapKind.Overflow)))));
    Assert.That(Codes(attribute).Single(), Does.Contain("trap label 'Nope' is not declared"));
  }

  /// <summary>Test design 12: an attribute on an operation that cannot fail, or not with its kind.</summary>
  [Test]
  public void AttributeOnAnOperationThatCannotFailSo_IsRefusedWithPly0178() {
    var select = Module(F(IrType.I32, [], [], new IrReturn(new IrTrapAttribute(new IrConditional(Bool(true), I(1), I(2)), TrapLabel.Of(TrapKind.Overflow)))));
    Assert.That(Codes(select), Is.EqualTo(new[] { "PLY0178: 'IrConditional' cannot carry a trap attribute [trap Overflow] in 'F': it cannot fail with Overflow" }));
    var kind = Module(F(IrType.I32, [], [], new IrReturn(new IrTrapAttribute(new IrBinary(IrBinaryOp.AddChecked, I(1), I(1)), TrapLabel.Of(TrapKind.DivideByZero)))));
    Assert.That(Codes(kind).Single(), Does.Contain("cannot carry a trap attribute [trap DivideByZero]"));
    var callee = new IrFunction("G", false, IrType.I32, [], [], Block(new IrReturn(I(1))));
    var call = Module(F(IrType.I32, [], [], new IrReturn(new IrTrapAttribute(new IrCall("G", IrType.I32, []), OverflowException))), callee);
    Assert.That(Codes(call).Single(), Does.Contain("'IrCall 'G'' cannot carry a trap attribute"));
  }

  /// <summary>What else the verifier checks of the new nodes: the condition, the type, a label listed twice, a test outside a filter.</summary>
  [Test]
  public void Verifier_ChecksTheShapeOfTheTrapNodes() {
    Assert.That(Codes(Module(F(IrType.Void, [], [], new IrAssert(I(1), TrapLabel.Of(TrapKind.User))))).Single(), Does.Contain("the condition of an assertion has the type i32"));
    var retyped = new IrTrapAttribute(new IrBinary(IrBinaryOp.AddChecked, I(1), I(1)), OverflowException) with { Type = IrType.I64 };
    Assert.That(Codes(Module(F(IrType.I64, [], [], new IrReturn(retyped)))), Has.Some.Contains("a trap attribute of the type i64 on an operation of the type i32"));
    var twice = Module(F(IrType.Void, [], [Caught], Protected(Block(), Handler([OverflowException, OverflowException]))));
    Assert.That(Codes(twice).Single(), Does.Contain("a handler lists a trap label twice"));
    var outside = Module(F(IrType.Bool, [], [], new IrReturn(new IrTrapMatches([]))));
    Assert.That(Codes(outside).Single(), Does.StartWith("PLY0100").And.Contain("the labels of a handler are tested outside the filter of a catch clause"));
    var lambdaType = IrType.Function(new IrFunctionType([], IrType.Bool));
    var lambda = new IrLambda(lambdaType, [], [], [], Block(new IrReturn(new IrTrapMatches([]))));
    var deferred = Module(F(IrType.Void, [], [Caught], Protected(Block(), new IrCatch(IrExceptionClass.Exception, Caught, new IrInvoke(IrType.Bool, lambda, []), Block()))));
    Assert.That(Codes(deferred), Has.One.Contains("tested outside the filter of a catch clause"), "a lambda's body runs when it is called");
  }

  /// <summary>Test design 16 for the trap table: declared once, under a name a label may have.</summary>
  [Test]
  public void TrapTable_IsCheckedOnce() {
    IReadOnlyList<string> Table(params TrapLabel[] labels) => Codes(new IrModule("T", [], []) { Traps = labels });
    Assert.That(Table(OverflowException, OutOfRange, new TrapLabel("ZeroDivisionError", TrapKind.DivideByZero)), Is.Empty);
    Assert.That(Table(OverflowException, OverflowException with { Kind = TrapKind.User }), Is.EqualTo(new[] { "PLY0178: trap label 'OverflowException' is declared twice in module 'T'" }));
    Assert.That(Table(new TrapLabel("Overflow", TrapKind.Overflow)).Single(), Does.Contain("is built in").And.Contain("in the trap table of module 'T'"));
    Assert.That(Table(new TrapLabel("PanicOverflow", TrapKind.Overflow)).Single(), Does.Contain("invented spelling"));
    Assert.That(Table(new TrapLabel("", TrapKind.User)).Single(), Does.Contain("is empty"));
  }

  /// <summary>The rule is one of the standard registry and reports under its own code.</summary>
  [Test]
  public void Rule_IsRegisteredAndReportsUnderItsOwnCode() {
    var rule = VerifierRuleRegistry.Standard.Find("ir.verifier.traps.label-declared")!;
    Assert.That((rule.Family, rule.Description), Is.EqualTo(("traps", "every trap label is built in or declared in the module's trap table, and a trap attribute stands on an operation that can fail with its kind")));
    var probe = new ProbeRule();
    var reported = IrVerifier.Verify(Module(F(IrType.Void, [], [])), VerifierRuleRegistry.CreateStandard().Register(probe));
    Assert.That(probe.Refusals, Is.EqualTo(2));
    Assert.That(reported.Single().Code, Is.EqualTo("PLY0100"));
    Assert.That(reported.Single().Message, Is.EqualTo("probe"));
  }

  /// <summary>A rule that reports once under a descriptor of its own and counts the null arguments refused.</summary>
  private sealed class ProbeRule : IVerifierRule {

    public int Refusals { get; private set; }

    public string Id => "test.probe";

    public string Family => "test";

    public string Description => "reports once";

    public void Check(VerifierRuleContext context) {
      foreach (var call in new Action[] { () => context.Report(null!, "x"), () => context.Report(IrVerifier.Descriptor, null!) })
        try {
          call();
        } catch (ArgumentNullException) {
          ++this.Refusals;
        }

      context.Report(IrVerifier.Descriptor, "probe");
    }

  }

  /// <summary>The effect summary counts an assertion as a trap, so that no pass drops it as dead code, and an attribute's operation as it is.</summary>
  [Test]
  public void EffectSummary_CountsAnAssertionAsATrap() {
    var condition = new IrLocal("c", IrType.Bool);
    var module = Module(F(IrType.Void, [condition], [], new IrAssert(new IrLocalRef(condition), TrapLabel.Of(TrapKind.User))));
    Assert.That(Polypiler.Core.Optimize.Passes.IrEffects.Of(module, "F").MayTrap, Is.True);
    var attributed = Module(F(IrType.I32, [], [], new IrReturn(new IrTrapAttribute(new IrBinary(IrBinaryOp.AddChecked, I(1), I(1)), OverflowException))));
    Assert.That(Polypiler.Core.Optimize.Passes.IrEffects.Of(attributed, "F").MayTrap, Is.True);
  }

  // ---------------------------------------------------------------- the tree and the binary form

  [Test]
  public void Tree_ListsAndReplacesTheOperandsOfTheTrapNodes() {
    var attribute = new IrTrapAttribute(new IrBinary(IrBinaryOp.AddChecked, I(1), I(2)), OverflowException);
    var replaced = new IrBinary(IrBinaryOp.AddChecked, I(3), I(4));
    Assert.That(IrTree.Operands(attribute), Is.EqualTo(new[] { attribute.Operation }));
    Assert.That(IrTree.With(attribute, [replaced]), Is.EqualTo(attribute with { Operation = replaced }));
    var assertion = new IrAssert(Bool(true), OutOfRange);
    Assert.That(IrTree.Expressions(assertion), Is.EqualTo(new[] { assertion.Condition }));
    Assert.That(IrTree.With(assertion, [Bool(false)], []), Is.EqualTo(IrAssert.Fail(OutOfRange)));
    Assert.That(IrTree.Operands(new IrTrapMatches([])), Is.Empty);
  }

  /// <summary>The trap table and the three nodes survive the binary form, and the module read back behaves the same.</summary>
  [Test]
  public void BinaryForm_KeepsTheTrapTableAndTheTrapNodes() {
    var k = new IrLocal("k", IrType.Bool);
    var a = new IrLocal("a", IrType.I32);
    var module = Module(F(IrType.I32, [k, a], [Caught],
      Protected(Block(new IrAssert(new IrLocalRef(k), OutOfRange), new IrReturn(new IrTrapAttribute(new IrBinary(IrBinaryOp.AddChecked, new IrLocalRef(a), I(1)), OverflowException))),
        Handler([OutOfRange, TrapLabel.Of(TrapKind.User)], new IrReturn(I(-1)))),
      new IrReturn(I(0))));
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(read.Traps, Is.EqualTo(module.Traps));
    Assert.That(Problems(read), Is.Empty);
    Assert.That((Run(read, true, 1), Run(read, false, 1)), Is.EqualTo((2, -1)));
    Assert.That(Trapped(read, true, int.MaxValue).Label, Is.EqualTo(OverflowException));
    Assert.That(IrModuleFormat.Read(IrModuleFormat.Write(new IrModule("E", []))).Traps, Is.Empty);
  }

}
