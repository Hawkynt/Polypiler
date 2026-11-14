using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The classes the JavaScript frontend imports - ECMAScript 2015 and 2022 classes and constructor
/// functions of ECMAScript 5 - with the types of their fields inferred, what the interpreter computes
/// of them, and the constructs it refuses by name.
/// </summary>
[TestFixture]
public sealed class JavaScriptClassImportTests {

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

  /// <summary>Test design 3: an ECMAScript 2022 class: private field, public field with a value, getter, static field, methods.</summary>
  [Test]
  public void Es2022Class_BecomesAClass() {
    var module = Import(ClassFixtures.SourceOf("counter.js"));
    var counter = ClassOf(module, "Counter");
    Assert.That(counter.Fields.Select(field => (field.Name, field.Type, field.Visibility)),
      Is.EqualTo(new[] { ("_count", IrType.F64, IrVisibility.Private), ("step", IrType.F64, IrVisibility.Public) }));
    Assert.That(counter.Methods.Select(method => (method.Name, method.Kind)),
      Is.EqualTo(new[] { ("count", IrMethodKind.Getter), ("next", IrMethodKind.Method), ("reset", IrMethodKind.Method) }));
    Assert.That(counter.StaticFields.Select(global => global.Name), Is.EqualTo(new[] { "Counter.created" }));
    var instance = IrInterpreter.Instantiate(module);
    Assert.That(instance.Call("run", 10.0, 3.0), Is.EqualTo(16.0));
    Assert.That(instance.Call("aliased"), Is.EqualTo(1.0));
    Assert.That(instance.Call("sameObject"), Is.EqualTo(true));
    Assert.That(instance.Call("made"), Is.EqualTo(1.0));
  }

  /// <summary>Test design 3: a constructor function of ECMAScript 5 with prototype methods and an accessor of Object.defineProperty.</summary>
  [Test]
  public void Es5ConstructorFunction_BecomesAClass() {
    var module = Import(ClassFixtures.SourceOf("accumulator.js"));
    var acc = ClassOf(module, "Acc");
    Assert.That(acc.Fields.Select(field => field.Name), Is.EqualTo(new[] { "total", "count" }));
    Assert.That(acc.Properties.Single().Name, Is.EqualTo("mean"));
    Assert.That(IrInterpreter.Call(module, "sum", [1.0, 2.0, 4.0]), Is.EqualTo(7.0));
    Assert.That(IrInterpreter.Call(module, "mean", [1.0, 4.0]), Is.EqualTo(2.5));
  }

  /// <summary>Test design 3: a field the program proves an integer is one; a field of a class type holds references; static methods and setters.</summary>
  [Test]
  public void FieldTypes_AreInferredFromEveryStore() {
    var module = Import("""
      class Node {
        constructor(value) { this.value = value | 0; this.next = null; this.flag = false; }
        static of(value) { return new Node(value); }
        get twice() { return this.value * 2; }
        set twice(v) { this.value = (v / 2) | 0; }
      }
      export function f(a) {
        const first = Node.of(a);
        first.next = new Node(a + 1);
        first.twice = 10;
        first.flag = first.next !== null;
        return first.next.value + first.value + (first.flag ? 100 : 0);
      }
      """);
    var node = ClassOf(module, "Node");
    Assert.That(node.Fields.Select(field => (field.Name, field.Type)), Is.EqualTo(new[] { ("value", IrType.I32), ("next", IrType.Object(node)), ("flag", IrType.Bool) }));
    Assert.That(IrInterpreter.Call(module, "f", [4.0]), Is.EqualTo(110.0));
  }

  /// <summary>Test design 12: a field values of two kinds reach.</summary>
  [Test]
  public void FieldOfTwoKinds_IsMixed() {
    var diagnostic = Refusal("class P { constructor() { this.f = 1; } flip() { this.f = true; } } export function g() { return new P().f; }");
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo((JavaScriptFrontend.MixedKinds, "'f' holds both number and boolean")));
  }

  /// <summary>Test design 11: what the construct table refuses, by name.</summary>
  [TestCase("export class P { constructor() { this.a = 1; } } export function f() { const p = new P(); p.b = 2; return p.a; }", "a property added outside the constructor")]
  [TestCase("class B {} export class P extends B { } export function f() { return 1; }", "extends")]
  [TestCase("export class P { ['x']() { return 1; } } export function f() { return 1; }", "a computed member name")]
  [TestCase("function P() { this.a = 1; } P.prototype = {}; export function f() { return new P().a; }", "prototype mutation")]
  [TestCase("export class P { m() { return 1; } } export function f() { const p = new P(); const g = p.m; return 1; }", "a method taken from its object")]
  [TestCase("export class P { m() { return 1; } } export function f() { const p = new P(); return p.m.bind(p)(); }", "call, apply or bind of a method")]
  [TestCase("export function f() { return this.a; }", "this")]
  [TestCase("export class P { constructor() { this.a = 1; } } export function f() { const p = new P(); delete p.a; return 1; }", "delete p.a as a statement")]
  [TestCase("export class P { static { } } export function f() { return 1; }", "a static block")]
  [TestCase("export class P { constructor() { this.a = 1; } } export function f() { return P; }", "the class P used as a value")]
  public void Construct_IsRefusedByName(string source, string what) {
    var diagnostic = Refusal(source);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

}
