using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The object model of the IR on modules built by hand: the rules of the verifier about classes and
/// their members, and what the interpreter does with objects, <c>null</c> and the boundary.
/// </summary>
[TestFixture]
public sealed class IrObjectTests {

  /// <summary>A class K: a field <c>v</c>, a read-only array field <c>a</c>, a constructor, a method <c>Get</c> and a static method <c>Make</c>.</summary>
  private sealed class Built {

    public IrClassType Type { get; } = new("K");

    public IrField Value { get; } = new("v", IrType.I32, false);

    public IrField Cells { get; } = new("a", IrType.Array(IrType.I32), true);

    public IrConstructor Constructor { get; } = new("K.ctor");

    public IrMethod Get { get; } = new("Get", "K.Get", false);

    public IrMethod Make { get; } = new("Make", "K.Make", true);

    public IrLocal This(string name = "this") => new(name, this.Object);

    public IrType Object => IrType.Object(this.Type);

    public List<IrFunction> Functions { get; } = [];

    public Built(IrBlock? constructorBody = null, IReadOnlyList<IrProperty>? properties = null, IReadOnlyList<IrMethod>? extra = null, IReadOnlyList<IrGlobal>? statics = null) {
      this.Type.Define([this.Value, this.Cells]);
      this.Type.DefineMembers([this.Constructor], [this.Get, this.Make, .. extra ?? []], properties ?? [], statics ?? []);
      var self = this.This();
      var start = new IrLocal("start", IrType.I32);
      this.Functions.Add(new IrFunction("K.ctor", false, IrType.Void, [self, start], [], constructorBody ?? new IrBlock([
        new IrFieldStore(new IrLocalRef(self), this.Value, new IrLocalRef(start)),
        new IrFieldStore(new IrLocalRef(self), this.Cells, new IrNewArray(IrType.Array(IrType.I32), new IrConst(IrType.I32, 2)))
      ])));
      var reader = this.This();
      this.Functions.Add(new IrFunction("K.Get", false, IrType.I32, [reader], [], new IrBlock([new IrReturn(new IrFieldLoad(new IrLocalRef(reader), this.Value))])));
      this.Functions.Add(new IrFunction("K.Make", false, this.Object, [], [], new IrBlock([new IrReturn(new IrNew(this.Object, this.Constructor, [new IrConst(IrType.I32, 7)]))])));
    }

    public IrModule Module(params IrFunction[] functions) => new("M", [.. this.Functions, .. functions]) { Types = [this.Type] };

  }

  private static IrFunction F(IrType result, IReadOnlyList<IrLocal> locals, params IrStmt[] statements) => new("F", true, result, [], locals, new IrBlock(statements));

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  [Test]
  public void WellFormedModule_IsAcceptedAndRuns() {
    var built = new Built();
    var made = new IrLocal("k", built.Object);
    var module = built.Module(F(IrType.I32, [made],
      new IrDeclare(made, new IrMethodCall(built.Object, built.Make, null, [])),
      new IrFieldStore(new IrLocalRef(made), built.Value, new IrBinary(IrBinaryOp.Add, new IrFieldLoad(new IrLocalRef(made), built.Value), new IrConst(IrType.I32, 1))),
      new IrReturn(new IrMethodCall(IrType.I32, built.Get, new IrLocalRef(made), []))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(8));
  }

  /// <summary>Test design 13: what the verifier refuses of objects, one rule each.</summary>
  [Test]
  public void Verifier_RefusesEveryBrokenRule() {
    var built = new Built();
    var k = new IrLocal("k", built.Object);
    IrExpr K() => new IrLocalRef(k);
    void Refused(IrModule module, string message) => Assert.That(Problems(module), Has.Some.Contains(message), string.Join("\n", Problems(module)));

    Refused(built.Module(F(IrType.Void, [k], new IrDeclare(k, new IrNull(built.Object)), new IrFieldStore(K(), built.Cells, new IrNewArray(IrType.Array(IrType.I32), new IrConst(IrType.I32, 1))))),
      "the read-only field 'a' is written");
    Refused(built.Module(F(IrType.Void, [], new IrExprStmt(new IrMethodCall(IrType.I32, built.Get, null, [])))), "is called without a receiver");
    Refused(built.Module(F(IrType.Void, [], new IrExprStmt(new IrMethodCall(built.Object, built.Make, new IrNull(built.Object), [])))), "is called with a receiver");
    Refused(built.Module(F(IrType.Void, [], new IrExprStmt(new IrMethodCall(IrType.I64, built.Get, new IrNull(built.Object), [])))), "the method returns i32");
    Refused(built.Module(F(IrType.Void, [], new IrExprStmt(new IrMethodCall(IrType.I32, built.Get, new IrNull(built.Object), [new IrConst(IrType.I32, 1)])))), "do not match its parameters");
    Refused(built.Module(F(IrType.Void, [], new IrExprStmt(new IrMethodCall(IrType.I32, new IrMethod("X", "X", false), new IrNull(built.Object), [])))), "is not one of a class of the module");
    Refused(built.Module(F(IrType.Void, [], new IrExprStmt(new IrNew(built.Object, new IrConstructor("other"), [new IrConst(IrType.I32, 1)])))), "a constructor that is not one of K");
    Refused(built.Module(F(IrType.Void, [], new IrExprStmt(new IrNull(IrType.I32)))), "a reference to an object is expected");
    Refused(built.Module(F(IrType.Bool, [], new IrReturn(new IrStructEquals(new IrNull(built.Object), new IrNull(built.Object))))), "which is no record");
    Refused(built.Module(F(IrType.Bool, [], new IrReturn(new IrReferenceEquals(new IrNull(built.Object), new IrConst(IrType.I32, 0))))), "is compared with one of the type i32");
    Refused(built.Module(F(IrType.Void, [], new IrExprStmt(new IrCall("K.Get", IrType.I32, [new IrNull(built.Object)])))), "the function 'K.Get' of a member is called by name");
    Refused(built.Module(F(IrType.Void, [], new IrReturn(new IrFieldLoad(new IrNull(built.Object), new IrField("x", IrType.I32, false))))), "the field 'x' is not a field of K");

    var other = new IrClassType("Other");
    other.Define([]);
    other.DefineMembers([], [], [], []);
    var module = built.Module(F(IrType.Void, [], new IrExprStmt(new IrNull(IrType.Object(other)))));
    Refused(module, "the type Other is not among the types of the module");
  }

  /// <summary>The members of a class must fit the functions they name.</summary>
  [Test]
  public void Verifier_RefusesMembersThatDoNotFitTheirFunctions() {
    void Refused(Built built, string message, params IrFunction[] functions) => Assert.That(Problems(built.Module(functions)), Has.Some.Contains(message));

    var assigned = new Built();
    var self = assigned.Functions[1].Parameters[0];
    assigned.Functions[1] = assigned.Functions[1] with { Body = new IrBlock([new IrAssign(self, new IrNull(assigned.Object)), new IrReturn(new IrConst(IrType.I32, 0))]) };
    Refused(assigned, "the receiver of a method is assigned");

    var unwritten = new Built(new IrBlock([]));
    Refused(unwritten, "does not give the read-only field 'a' an array");

    var missing = new Built(extra: [new IrMethod("Gone", "K.Gone", false)]);
    Refused(missing, "the function 'K.Gone' of a member does not exist");

    var twice = new Built(extra: [new IrMethod("Again", "K.Get", false)]);
    Refused(twice, "the function 'K.Get' is the body of two members");

    var getter = new IrMethod("P", "K.get_P", false) { Kind = IrMethodKind.Getter };
    var setter = new IrMethod("P", "K.set_P", false) { Kind = IrMethodKind.Setter };
    var shapes = new Built(properties: [new IrProperty("P", IrType.I32) { Getter = getter, Setter = setter }], extra: [getter, setter]);
    var receiver = shapes.This();
    Refused(shapes, "the getter 'K.get_P' takes a value or returns none",
      new IrFunction("K.get_P", false, IrType.Void, [receiver], [], new IrBlock([])),
      new IrFunction("K.set_P", false, IrType.I32, [receiver], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 0))])));
    Refused(shapes, "the setter 'K.set_P' does not take one value and return nothing",
      new IrFunction("K.get_P", false, IrType.Void, [receiver], [], new IrBlock([])),
      new IrFunction("K.set_P", false, IrType.I32, [receiver], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 0))])));

    var stranger = new IrMethod("S", "K.S", false);
    var foreign = new Built(properties: [new IrProperty("Q", IrType.I32) { BackingField = new IrField("q", IrType.I32, false), Getter = stranger }, new IrProperty("E", IrType.I32)]);
    Refused(foreign, "the property 'Q' is backed by a field that is not one of its class's own of its type");
    Refused(foreign, "an accessor of the property 'Q' is not a method of K");
    Refused(foreign, "the property 'E' has neither a field nor an accessor");

    var statics = new Built(statics: [new IrGlobal("K.s", IrType.I32, null)]);
    Refused(statics, "the static field 'K.s' is not a global of the module");

    var badReceiver = new Built();
    badReceiver.Functions[1] = badReceiver.Functions[1] with { Parameters = [new IrLocal("this", IrType.I32)] };
    Refused(badReceiver, "does not take the receiver of K first");

    var valued = new Built();
    valued.Functions[0] = valued.Functions[0] with { ReturnType = IrType.I32 };
    Refused(valued, "the constructor 'K.ctor' returns a value");

    var objectGlobal = new Built();
    // A global holds an object (#1365): what the initializer of the module puts there.
    Assert.That(Problems(objectGlobal.Module() with { Globals = [new IrGlobal("g", objectGlobal.Object, null)] }), Has.None.Contains("a global of the type K"));
    Assert.That(Problems(objectGlobal.Module() with { Globals = [new IrGlobal("g", IrType.Span(IrType.I32), null)] }), Has.Some.Contains("a global of the type"));
  }

  /// <summary>Test design 9: a null receiver traps on a field read, a field write, a method call and an array field without an array.</summary>
  [Test]
  public void NullReceiver_TrapsBeforeTheArgumentsOfACallButAfterTheValueOfAStore() {
    var built = new Built(new IrBlock([]));
    var counter = new IrGlobal("hits", IrType.I32, null);
    var hit = new IrFunction("Hit", false, IrType.I32, [], [], new IrBlock([
      new IrGlobalAssign(counter, new IrBinary(IrBinaryOp.Add, new IrGlobalRef(counter), new IrConst(IrType.I32, 1))),
      new IrReturn(new IrConst(IrType.I32, 1))
    ]));
    IrExpr Hit() => new IrCall("Hit", IrType.I32, []);
    var setter = new IrMethod("V", "K.set_V", false) { Kind = IrMethodKind.Setter };
    var withSetter = new Built(new IrBlock([]), properties: [new IrProperty("V", IrType.I32) { Setter = setter }], extra: [setter]);
    var receiver = withSetter.This();
    var value = new IrLocal("value", IrType.I32);
    var setterBody = new IrFunction("K.set_V", false, IrType.Void, [receiver, value], [], new IrBlock([new IrFieldStore(new IrLocalRef(receiver), withSetter.Value, new IrLocalRef(value))]));
    var functions = new[] {
      hit,
      new IrFunction("Call", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrMethodCall(IrType.I32, withSetter.Get, new IrNull(withSetter.Object), []))])),
      new IrFunction("Store", true, IrType.Void, [], [], new IrBlock([new IrFieldStore(new IrNull(withSetter.Object), withSetter.Value, Hit())])),
      new IrFunction("Set", true, IrType.Void, [], [], new IrBlock([new IrExprStmt(new IrMethodCall(IrType.Void, setter, new IrNull(withSetter.Object), [Hit()]))])),
      new IrFunction("Read", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrFieldLoad(new IrNull(withSetter.Object), withSetter.Value))])),
      new IrFunction("Length", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrArrayLength(new IrFieldLoad(new IrNew(withSetter.Object, withSetter.Constructor, [new IrConst(IrType.I32, 0)]), withSetter.Cells)))])),
      new IrFunction("Element", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrArrayLoad(new IrFieldLoad(new IrNew(withSetter.Object, withSetter.Constructor, [new IrConst(IrType.I32, 0)]), withSetter.Cells), new IrConst(IrType.I32, 0)))])),
      new IrFunction("Hits", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrGlobalRef(counter))]))
    };
    var module = withSetter.Module([setterBody, .. functions]) with { Globals = [counter] };
    // A constructor that leaves the array field without an array is refused; the trap is for the interpreter's own sake here.
    Assert.That(Problems(module), Has.All.Contains("does not give the read-only field 'a' an array"));
    var instance = IrInterpreter.Instantiate(module);
    foreach (var name in new[] { "Call", "Store", "Set", "Read", "Length", "Element" })
      Assert.That(() => instance.Call(name), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.NullReference), name);

    // The store and the setter evaluated their value before they trapped.
    Assert.That(instance.Call("Hits"), Is.EqualTo(2));
    Assert.That(IrTrapException.Describe(IrTrap.NullReference), Is.EqualTo("null reference"));
    _ = built;
  }

  /// <summary>Objects cross the boundary as themselves, <c>null</c> as null; another class's object is no argument.</summary>
  [Test]
  public void Objects_CrossTheBoundaryAsThemselves() {
    var built = new Built();
    var given = new IrLocal("k", built.Object);
    var module = built.Module(
      new IrFunction("Make", true, built.Object, [], [], new IrBlock([new IrReturn(new IrMethodCall(built.Object, built.Make, null, []))])),
      new IrFunction("Read", true, IrType.I32, [given], [], new IrBlock([new IrReturn(new IrMethodCall(IrType.I32, built.Get, new IrLocalRef(given), []))])),
      new IrFunction("Nothing", true, built.Object, [], [], new IrBlock([new IrReturn(new IrNull(built.Object))])),
      new IrFunction("IsNull", true, IrType.Bool, [given], [], new IrBlock([new IrReturn(new IrReferenceEquals(new IrLocalRef(given), new IrNull(built.Object)))])));
    Assert.That(Problems(module), Is.Empty);
    var instance = IrInterpreter.Instantiate(module);
    var made = instance.Call("Make");
    Assert.That(made, Is.Not.Null);
    Assert.That(instance.Call("Read", made!), Is.EqualTo(7));
    Assert.That(instance.Call("Nothing"), Is.Null);
    Assert.That(IrInterpreter.Call(module, "IsNull", [null!]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "IsNull", [made!]), Is.EqualTo(false));
    Assert.That(() => IrInterpreter.Call(module, "Read", [5]), Throws.ArgumentException);
  }

  /// <summary>A record compares field by field, a record field of it as a record, and two nulls as equal.</summary>
  [Test]
  public void RecordEquality_IsFieldByFieldAndNullSafe() {
    var inner = new IrClassType("In") { IsRecord = true };
    var outer = new IrClassType("Out") { IsRecord = true };
    var x = new IrField("x", IrType.F64, true);
    var link = new IrField("link", IrType.Object(inner), true);
    inner.Define([x]);
    outer.Define([link]);
    var makeIn = new IrConstructor("In.ctor");
    var makeOut = new IrConstructor("Out.ctor");
    inner.DefineMembers([makeIn], [], [], []);
    outer.DefineMembers([makeOut], [], [], []);
    var a = new IrLocal("this", IrType.Object(inner));
    var av = new IrLocal("x", IrType.F64);
    var b = new IrLocal("this", IrType.Object(outer));
    var bv = new IrLocal("link", IrType.Object(inner));
    IrExpr In(double value) => new IrNew(IrType.Object(inner), makeIn, [new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(value))]);
    IrExpr Out(IrExpr value) => new IrNew(IrType.Object(outer), makeOut, [value]);
    IrFunction Compare(string name, IrExpr left, IrExpr right) => new(name, true, IrType.Bool, [], [], new IrBlock([new IrReturn(new IrStructEquals(left, right))]));
    var module = new IrModule("M", [
      new IrFunction("In.ctor", false, IrType.Void, [a, av], [], new IrBlock([new IrFieldStore(new IrLocalRef(a), x, new IrLocalRef(av))])),
      new IrFunction("Out.ctor", false, IrType.Void, [b, bv], [], new IrBlock([new IrFieldStore(new IrLocalRef(b), link, new IrLocalRef(bv))])),
      Compare("Same", Out(In(double.NaN)), Out(In(double.NaN))),
      Compare("Differ", Out(In(1)), Out(In(2))),
      Compare("Nulls", new IrNull(IrType.Object(outer)), new IrNull(IrType.Object(outer))),
      Compare("Half", Out(In(1)), new IrNull(IrType.Object(outer))),
      Compare("InnerNull", Out(new IrNull(IrType.Object(inner))), Out(In(1)))
    ]) { Types = [inner, outer] };
    Assert.That(Problems(module), Is.Empty);
    Assert.That(new[] { "Same", "Differ", "Nulls", "Half", "InnerNull" }.Select(name => IrInterpreter.Call(module, name, [])), Is.EqualTo(new object[] { true, false, true, false, false }));
  }

  /// <summary>A class gives its members once and finds the property of a field or an accessor.</summary>
  [Test]
  public void ClassDeclaration_DefinesItsMembersOnce() {
    var built = new Built();
    Assert.That(() => built.Type.DefineMembers([], [], [], []), Throws.InvalidOperationException);
    Assert.That(built.Type.HasMembers, Is.True);
    Assert.That(built.Type.Functions, Is.EqualTo(new[] { "K.ctor", "K.Get", "K.Make" }));
    Assert.That(built.Type.PropertyOf(built.Value), Is.Null);
    Assert.That(built.Type.PropertyOf(built.Get), Is.Null);
    Assert.That((built.Object.IsObject, built.Object.ClassType, built.Object.ToString(), built.Object.IsStorable), Is.EqualTo((true, built.Type, "K", true)));
  }

}
