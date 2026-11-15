using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Precomputed.Support;
using static Polypiler.Tests.Precomputed.IrModuleFormatTests;

namespace Polypiler.Tests.Precomputed;

/// <summary>
/// <see cref="IrModuleFormat"/> keeps what came after the first IR: classes and interfaces with every
/// part of their declarations, optional values, generics with their templates, exceptions and the
/// classes of the library, resource scopes, collections and function values; and it refuses a module
/// that names a part of them it does not own, and bytes that name one that does not exist.
/// </summary>
[TestFixture]
public sealed class IrModuleFormatFeatureTests {

  private static readonly IrType _i32 = IrType.I32;

  /// <summary>The parts of <see cref="Sink"/> that the tests look at after a round trip.</summary>
  private sealed record Parts(IrModule Module, IrClassType Square, IrMethod SquareArea, IrGenericTemplate Template);

  /// <summary>A module with every kind of declaration, member and node that classes, generics and the later features brought.</summary>
  private static Parts Sink() {
    var shape = new IrInterfaceType("IShape") { Visibility = IrVisibility.Internal };
    var area = new IrMethod("Area", "IShape.Area", false) { Dispatch = IrDispatch.Abstract, Kind = IrMethodKind.Getter };
    shape.DefineMembers([area], [new IrProperty("Area", IrType.F64) { Getter = area }], []);
    var named = new IrInterfaceType("INamed");
    named.DefineMembers([], [], [shape]);
    var opaque = new IrInterfaceType("IOpaque");
    var (disposable, dispose, signature) = IrDisposable.Declare();

    var side = new IrField("side", IrType.F64, false) { Visibility = IrVisibility.Private };
    var @base = new IrClassType("Shape") { IsAbstract = true, Visibility = IrVisibility.Internal };
    @base.Define([side]);
    @base.Inherit(null, [shape, disposable]);
    var baseArea = new IrMethod("Area", "Shape.Area", false) { Dispatch = IrDispatch.Virtual, Implements = [area], Kind = IrMethodKind.Getter };
    var baseDispose = new IrMethod("Dispose", "Shape.Dispose", false) { Implements = [dispose] };
    var setSide = new IrMethod("set_Side", "Shape.set_Side", false) { Kind = IrMethodKind.Setter, Visibility = IrVisibility.Protected };
    var count = new IrGlobal("Shape.count", _i32, new IrConst(_i32, 0));
    @base.DefineMembers([new IrConstructor("Shape..ctor") { Visibility = IrVisibility.Protected }], [baseArea, baseDispose, setSide], [
      new IrProperty("Side", IrType.F64) { BackingField = side, Setter = setSide, HasSetter = true, IsInitOnly = true, Visibility = IrVisibility.Internal }
    ], [count]);

    var square = new IrClassType("Square") { IsSealed = true, IsRecord = true };
    square.Define([]);
    square.Inherit(@base, [named]);
    var squareArea = new IrMethod("Area", "Square.Area", false) { Dispatch = IrDispatch.Override, Overrides = baseArea, IsSealedOverride = true, Kind = IrMethodKind.Getter };
    var make = new IrMethod("Make", "Square.Make", true) { Visibility = IrVisibility.Private };
    var squareConstructor = new IrConstructor("Square..ctor");
    square.DefineMembers([squareConstructor], [squareArea, make], [], []);
    var pending = new IrClassType("Pending");

    var shapeType = IrType.Object(@base);
    var squareType = IrType.Object(square);
    var box = new IrClassType("Box<i32>") {
      IsSealed = true,
      Origin = new IrGenericOrigin("Box", ["T"], [_i32], [
        new IrGenericConstraint("T", IrGenericConstraintKind.Struct),
        new IrGenericConstraint("T", IrGenericConstraintKind.Interface) { Interface = IrType.Object(shape) }
      ])
    };
    box.Define([new IrField("Value", squareType, false)]);
    box.Inherit(null, []);
    box.DefineMembers([], [], [], []);
    var color = new IrEnumType("Color", IrType.U8, [new IrEnumMember("Red", 1)]);

    // A template over a type parameter that no module declares, shared by two instantiations.
    var parameter = new IrInterfaceType("T");
    parameter.DefineMembers([], [], []);
    var open = IrType.Object(parameter);
    var given = new IrLocal("a", open);
    var templateFunction = new IrFunction("Id", true, open, [given], [], new IrBlock([
      new IrIf(new IrIsNull(new IrLocalRef(given)), new IrBlock([new IrReturn(new IrNew(open, IrGenerics.Parameterless, []))]), null),
      new IrReturn(new IrLocalRef(given))
    ]));
    var template = new IrGenericTemplate(templateFunction, [open]) { Calls = new Dictionary<string, IrGenericCall> { ["Id"] = new("Id", [open]), ["Other"] = new("Other", []) } };
    IrFunction Instance(IrType argument) {
      var value = new IrLocal("a", argument);
      return new IrFunction($"Id<{argument}>", false, argument, [value], [], new IrBlock([new IrReturn(new IrLocalRef(value))])) {
        Origin = new IrGenericOrigin("Id", ["T"], [argument], []) { Template = template }
      };
    }

    var s = new IrLocal("s", shapeType);
    var e = new IrLocal("e", IrExceptionClass.Reference);
    var maybe = new IrLocal("maybe", IrType.Optional(_i32));
    var bound = new IrLocal("bound", _i32);
    var list = new IrLocal("list", IrType.List(_i32));
    var map = new IrLocal("map", IrType.Map(IrType.String, _i32, MapOrder.SlotReuse));
    var set = new IrLocal("set", IrType.Set(IrType.I64, MapOrder.Insertion));
    var key = new IrLocal("key", IrType.String);
    var value = new IrLocal("value", _i32);
    var item = new IrLocal("item", IrType.I64);
    var memory = new IrLocal("memory", IrType.Memory(IrType.U8));
    var frozen = new IrLocal("frozen", IrType.ReadOnlyMemory(IrType.U8));
    var tint = new IrLocal("tint", color.Type);
    var step = new IrLocal("step", _i32);
    var x = new IrLocal("x", _i32);
    var lambda = IrLambda.Of([x], _i32, [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.Add, new IrLocalRef(x), new IrLocalRef(step)))]));
    var function = new IrLocal("f", lambda.Type);
    var resource = new IrLocal("r", shapeType);
    var thrown = IrType.Object(IrExceptionClass.DivideByZeroException);
    var body = new IrBlock([
      new IrDeclare(s, new IrNew(squareType, squareConstructor, [])),
      new IrExprStmt(new IrMethodCall(IrType.F64, baseArea, new IrLocalRef(s), []) { Kind = IrMethodCallKind.Virtual }),
      new IrExprStmt(new IrMethodCall(squareType, make, null, [])),
      new IrExprStmt(new IrReferenceEquals(new IrLocalRef(s), new IrNull(shapeType))),
      new IrExprStmt(new IrTypeTest(new IrLocalRef(s), squareType)),
      new IrExprStmt(new IrCast(squareType, new IrLocalRef(s))),
      new IrExprStmt(new IrAsCast(squareType, new IrLocalRef(s))),
      new IrFieldStore(new IrLocalRef(s), side, new IrFieldLoad(new IrLocalRef(s), side)),
      new IrExprStmt(new IrGlobalRef(count)),
      new IrDeclare(e, new IrNew(IrExceptionClass.Reference, IrExceptionClass.Constructor(IrExceptionClass.Exception, 1), [new IrStrConst("no")])),
      new IrExprStmt(new IrFieldLoad(new IrLocalRef(e), IrExceptionClass.Message)),
      new IrExprStmt(new IrGenericEquals(new IrConst(_i32, 1), new IrConst(_i32, 2))),
      new IrExprStmt(new IrGenericCompare(new IrConst(_i32, 1), new IrConst(_i32, 2))),
      new IrDeclare(maybe, new IrNone(IrType.Optional(_i32))),
      new IrAssign(maybe, new IrSome(new IrConst(_i32, 3))),
      new IrExprStmt(new IrIsNone(new IrLocalRef(maybe))),
      new IrExprStmt(new IrUnwrap(new IrLocalRef(maybe)) { IsTested = true }),
      new IrExprStmt(new IrCoalesce(new IrLocalRef(maybe), new IrConst(_i32, 0))),
      new IrExprStmt(new IrBind(bound, new IrUnwrap(new IrLocalRef(maybe)))),
      new IrDeclare(list, new IrCollection(IrCollectionOp.ListNew, IrType.List(_i32), [])),
      new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [new IrLocalRef(list), new IrConst(_i32, 4)])),
      new IrDeclare(map, new IrCollection(IrCollectionOp.MapNew, map.Type, [])),
      new IrDeclare(set, new IrCollection(IrCollectionOp.SetNew, set.Type, [])),
      new IrForEachCollection(new IrLocalRef(map), EnumerationMode.Live, key, value, new IrBlock([])),
      new IrForEachCollection(new IrLocalRef(set), EnumerationMode.Checked, item, null, new IrBlock([new IrContinue()])),
      new IrForEachCollection(new IrLocalRef(list), EnumerationMode.Checked, null, null, new IrBlock([])),
      new IrDeclare(memory, null),
      new IrDeclare(frozen, null),
      new IrDeclare(tint, new IrConst(color.Type, 1)),
      new IrFor(new IrBinary(IrBinaryOp.Less, new IrLocalRef(step), new IrConst(_i32, 3)), new IrBlock([
        new IrDeclare(function, lambda),
        new IrExprStmt(new IrInvoke(_i32, new IrLocalRef(function), [new IrConst(_i32, 5)]))
      ]), new IrBlock([new IrExprStmt(new IrStep(new IrLocalRef(step), true, false, false))])) { PerIteration = [step] },
      new IrExprStmt(new IrFunctionRef(IrType.Function(_i32), "Other")),
      new IrExprStmt(new IrBoundMethod(IrType.Function(IrType.F64), new IrLocalRef(s), baseArea) { Kind = IrMethodCallKind.Virtual }),
      new IrExprStmt(new IrBoundMethod(IrType.Function(squareType), null, make)),
      new IrTry(new IrBlock([new IrThrow(new IrLocalRef(e))]), [
        new IrCatch(IrExceptionClass.DivideByZeroException, new IrLocal("d", thrown), new IrBinary(IrBinaryOp.Equal, new IrConst(_i32, 1), new IrConst(_i32, 1)), new IrBlock([new IrRethrow()])),
        new IrCatch(IrExceptionClass.Exception, null, null, new IrBlock([]))
      ], new IrBlock([])),
      new IrTry(new IrBlock([]), [new IrCatch(square, null, null, new IrBlock([]))], null),
      new IrUsing(resource, new IrNew(squareType, squareConstructor, []), dispose, new IrBlock([])) { IsDeclaration = true },
      new IrUsing(new IrLocal("q", shapeType), new IrNull(shapeType), dispose, new IrBlock([])),
      new IrReturn(new IrThrowExpression(_i32, new IrLocalRef(e)))
    ]);

    var main = new IrFunction("Main", true, _i32, [], [s, e, maybe, bound, list, map, set, memory, frozen, tint, step, function, resource], body) { Absent = IrAbsent.Undefined };
    var other = new IrFunction("Other", false, _i32, [], [], new IrBlock([new IrReturn(new IrConst(_i32, 0))])) {
      Origin = new IrGenericOrigin("Other", [], [], [])
    };
    var module = new IrModule("Features", [main, other, Instance(_i32), Instance(IrType.F64), signature], []) {
      Types = [shape, named, opaque, disposable, @base, square, pending, box, color],
      Globals = [count],
      Caveats = ["first caveat", "second caveat"]
    };
    return new Parts(module, square, squareArea, template);
  }

  private static IrModule RoundTrip(IrModule module) => IrModuleFormat.Read(IrModuleFormat.Write(module));

  [Test]
  public void EveryLaterFeature_ReadsBackAsTheSameGraph() {
    var module = Sink().Module;
    var bytes = IrModuleFormat.Write(module);
    var read = IrModuleFormat.Read(bytes);
    Assert.That(IrGraph.Difference(module, read), Is.Null);
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(bytes));
  }

  [Test]
  public void TheDeclarations_ReadBackWithTheirParts() {
    var read = RoundTrip(Sink().Module);
    var square = (IrClassType)read.Types[5];
    var shape = (IrClassType)read.Types[4];
    Assert.That(square.Base, Is.SameAs(shape));
    Assert.That(square.Methods[0].Overrides, Is.SameAs(shape.Methods[0]));
    Assert.That(shape.Methods[0].Implements[0], Is.SameAs(((IrInterfaceType)read.Types[0]).Methods[0]));
    Assert.That(shape.Properties[0].BackingField, Is.SameAs(shape.Fields[0]));
    Assert.That(shape.StaticFields[0], Is.SameAs(read.Globals[0]));
    Assert.That(((IrClassType)read.Types[6]).HasHierarchy || read.Types[6].IsDefined || ((IrClassType)read.Types[6]).HasMembers, Is.False);
    Assert.That(read.Caveats, Is.EqualTo(new[] { "first caveat", "second caveat" }));
  }

  [Test]
  public void TheClassesOfTheLibrary_ReadBackAsTheSameObjects() {
    var read = RoundTrip(Sink().Module);
    var statements = read.Functions[0].Body.Statements;
    var created = (IrNew)((IrDeclare)statements[9]).Initializer!;
    Assert.That(created.Type.ClassType, Is.SameAs(IrExceptionClass.Exception));
    Assert.That(created.Constructor, Is.SameAs(IrExceptionClass.Constructor(IrExceptionClass.Exception, 1)));
    Assert.That(((IrFieldLoad)((IrExprStmt)statements[10]).Expression).Field, Is.SameAs(IrExceptionClass.Message));
    Assert.That(read.Types.OfType<IrClassType>().Any(type => type.IsLibrary), Is.False);
  }

  [Test]
  public void ATemplate_StaysSharedAndItsTypeParameterIsNoTypeOfTheModule() {
    var read = RoundTrip(Sink().Module);
    var first = read.Functions[2].Origin!.Template!;
    Assert.That(read.Functions[3].Origin!.Template, Is.SameAs(first));
    Assert.That(read.Types, Has.None.SameAs(first.Parameters[0].Declaration));
    Assert.That(read.Functions[1].Origin!.Template, Is.Null);
    var created = (IrNew)((IrReturn)((IrIf)first.Function.Body.Statements[0]).Then.Statements[0]).Value!;
    Assert.That(created.Constructor, Is.SameAs(IrGenerics.Parameterless));
  }

  [Test]
  public void TheGraphComparison_ComparesClassesByTheirParts() {
    var parts = Sink();
    Assert.That(IrGraph.Difference(parts.Module, Sink().Module), Is.Null);
    Assert.That(IrGraph.Difference(parts.SquareArea, parts.SquareArea with { IsSealedOverride = false }), Does.Contain("IsSealedOverride"));
    var twin = new IrClassType("Square") { IsSealed = true };
    twin.Define([]);
    Assert.That(IrGraph.Difference(parts.Square, twin), Does.Contain("IsRecord"));
  }

  [Test]
  public void TheGraphComparison_SeesAnotherClassOfTheLibrary() {
    var copy = new IrClassType("Exception") { IsLibrary = true };
    copy.Define(IrExceptionClass.Exception.Fields);
    Assert.That(IrGraph.Difference(IrExceptionClass.Exception, copy), Does.Contain("another object"));
  }

  // --- what Write refuses ---------------------------------------------------------------------------

  private static IrModule Using(IrType type) {
    var local = new IrLocal("v", type);
    return new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [local], new IrBlock([new IrDeclare(local, null)]))]);
  }

  private static IrModule With(IrExpr expression, params IrTypeDeclaration[] types)
    => new("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock([new IrExprStmt(expression)]))]) { Types = types };

  [Test]
  public void AClassOfTheLibraryAmongTheTypes_IsRefused()
    => Assert.That(() => IrModuleFormat.Write(new IrModule("M", []) { Types = [IrExceptionClass.Exception] }), Throws.ArgumentException.With.Message.Contains("'Exception' is of the library"));

  [Test]
  public void AClassWhoseOriginHasATemplate_IsRefused() {
    var parts = Sink();
    var type = new IrClassType("Box<i32>") { Origin = new IrGenericOrigin("Box", ["T"], [_i32], []) { Template = parts.Template } };
    Assert.That(() => IrModuleFormat.Write(new IrModule("M", []) { Types = [type] }), Throws.ArgumentException.With.Message.Contains("'Box<i32>' has a template"));
  }

  private static IEnumerable<TestCaseData> StrayTypes() {
    yield return new TestCaseData(new IrEnumType("Stray", IrType.U8, []).Type, "enum 'Stray'").SetName("An enum that is not the module's is refused");
    yield return new TestCaseData(IrType.Object(new IrClassType("Stray")), "class 'Stray'").SetName("A class that is not the module's is refused");
    yield return new TestCaseData(IrType.Object(new IrInterfaceType("Stray")), "interface 'Stray'").SetName("An interface that is not the module's is refused");
    yield return new TestCaseData(IrType.Object(new IrClassType("Exception") { IsLibrary = true }), "class 'Exception'").SetName("A library class that is no class of the library is refused");
  }

  [TestCaseSource(nameof(StrayTypes))]
  public void ATypeThatIsNotTheModules_IsRefused(IrType type, string message)
    => Assert.That(() => IrModuleFormat.Write(Using(type)), Throws.ArgumentException.With.Message.Contains(message + " is not one of the module's types"));

  [Test]
  public void AMethodOfNoTypeOfTheModule_IsRefused()
    => Assert.That(() => IrModuleFormat.Write(With(new IrMethodCall(IrType.Void, new IrMethod("Stray", "X.Stray", true), null, []))), Throws.ArgumentException.With.Message.Contains("method 'Stray'"));

  [Test]
  public void AMethodThatOverridesOneOfNoTypeOfTheModule_IsRefused() {
    var type = new IrClassType("C");
    type.DefineMembers([], [new IrMethod("M", "C.M", false) { Dispatch = IrDispatch.Override, Overrides = new IrMethod("M", "B.M", false) { Dispatch = IrDispatch.Virtual } }], [], []);
    Assert.That(() => IrModuleFormat.Write(new IrModule("M", []) { Types = [type] }), Throws.ArgumentException.With.Message.Contains("method 'M' is not one of a type of the module"));
  }

  [Test]
  public void AConstructorOfNoClassOfTheModule_IsRefused() {
    var type = new IrClassType("C");
    Assert.That(() => IrModuleFormat.Write(With(new IrNew(IrType.Object(type), new IrConstructor("C..ctor"), []), type)), Throws.ArgumentException.With.Message.Contains("constructor 'C..ctor'"));
  }

  [Test]
  public void AStaticFieldThatIsNoGlobalOfTheModule_IsRefused() {
    var type = new IrClassType("C");
    type.DefineMembers([], [], [], [new IrGlobal("C.x", _i32, null)]);
    Assert.That(() => IrModuleFormat.Write(new IrModule("M", []) { Types = [type] }), Throws.ArgumentException.With.Message.Contains("global 'C.x'"));
  }

  // --- what Read refuses ----------------------------------------------------------------------------

  /// <summary>A class "D" with no fields, no members and no origin, whose hierarchy the action writes after its header.</summary>
  private static void Inheriting(BinaryWriter writer, Action<BinaryWriter> hierarchy) {
    writer.Write((byte)2);
    writer.Write(false);
    writer.Write(false);
    writer.Write(false);
    writer.Write7BitEncodedInt(0);
    writer.Write(false);
    writer.Write(true);
    writer.Write(false);
    writer.Write(false);
    hierarchy(writer);
  }

  private static object[] Malformed() => [
    new TestCaseData(Expression(writer => { writer.Write(Const); writer.Write((byte)IrTypeKind.Object); writer.Write7BitEncodedInt(0); writer.Write("Nope"); }), "No class of the library is named 'Nope'").SetName("A class of the library that does not exist"),
    new TestCaseData(Declared(Opaque, writer => { writer.Write((byte)IrTypeKind.Object); writer.Write7BitEncodedInt(1); }), "An object type names neither a class nor an interface").SetName("An object type that names a struct"),
    new TestCaseData(Declared(Opaque, writer => writer.Write((byte)IrTypeKind.Void), writer => {
      writer.Write(Try);
      writer.Write(Block);
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(1);
      writer.Write7BitEncodedInt(1);
    }), "A class is expected").SetName("A catch that names a struct"),
    new TestCaseData(Declared(writer => Inheriting(writer, hierarchy => { hierarchy.Write(false); hierarchy.Write7BitEncodedInt(1); hierarchy.Write7BitEncodedInt(1); }), writer => writer.Write((byte)IrTypeKind.Void)),
      "An interface is expected").SetName("A class that implements a class"),
    new TestCaseData(Expression(writer => { writer.Write(New); writer.Write(I32); writer.Write7BitEncodedInt(5); }), "5 is no Construction").SetName("A constructor of no kind"),
    new TestCaseData(Expression(writer => { writer.Write(New); writer.Write(I32); writer.Write7BitEncodedInt(2); writer.Write7BitEncodedInt(0); }), "constructor 0 does not exist").SetName("A constructor that does not exist"),
    new TestCaseData(Expression(writer => { writer.Write(New); writer.Write(I32); writer.Write7BitEncodedInt(1); writer.Write("Exception"); writer.Write7BitEncodedInt(9); }), "constructor of 'Exception' 9 does not exist").SetName("A constructor of a class of the library that does not exist"),
    new TestCaseData(Expression(writer => { writer.Write(New); writer.Write(I32); writer.Write7BitEncodedInt(1); writer.Write("Nope"); }), "No class of the library is named 'Nope'").SetName("A constructor of a class of the library that is not there"),
    new TestCaseData(Expression(writer => { writer.Write(MethodCall); writer.Write(I32); writer.Write7BitEncodedInt(0); }), "method 0 does not exist").SetName("A method that does not exist"),
    new TestCaseData(Sealed(writer => {
      writer.Write("M");
      for (var count = 0; count < 6; ++count)
        writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(1);
      writer.Write("F");
      writer.Write(true);
      writer.Write((byte)IrTypeKind.Void);
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(0);
      writer.Write(Block);
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(0);
      writer.Write(true);
      writer.Write("F");
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(0);
      writer.Write7BitEncodedInt(2);
    }), "template 1 does not exist").SetName("A template that does not exist"),
  ];

  [TestCaseSource(nameof(Malformed))]
  public void MalformedContent_IsRefused(byte[] bytes, string message)
    => Assert.That(() => IrModuleFormat.Read(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains(message));

}
