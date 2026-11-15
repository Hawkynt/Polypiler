using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The hierarchies the JavaScript frontend imports - ECMAScript 2015 classes with <c>extends</c> and
/// constructor functions chained with <c>Object.create</c> - with their methods virtual where a
/// derived class overrides them, what the interpreter computes of them (the values node computes, see
/// the comments of the fixtures), and the constructs it refuses.
/// </summary>
[TestFixture]
public sealed class JavaScriptDispatchImportTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static Diagnostic Refusal(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Is.Not.Empty);
    return result.Diagnostics[0];
  }

  private static IrClassType ClassOf(IrModule module, string name) => module.Types.OfType<IrClassType>().Single(type => type.Name == name);

  /// <summary>Test design 1 and 4: both forms of the shapes give one hierarchy and the values of node.</summary>
  [TestCase("shapes.js")]
  [TestCase("shapes-es5.js")]
  public void Shapes_BecomeOneHierarchy(string file) {
    var module = DispatchFixtures.ImportedScript(file);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var (shape, rect, circle) = (ClassOf(module, "Shape"), ClassOf(module, "Rect"), ClassOf(module, "Circle"));
    Assert.That((shape.Base, rect.Base, circle.Base), Is.EqualTo(((IrClassType?)null, shape, shape)));
    Assert.That(shape.Methods.Select(method => (method.Name, method.Dispatch)),
      Is.EqualTo(new[] { ("area", IrDispatch.Virtual), ("sides", IrDispatch.Virtual), ("twice", IrDispatch.Instance) }));
    Assert.That(rect.Methods.Select(method => (method.Name, method.Dispatch, method.Overrides)),
      Is.EqualTo(new[] { ("area", IrDispatch.Override, shape.Methods[0]), ("sides", IrDispatch.Override, shape.Methods[1]) }));
    Assert.That(rect.Fields.Select(field => (field.Name, field.Type)), Is.EqualTo(new[] { ("width", IrType.F64), ("height", IrType.F64) }));
    var instance = IrInterpreter.Instantiate(module);
    Assert.That(instance.Call("sumTwice", 2.0, 3.0, 1.0), Is.EqualTo(18.0));
    Assert.That(instance.Call("sumSides", 2.0, 3.0, 1.0), Is.EqualTo(4.0));
    Assert.That(instance.Call("isRect", 1.0, 0.0), Is.EqualTo(false));
    Assert.That(instance.Call("isRect", 1.0, 1.0), Is.EqualTo(true));
  }

  /// <summary>The method and its overrides have one signature: every area is a double, since a product is never narrowed.</summary>
  [Test]
  public void Overrides_ShareTheirSignature() {
    var module = DispatchFixtures.ImportedScript("shapes.js");
    var areas = module.Types.OfType<IrClassType>().SelectMany(type => type.Methods).Where(method => method.Name == "area").Select(method => method.Function);
    Assert.That(areas.Select(name => module.Functions.Single(function => function.Name == name).ReturnType), Has.All.EqualTo(IrType.F64));
    var text = CSharpEmitter.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("public class Shape {").And.Contain("public virtual double area()").And.Contain("public sealed class Rect : Shape {")
      .And.Contain("public override double area()").And.Contain("public double twice()"));
  }

  /// <summary>Test design 2: a base call through super selects the method of the base class, also in ECMAScript 5.</summary>
  [TestCase("export class A { m() { return 1; } } export class B extends A { m() { return super.m() + 1; } } export function f() { return new B().m(); }", 2.0)]
  [TestCase("function A() { } A.prototype.m = function () { return 1; }; function B() { A.call(this); } B.prototype = Object.create(A.prototype); "
    + "B.prototype.constructor = B; B.prototype.m = function () { return A.prototype.m.call(this) + 1; }; function f() { return new B().m(); }", 2.0)]
  [TestCase("export class A { get v() { return 5; } } export class B extends A { w() { return super.v + 1; } } export function f() { return new B().w(); }", 6.0)]
  [TestCase("export class A { m() { return 1; } } export class B extends A { n() { return super.m() + 1; } } export function f() { return new B().n(); }", 2.0)]
  [TestCase("export class A { constructor(x) { this.x = x; } } export class B extends A { constructor(y) { super(y * 2); this.y = y; } } "
    + "export function f() { const b = new B(3); return b.x + b.y; }", 9.0)]
  [TestCase("export class A { m() { return 1; } } export class B extends A { m() { return 2; } } export class C extends B { m() { return super.m() + 10; } } "
    + "export function f() { let a = new A(); const s = a.m(); a = new C(); return s + a.m(); }", 13.0)]
  public void BaseCalls_SelectTheMethodOfTheBaseClass(string source, double expected)
    => Assert.That(IrInterpreter.Call(Import(source), "f", []), Is.EqualTo(expected));

  /// <summary>Test design 8: a call on 'this' during construction is refused only where an override could run.</summary>
  [TestCase("export class A { constructor() { this.v = 0; this.init(); } init() { } } export class B extends A { init() { this.v = 1; } } export function f() { return new B().v; }", true)]
  [TestCase("export class A { init() { } } export class B extends A { constructor() { super(); this.init(); } init() { } } export class C extends B { init() { } } export function f() { const c = new C(); return 1; }", true)]
  [TestCase("export class A { constructor() { this.v = 0; this.init(); } init() { this.v = 1; } } export class B extends A { } export function f() { return new B().v; }", false)]
  [TestCase("export class A { m() { return 1; } } export class B extends A { m() { return 2; } } "
    + "export class C { constructor() { this.v = new B().m(); } } export function f() { return new C().v; }", false)]
  [TestCase("export class A { constructor() { this.v = 0; } m() { return 1; } } export class B extends A { m() { return 2; } } export function f() { return new B().m(); }", false)]
  public void CallDuringConstruction_IsRefusedWhereAnOverrideRuns(string source, bool refused) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module is null, Is.EqualTo(refused), string.Join("; ", result.Diagnostics));
    if (refused)
      Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("'a call of an overridden method on 'this' during construction' is not supported yet"));
  }

  /// <summary>The fixture of the constructor case is refused at the call in the base constructor.</summary>
  [Test]
  public void CtorVirtualFixture_IsRefused() {
    var result = JavaScriptFrontend.Import("ctor-virtual.js", DispatchFixtures.SourceOf("ctor-virtual.js"));
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line)), Is.EqualTo(new[] { (JavaScriptFrontend.Unsupported, 8) }));
  }

  /// <summary>Instances of two classes in one variable have the type of their nearest common base class; unrelated ones are mixed.</summary>
  [Test]
  public void Variable_TakesTheCommonBase() {
    var module = Import("export class A { } export class B extends A { } export class C extends A { } export class D extends C { } "
      + "export function f(k) { let a = new B(); if (k > 0) { a = new D(); } return a instanceof C; }");
    Assert.That(IrInterpreter.Call(module, "f", [1.0]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "f", [0.0]), Is.EqualTo(false));
    var mixed = Refusal("export class A { } export class B { } export function f(k) { let a = new A(); if (k > 0) { a = new B(); } return a === null; }");
    Assert.That((mixed.Code, mixed.Message), Is.EqualTo((JavaScriptFrontend.MixedKinds, "'a' holds both A and B")));
  }

  /// <summary>Test design 10: what the construct table refuses, by name.</summary>
  [TestCase("export class P extends Error { } export function f() { return 1; }", "extends Error, which is no class of the file")]
  [TestCase("function g() { return class { }; } export class P extends g() { } export function f() { return 1; }", "extends g()")]
  [TestCase("export class P extends Q { } export class Q extends P { } export function f() { return 1; }", "a class that extends itself")]
  [TestCase("function A() { this.a = 1; } function B() { this.b = 1; } Object.setPrototypeOf(B.prototype, A.prototype); export function f() { return new B().b; }", "Object.setPrototypeOf")]
  [TestCase("export class A { constructor() { this.a = 1; } } export function f() { const a = new A(); a.__proto__ = null; return 1; }", "a property added outside the constructor")]
  [TestCase("export class A { m() { return 1; } } export class B extends A { get m() { return 2; } } export function f() { return 1; }", "the member m of a base class declared again")]
  [TestCase("export class A { #v = 1; constructor() { } } export class B extends A { #v = 2; constructor() { super(); } } export function f() { return 1; }",
    "the member #v of a base class declared again")]
  [TestCase("export class A { constructor() { this.a = 1; } } export class B extends A { constructor() { super(); this.a = 2; } a() { return 1; } } export function f() { return 1; }",
    "the member a of a base class declared again")]
  [TestCase("export class A { constructor(x) { this.x = x; } } export class B extends A { } export function f() { return 1; }",
    "a derived class without a constructor whose base class constructor takes parameters")]
  [TestCase("export class A { } export class B extends A { constructor() { this.b = 1; super(); } } export function f() { return new B().b; }",
    "a constructor of a derived class that does not begin with the call of the constructor of its base class")]
  [TestCase("export class A { } export class B extends A { m() { return super.n(); } } export function f() { return 1; }", "the method n of A, which it does not have")]
  [TestCase("export class A { } export class B extends A { m() { return super.x; } } export function f() { return 1; }", "super.x, which reads no getter of the base class")]
  [TestCase("function A() { } function B() { A.call(this); } B.prototype = Object.create(A.prototype); B.prototype.constructor = B; "
    + "B.prototype.m = function () { A.call(this); return 1; }; function f() { return new B().m(); }", "a call of the constructor of the base class outside a constructor")]
  [TestCase("function A() { } function B() { } B.prototype.m = function () { return 1; }; B.prototype = Object.create(A.prototype); function f() { return 1; }",
    "a method put on the prototype before Object.create replaces it")]
  [TestCase("export class A { m(x) { return x; } } export class B extends A { m(x, y) { return y; } } export function f() { return 1; }", "an override with another number of parameters")]
  [TestCase("export class A { } export class B extends A { } export class C extends A { } export function f(k) { const a = k > 0 ? new B() : new C(); return a instanceof B; }",
    "a conditional expression whose branches are objects of two classes")]
  public void Construct_IsRefusedByName(string source, string what) {
    var diagnostic = Refusal(source);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

  /// <summary>instanceof of a value that is no object of a class is refused.</summary>
  [Test]
  public void InstanceOfNumber_IsRefused()
    => Assert.That(Refusal("export class A { } export function f(x) { return x instanceof A; }").Message, Is.EqualTo("'instanceof of x, which is no object of a class' is not supported yet"));

}
