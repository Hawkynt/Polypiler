using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Objects;

/// <summary>
/// Inheritance, interfaces, dispatch and type tests of the IR on modules built by hand: the rules of
/// the verifier about hierarchies and calls, the constructor rule, and what the interpreter selects.
/// </summary>
[TestFixture]
public sealed class IrDispatchTests {

  /// <summary>
  /// An interface I with a method N; an abstract class A : I with a field f, a virtual method M, an
  /// abstract method N and a constructor; a class B : A with a field g, an override of M and of N;
  /// each function of a member as its name says.
  /// </summary>
  private sealed class Built {

    public IrInterfaceType I { get; } = new("I");

    public IrClassType A { get; }

    public IrClassType B { get; } = new("B");

    public IrField F { get; } = new("f", IrType.I32, false);

    public IrField G { get; } = new("g", IrType.I32, false);

    public IrMethod IN { get; } = new("N", "I.N", false) { Dispatch = IrDispatch.Abstract };

    public IrMethod AM { get; }

    public IrMethod AN { get; }

    public IrMethod BM { get; }

    public IrMethod BN { get; }

    /// <summary>A method of B that calls M virtually on its receiver.</summary>
    public IrMethod Extra { get; } = new("X", "B.X", false);

    public IrConstructor ACtor { get; } = new("A.ctor");

    public IrConstructor BCtor { get; } = new("B.ctor");

    public List<IrFunction> Functions { get; } = [];

    public IrType OfA => IrType.Object(this.A);

    public IrType OfB => IrType.Object(this.B);

    public IrType OfI => IrType.Object(this.I);

    public Built(bool abstractA = true, IrType? bmResult = null, IrDispatch amDispatch = IrDispatch.Virtual, bool implementN = true, IrBlock? bCtorBody = null,
        bool sealedA = false, IReadOnlyList<IrMethod>? extraB = null, IReadOnlyList<IrFunction>? extraFunctions = null, bool concreteA = false) {
      abstractA &= !concreteA;
      this.A = new IrClassType("A") { IsAbstract = abstractA, IsSealed = sealedA };
      this.AM = new IrMethod("M", "A.M", false) { Dispatch = amDispatch };
      this.AN = new IrMethod("N", "A.N", false) { Dispatch = concreteA ? IrDispatch.Virtual : IrDispatch.Abstract, Implements = implementN ? [this.IN] : [] };
      this.BM = new IrMethod("M", "B.M", false) { Dispatch = IrDispatch.Override, Overrides = this.AM };
      this.BN = new IrMethod("N", "B.N", false) { Dispatch = IrDispatch.Override, Overrides = this.AN };
      this.I.DefineMembers([this.IN], [], []);
      this.A.Define([this.F]);
      this.B.Define([this.G]);
      this.A.Inherit(null, [this.I]);
      this.B.Inherit(this.A, []);
      this.A.DefineMembers([this.ACtor], [this.AM, this.AN], [], []);
      this.B.DefineMembers([this.BCtor], [this.BM, this.BN, this.Extra, .. extraB ?? []], [], []);
      IrLocal Self(IrType type) => new("this", type);
      IrFunction Returning(string name, IrType receiver, IrType result, long value) {
        var self = Self(receiver);
        return new(name, false, result, [self], [], new IrBlock([new IrReturn(new IrConst(result, value))]));
      }

      var aSelf = Self(this.OfA);
      var bSelf = Self(this.OfB);
      this.Functions.AddRange([
        new IrFunction("I.N", false, IrType.I32, [Self(this.OfI)], [], new IrBlock([])),
        new IrFunction("A.ctor", false, IrType.Void, [aSelf], [], new IrBlock([new IrFieldStore(new IrLocalRef(aSelf), this.F, new IrConst(IrType.I32, 1))])),
        Returning("A.M", this.OfA, IrType.I32, 10),
        concreteA ? Returning("A.N", this.OfA, IrType.I32, 5) : new IrFunction("A.N", false, IrType.I32, [Self(this.OfA)], [], new IrBlock([])),
        new IrFunction("B.ctor", false, IrType.Void, [bSelf], [], bCtorBody?.Statements is { } given ? new IrBlock(given) : new IrBlock([
          new IrExprStmt(new IrCall("A.ctor", IrType.Void, [new IrLocalRef(bSelf)])),
          new IrFieldStore(new IrLocalRef(bSelf), this.G, new IrConst(IrType.I32, 2))
        ])),
        Returning("B.M", this.OfB, bmResult ?? IrType.I32, 20),
        Returning("B.N", this.OfB, IrType.I32, 30),
        new IrFunction("B.X", false, IrType.I32, [bSelf], [], new IrBlock([new IrReturn(new IrMethodCall(IrType.I32, this.AM, new IrLocalRef(bSelf), []) { Kind = IrMethodCallKind.Virtual })])),
        .. extraFunctions ?? []
      ]);
    }

    public IrModule Module(params IrFunction[] functions) => new("M", [.. this.Functions, .. functions]) { Types = [this.I, this.A, this.B] };

    public IrNew NewB() => new(this.OfB, this.BCtor, []);

  }

  private static IrFunction Function(IrType result, IReadOnlyList<IrLocal> locals, params IrStmt[] statements) => new("F", true, result, [], locals, new IrBlock(statements));

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static void Refused(IrModule module, string message) => Assert.That(Problems(module), Has.Some.Contains(message), string.Join("\n", Problems(module)));

  /// <summary>Test design 2: direct, virtual, interface and base calls select their methods; fields of the base class come first.</summary>
  [Test]
  public void Calls_SelectTheirMethods() {
    var built = new Built();
    var a = new IrLocal("a", built.OfA);
    var i = new IrLocal("i", built.OfI);
    IrExpr Call(IrMethod method, IrExpr target, IrMethodCallKind kind) => new IrMethodCall(IrType.I32, method, target, []) { Kind = kind };
    var module = built.Module(Function(IrType.I32, [a, i],
      new IrDeclare(a, built.NewB()),
      new IrDeclare(i, new IrLocalRef(a)),
      new IrReturn(new IrBinary(IrBinaryOp.Add,
        new IrBinary(IrBinaryOp.Add, Call(built.AM, new IrLocalRef(a), IrMethodCallKind.Virtual), Call(built.IN, new IrLocalRef(i), IrMethodCallKind.Interface)),
        new IrBinary(IrBinaryOp.Add, new IrFieldLoad(new IrLocalRef(a), built.F), new IrFieldLoad(new IrCast(built.OfB, new IrLocalRef(a)), built.G))))));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(20 + 30 + 1 + 2));
    Assert.That(built.B.AllFields, Is.EqualTo(new[] { built.F, built.G }));
    Assert.That((built.B.OwnerOf(built.F), built.B.OwnerOf(built.AM), built.B.OwnerOf(built.BM), built.A.OwnerOf(built.BM)), Is.EqualTo((built.A, built.A, built.B, (IrClassType?)null)));
    Assert.That((built.B.Select(built.AM), built.A.Select(built.AN), built.B.Implementation(built.IN), built.BM.Root), Is.EqualTo((built.BM, built.AN, built.AN, built.AM)));
    Assert.That(built.B.AllInterfaces, Is.EqualTo(new[] { built.I }));
  }

  /// <summary>Test design 5: is, as and a cast against a class and an interface, for an object of the type, of a subclass, of an unrelated class and null.</summary>
  [Test]
  public void TypeTests_FollowTheHierarchy() {
    var built = new Built(concreteA: true);
    var other = new IrClassType("U");
    other.Define([]);
    other.Inherit(null, []);
    var otherCtor = new IrConstructor("U.ctor");
    other.DefineMembers([otherCtor], [], [], []);
    var uSelf = new IrLocal("this", IrType.Object(other));
    var functions = new List<IrFunction> { new("U.ctor", false, IrType.Void, [uSelf], [], new IrBlock([])) };
    // A function per operand and kind of test: the operand is made by the case number.
    IrExpr Operand(int which) => which switch {
      0 => new IrNew(built.OfA, built.ACtor, []),
      1 => built.NewB(),
      _ => new IrNull(built.OfA)
    };

    var cases = new (string Name, IrType Result, Func<IrExpr, IrExpr> Body)[] {
      ("IsB", IrType.Bool, value => new IrTypeTest(value, built.OfB)),
      ("IsI", IrType.Bool, value => new IrTypeTest(value, built.OfI)),
      ("AsB", IrType.Bool, value => new IrReferenceEquals(new IrAsCast(built.OfB, value), new IrNull(built.OfB))),
      ("CastB", IrType.Bool, value => new IrReferenceEquals(new IrCast(built.OfB, value), new IrNull(built.OfB))),
      ("CastI", IrType.Bool, value => new IrReferenceEquals(new IrCast(built.OfI, value), new IrNull(built.OfI)))
    };
    foreach (var (name, result, body) in cases)
      for (var which = 0; which < 3; ++which)
        functions.Add(new IrFunction($"{name}{which}", true, result, [], [], new IrBlock([new IrReturn(body(Operand(which)))])));

    functions.Add(new IrFunction("IsU", true, IrType.Bool, [], [], new IrBlock([new IrReturn(new IrTypeTest(new IrNew(IrType.Object(other), otherCtor, []), built.OfI))])));
    var module = new IrModule("M", [.. built.Functions, .. functions]) { Types = [built.I, built.A, built.B, other] };
    Assert.That(Problems(module), Is.Empty);
    string Outcome(string function) {
      try {
        return IrInterpreter.Call(module, function, [])!.ToString()!;
      } catch (IrTrapException trap) {
        return IrTrapException.Describe(trap.Trap);
      }
    }

    Assert.That(Enumerable.Range(0, 3).Select(which => Outcome($"IsB{which}")), Is.EqualTo(new[] { "False", "True", "False" }));
    Assert.That(Enumerable.Range(0, 3).Select(which => Outcome($"IsI{which}")), Is.EqualTo(new[] { "True", "True", "False" }));
    Assert.That(Enumerable.Range(0, 3).Select(which => Outcome($"AsB{which}")), Is.EqualTo(new[] { "True", "False", "True" }));
    Assert.That(Enumerable.Range(0, 3).Select(which => Outcome($"CastB{which}")), Is.EqualTo(new[] { "invalid cast", "False", "True" }));
    Assert.That(Enumerable.Range(0, 3).Select(which => Outcome($"CastI{which}")), Is.EqualTo(new[] { "False", "False", "True" }));
    Assert.That(Outcome("IsU"), Is.EqualTo("False"));
  }

  /// <summary>The upcast is implicit: a reference converts to its base class and its interfaces, never the other way, and nothing else does.</summary>
  [Test]
  public void Assignability_FollowsTheHierarchy() {
    var built = new Built();
    var other = new IrInterfaceType("J");
    other.DefineMembers([], [], [built.I]);
    Assert.That(built.OfB.IsAssignableTo(built.OfA) && built.OfB.IsAssignableTo(built.OfI) && built.OfA.IsAssignableTo(built.OfA) && IrType.Object(other).IsAssignableTo(built.OfI), Is.True);
    Assert.That(built.OfA.IsAssignableTo(built.OfB) || built.OfI.IsAssignableTo(built.OfA) || built.OfI.IsAssignableTo(IrType.Object(other)) || IrType.I32.IsAssignableTo(IrType.I64)
      || built.OfA.IsAssignableTo(IrType.I32) || built.OfA.IsAssignableTo(null), Is.False);
    Assert.That(other.Closure, Is.EqualTo(new[] { other, built.I }));
    Assert.That(other.AllMethods, Is.EqualTo(new[] { built.IN }));
  }

  /// <summary>A hierarchy and the members of an interface are given once.</summary>
  [Test]
  public void Declarations_AreGivenOnce() {
    var built = new Built();
    Assert.Throws<InvalidOperationException>(() => built.B.Inherit(null, []));
    Assert.Throws<InvalidOperationException>(() => built.I.DefineMembers([], [], []));
    var property = new IrProperty("P", IrType.I32) { Getter = built.IN };
    var contract = new IrInterfaceType("K");
    contract.DefineMembers([built.IN], [property], []);
    Assert.That((contract.PropertyOf(built.IN), contract.PropertyOf(built.AM), contract.Visibility, contract.HasMembers), Is.EqualTo((property, (IrProperty?)null, IrVisibility.Public, true)));
  }

  /// <summary>A cycle of classes or of interfaces ends the walk through it at its first repetition.</summary>
  [Test]
  public void Cycles_EndTheirWalks() {
    var (first, second) = (new IrClassType("X"), new IrClassType("Y"));
    first.Inherit(second, []);
    second.Inherit(first, []);
    Assert.That(first.Lineage, Is.EqualTo(new[] { first, second }));
    var (one, two) = (new IrInterfaceType("P"), new IrInterfaceType("Q"));
    one.DefineMembers([], [], [two]);
    two.DefineMembers([], [], [one]);
    Assert.That(one.Closure, Is.EqualTo(new[] { one, two }));
  }

  /// <summary>Test design 11: what the verifier refuses of hierarchies, one rule each.</summary>
  [Test]
  public void Verifier_RefusesEveryBrokenRule() {
    Refused(new Built(bmResult: IrType.I64).Module(), "the method 'M' overrides no virtual method of a base class with its name and signature");
    Refused(new Built(abstractA: false).Module(), "the abstract method 'N' is one of a class that is not abstract");
    Refused(new Built(implementN: false).Module(), "the class does not implement the method 'N' of an interface");
    Refused(new Built(amDispatch: IrDispatch.Instance).Module(), "the method 'M' overrides no virtual method");
    Refused(new Built(sealedA: true).Module(), "the class derives from the sealed class A");
    Refused(new Built(bCtorBody: new IrBlock([])).Module(), "the constructor does not begin with the call of a constructor of A or of B");
    var unimplemented = new Built();
    var bare = new IrClassType("C");
    bare.Define([]);
    bare.Inherit(unimplemented.A, []);
    var bareCtor = new IrConstructor("C.ctor");
    bare.DefineMembers([bareCtor], [], [], []);
    var cSelf = new IrLocal("this", IrType.Object(bare));
    Refused(new IrModule("M", [.. unimplemented.Functions, new IrFunction("C.ctor", false, IrType.Void, [cSelf], [], new IrBlock([new IrExprStmt(new IrCall("A.ctor", IrType.Void, [new IrLocalRef(cSelf)]))]))]) {
      Types = [unimplemented.I, unimplemented.A, unimplemented.B, bare]
    }, "the class does not implement the abstract method 'N'");

    var (x, y) = (new IrClassType("X"), new IrClassType("Y"));
    foreach (var (type, other) in new[] { (x, y), (y, x) }) {
      type.Define([]);
      type.Inherit(other, []);
      type.DefineMembers([], [], [], []);
    }

    Refused(new IrModule("M", []) { Types = [x, y] }, "the class derives from itself");
    var (p, q) = (new IrInterfaceType("P"), new IrInterfaceType("Q"));
    p.DefineMembers([], [], [q]);
    q.DefineMembers([], [], [p]);
    Refused(new IrModule("M", []) { Types = [p, q] }, "the interface extends itself");
    var outside = new IrInterfaceType("Z");
    outside.DefineMembers([], [], []);
    var lone = new IrInterfaceType("L");
    lone.DefineMembers([new IrMethod("V", "L.V", false)], [], [outside]);
    var stray = new IrClassType("S");
    stray.Define([]);
    stray.Inherit(new IrClassType("Missing"), [outside]);
    stray.DefineMembers([], [
      new IrMethod("St", "S.St", true) { Dispatch = IrDispatch.Instance },
      new IrMethod("Ov", "S.Ov", false) { Overrides = new IrMethod("Ov", "S.Ov", false), IsSealedOverride = true },
      new IrMethod("Im", "S.Im", false) { Implements = [new IrMethod("Q", "L.V", false)] },
      new IrMethod("Ge", "S.Ge", false) { Kind = IrMethodKind.Getter, Dispatch = IrDispatch.Virtual }
    ], [], []);
    var problems = Problems(new IrModule("M", [new IrFunction("L.V", false, IrType.I32, [new IrLocal("this", IrType.I32)], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]))]) {
      Types = [lone, stray]
    });
    Assert.That(problems, Has.Some.Contains("the method 'V' of an interface is not abstract"));
    Assert.That(problems, Has.Some.Contains("the function 'L.V' does not take the receiver of L first"));
    Assert.That(problems, Has.Some.Contains("the abstract method 'V' has a body"));
    Assert.That(problems, Has.Some.Contains("the interface Z is not among the types of the module"));
    Assert.That(problems, Has.Some.Contains("the base class Missing is not among the types of the module"));
    Assert.That(problems, Has.Some.Contains("the method 'St' has a receiver and the dispatch Instance"));
    Assert.That(problems, Has.Some.Contains("the method 'Ov' names an overridden method without being an override"));
    Assert.That(problems, Has.Some.Contains("the method 'Im' implements no method of an interface of S with its signature"));
    Assert.That(problems, Has.Some.Contains("the accessor 'Ge' has the dispatch Virtual"));
  }

  /// <summary>Test design 11: the verifier's rules about calls, new and conversions in a hierarchy.</summary>
  [Test]
  public void Verifier_RefusesBrokenCalls() {
    var built = new Built();
    var a = new IrLocal("a", built.OfA);
    IrModule With(IrExpr expression) => built.Module(Function(IrType.Void, [a], new IrDeclare(a, new IrNull(built.OfA)), new IrExprStmt(expression)));
    IrMethodCall Call(IrMethod method, IrExpr target, IrMethodCallKind kind) => new(IrType.I32, method, target, []) { Kind = kind };
    Refused(With(Call(built.AM, new IrLocalRef(a), IrMethodCallKind.Direct)), "the virtual method 'M' is called directly");
    Refused(With(Call(built.IN, new IrLocalRef(a), IrMethodCallKind.Virtual)), "the method 'N', which is no virtual method of a class, is called virtually");
    Refused(With(Call(built.AM, new IrLocalRef(a), IrMethodCallKind.Interface)), "the method 'M', which is no method of an interface, is called through one");
    Refused(With(Call(built.AM, new IrLocalRef(a), IrMethodCallKind.Base)), "the method 'M' is no method with a body of a base class called on the receiver");
    Refused(With(Call(built.BM, new IrLocalRef(a), IrMethodCallKind.Virtual)), "the method 'M' of B is called on the type A");
    Refused(With(new IrNew(built.OfA, built.ACtor, [])), "an object of the abstract class A is made");
    Refused(With(new IrCall("I.N", IrType.I32, [new IrNull(built.OfI)])), "the signature 'I.N' of an abstract method is called");
    Refused(With(new IrTypeTest(new IrConst(IrType.I32, 1), built.OfA)), "a reference to an object is expected, not the type i32");
    Refused(With(new IrCast(IrType.I32, new IrLocalRef(a))), "a reference to an object is expected, not the type i32");
    Refused(With(new IrAsCast(IrType.I32, new IrLocalRef(a))), "a reference to an object is expected, not the type i32");
    Refused(With(new IrFieldLoad(new IrNull(built.OfI), built.F)), "a reference to an object of a class is expected, not the type I");
    var u = new IrClassType("U");
    u.Define([]);
    u.Inherit(null, []);
    u.DefineMembers([], [], [], []);
    Refused(new IrModule("M", [.. built.Functions, Function(IrType.Bool, [], new IrReturn(new IrReferenceEquals(new IrNull(built.OfA), new IrNull(IrType.Object(u)))))]) { Types = [built.I, built.A, built.B, u] },
      "a reference to A is compared with one of the type U");
  }

  /// <summary>Test design 8 and 11: a virtual call on the object being constructed, directly, through a callee, a local, a cast or an argument, is refused; one on another object is not.</summary>
  [Test]
  public void ConstructionRule_FollowsTheObjectThroughItsCallees() {
    // The constructor of B calls A.ctor and then, in each case, reaches a virtual call on its object.
    var cases = new Dictionary<string, Func<Built, IrLocal, IrStmt[]>> {
      ["direct"] = (built, self) => [new IrExprStmt(new IrMethodCall(IrType.I32, built.AM, new IrLocalRef(self), []) { Kind = IrMethodCallKind.Virtual })],
      ["through a cast in a nested block"] = (built, self) => [new IrIf(new IrConst(IrType.Bool, 1), new IrBlock([new IrExprStmt(new IrMethodCall(IrType.I32, built.IN, new IrCast(built.OfI, new IrLocalRef(self)), []) {
        Kind = IrMethodCallKind.Interface })]), null)],
      ["through a helper and a local"] = (_, self) => [new IrExprStmt(new IrCall("Help", IrType.I32, [new IrLocalRef(self)]))],
      ["through a direct call"] = (built, self) => [new IrExprStmt(new IrMethodCall(IrType.I32, built.Extra, new IrLocalRef(self), []))]
    };
    foreach (var (name, body) in cases) {
      var built = new Built();
      var self = new IrLocal("this", built.OfB);
      var helperSelf = new IrLocal("x", built.OfA);
      var held = new IrLocal("h", built.OfA);
      var helper = new IrFunction("Help", false, IrType.I32, [helperSelf], [held], new IrBlock([
        new IrDeclare(held, new IrLocalRef(helperSelf)),
        new IrAssign(held, new IrLocalRef(helperSelf)),
        new IrReturn(new IrMethodCall(IrType.I32, built.AM, new IrLocalRef(held), []) { Kind = IrMethodCallKind.Virtual })
      ]));
      var module = new IrModule("M", [.. built.Functions.Where(function => function.Name != "B.ctor"), helper,
        new IrFunction("B.ctor", false, IrType.Void, [self], [], new IrBlock([new IrExprStmt(new IrCall("A.ctor", IrType.Void, [new IrLocalRef(self)])), .. body(built, self)]))]) {
        Types = [built.I, built.A, built.B]
      };
      Assert.That(IrConstruction.VirtualCalls(module).Select(found => found.Constructor), Is.EqualTo(new[] { "B.ctor" }), name);
      Refused(module, "on 'this' during construction");
    }

    var clean = new Built();
    var other = new IrLocal("o", clean.OfA);
    var cleanSelf = new IrLocal("this", clean.OfB);
    var accepted = new IrModule("M", [.. clean.Functions.Where(function => function.Name != "B.ctor"),
      new IrFunction("B.ctor", false, IrType.Void, [cleanSelf], [other], new IrBlock([
        new IrExprStmt(new IrCall("A.ctor", IrType.Void, [new IrLocalRef(cleanSelf)])),
        new IrDeclare(other, new IrNull(clean.OfA)),
        new IrIf(new IrConst(IrType.Bool, 0), new IrBlock([new IrExprStmt(new IrMethodCall(IrType.I32, clean.AM, new IrLocalRef(other), []) { Kind = IrMethodCallKind.Virtual })]), null)
      ]))]) { Types = [clean.I, clean.A, clean.B] };
    Assert.That(IrConstruction.VirtualCalls(accepted), Is.Empty);
    Assert.That(Problems(accepted), Is.Empty);
  }

  /// <summary>The walkers of the IR see the operand of every conversion.</summary>
  [Test]
  public void Walkers_SeeTheOperandsOfConversions() {
    var built = new Built();
    var value = new IrNull(built.OfA);
    foreach (IrExpr conversion in new IrExpr[] { new IrTypeTest(value, built.OfB), new IrCast(built.OfB, value), new IrAsCast(built.OfB, value) }) {
      Assert.That(IrTree.Operands(conversion), Is.EqualTo(new[] { value }));
      Assert.That(Polypiler.Core.Recognize.IrShape.Operands(conversion), Is.EqualTo(new[] { value }));
      Assert.That(Polypiler.Core.Recognize.IrShape.With(conversion, [new IrNull(built.OfA)]), Is.EqualTo(conversion));
    }
  }

}
