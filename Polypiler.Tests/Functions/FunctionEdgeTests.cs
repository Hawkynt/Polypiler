using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Functions;

/// <summary>
/// The edges of function values (#1326): the refusals each frontend names one by one, the delegate
/// types and method groups the backends write, the environments of ES5 and the loops whose header
/// cannot declare their variables of every pass.
/// </summary>
[TestFixture]
internal sealed class FunctionEdgeTests {

  private static IrModule CSharp(string source) => PrototypeFixture.Import("Snippet.cs", "#nullable disable\nusing System;\n" + source);

  private static IrModule Script(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    return result.Module ?? throw new System.InvalidOperationException(string.Join("\n", result.Diagnostics));
  }

  private static string Csharp(IrModule module, string profile) {
    var parsed = CSharpProfile.Parse(profile);
    var text = LegalizedCSharp.Emit(module, parsed)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, parsed.Name, parsed.LanguageVersionName), Is.Empty, text);
    return text;
  }

  private static string JavaScript(IrModule module, string level) {
    var profile = JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm");
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  /// <summary>Test design 10: what the C# frontend refuses at its edges, by name.</summary>
  [TestCase("public static int F(D d) => 1;", "a delegate that returns by reference", "public delegate ref int D();")]
  [TestCase("public static int F() { return 1; int G(ref int v) => v; }", "a ref parameter of a lambda")]
  [TestCase("public static int F() { return G(1); int G(params int[] v) => v.Length; }", "a params parameter of a lambda")]
  [TestCase("public static int F() { Func<int, int> f = (int x = 1) => x; return f(2); }", "a default parameter value")]
  [TestCase("public static int F() { return G(); [System.Obsolete] int G() => 1; }", "an attribute on a local function")]
  [TestCase("public static int F() { Func<int, int> f = v => v; return f(arg: 1); }", "a named or ref argument")]
  public void CSharp_RefusesByName(string methods, string named, string before = "") {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\nusing System;\n" + before + PrototypeFixture.Wrap(methods));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains($"'{named}' is not supported yet"), string.Join("\n", result.Diagnostics));
  }

  /// <summary>
  /// A delegate type the class declares is named from the types of its signature; method groups of
  /// a static, a virtual and an interface method run what C# runs; an array of function values in a
  /// public signature publishes the delegate type.
  /// </summary>
  [TestCase("net2.0")]
  [TestCase("net10.0")]
  public void DelegateTypesAndMethodGroups(string profile) {
    var module = CSharp("""
      public delegate bool Wide(sbyte a, byte b, short c, ushort d, uint e, long f, ulong g, float h, double i, int[] j, Box k, bool l, string m);
      public sealed class Box { public static int Make() => 5; }
      public interface IShape { int Corners(); }
      public class Base : IShape { public virtual int Kind() => 1; public int Corners() => 4; }
      public sealed class Derived : Base { public override int Kind() => 2; }
      public static class Snippet {
        public static bool F(Wide w) => w(1, 2, 3, 4, 5, 6, 7, 8.5f, 9.5, new int[1], new Box(), true, "x");
        public static bool G() => F((a, b, c, d, e, f, g, h, i, j, k, l, m) => m.Length == 1 && j.Length == 1 && l);
        public static int H() {
          Func<int> make = Box.Make;
          Base shape = new Derived();
          Func<int> kind = shape.Kind;
          IShape contract = shape;
          Func<int> corners = contract.Corners;
          Func<int> none = null;
          Func<int>[] all = { make, kind, corners };
          return all[0]() * 100 + all[1]() * 10 + all[2]() + (none is not null ? 1000 : 0);
        }
        public static Func<int>[] Many() => new Func<int>[2];
      }
      """);
    Assert.That((IrInterpreter.Call(module, "G", []), IrInterpreter.Call(module, "H", [])), Is.EqualTo((true, 524)));
    var text = Csharp(module, profile);
    if (profile == "net2.0")
      Assert.That(text, Does.Contain("delegate bool SByteByteInt16UInt16UInt32Int64UInt64SingleDoubleInt32ArrayBoxBooleanStringToBoolean(").And.Contain("public delegate int ToInt32();"), text);

    var build = GeneratedCSharp.Compile(text, false);
    Assert.That((build.Outcome(new PrototypeCall("G", [])), build.Outcome(new PrototypeCall("H", []))), Is.EqualTo(("true", "524")), text);
    Assert.That(JavaScript(module, "es2022"), Does.Contain("() => Box.Make()"));
  }

  /// <summary>A call of a function of the module through IrFunctionRef is a call of it in C#; a lambda that ends without its value is no value.</summary>
  [Test]
  public void HandBuiltCalls() {
    var v = new IrLocal("v", IrType.I32);
    var square = new IrFunction("Square", false, IrType.I32, [v], [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(v), new IrLocalRef(v)))]));
    var type = IrType.Function(IrType.I32, IrType.I32);
    var f = new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrInvoke(IrType.I32, new IrFunctionRef(type, "Square"), [new IrConst(IrType.I32, 7)]))]));
    var module = new IrModule("M", [square, f]);
    Assert.That(Csharp(module, "net10.0"), Does.Contain("=> Square(7);"));
    Assert.That(JavaScript(module, "es2022"), Does.Contain("return Square(7);"));
    var empty = new IrFunction("G", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrInvoke(IrType.I32, new IrLambda(IrType.Function(IrType.I32), [], [], [], new IrBlock([])), []))]));
    Assert.That(() => IrInterpreter.Call(new IrModule("M", [empty]), "G", []), Throws.InvalidOperationException);
  }

  /// <summary>
  /// ES5: two locals of one name in one loop body get properties of their own in the environment,
  /// this of a method is $self in a function expression, a function variable assigned in two places
  /// starts as null.
  /// </summary>
  [Test]
  public void Es5_Environments() {
    var module = CSharp("""
      public sealed class Counter { private int _n; public int Add(int times) { Action step = () => this._n++; for (var i = 0; i < times; i++) step(); return this._n; } }
      public static class Snippet {
        public static int F(int[] a) {
          var fs = new Func<int>[a.Length * 2];
          var k = 0;
          foreach (var v in a) {
            { var x = v; fs[k++] = () => x; }
            { var x = v * 10; fs[k++] = () => x; }
          }
          var s = 0;
          foreach (var f in fs) s += f();
          return s;
        }
        public static int G(bool c) { Func<int> f; if (c) f = () => 1; else f = () => 2; return f() + new Counter().Add(2); }
      }
      """);
    var es5 = JavaScript(module, "es5");
    Assert.That(es5, Does.Contain("$c1.x = ").And.Contain("$c1.x1 = ").And.Contain("var $self = this;").And.Contain("var f = null;"), es5);
    Assert.That(JavaScript(module, "es2022"), Does.Contain("let f = null;"));
    Assert.That(IrInterpreter.Call(module, "F", [new[] { 1, 2 }]), Is.EqualTo(33));
  }

  /// <summary>A loop of variables of every pass whose variable is read after it keeps a let in front of it and copies it into the body.</summary>
  [Test]
  public void PerIterationLoop_WithoutAHeader() {
    var i = new IrLocal("i", IrType.I32);
    var g = new IrLocal("g", IrType.Function(IrType.I32));
    var after = new IrLocal("after", IrType.I32);
    var loop = new IrFor(new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrConst(IrType.I32, 3)),
      new IrBlock([new IrAssign(g, IrLambda.Of([], IrType.I32, [], new IrBlock([new IrReturn(new IrLocalRef(i))])))]),
      new IrBlock([new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), new IrConst(IrType.I32, 1)))])) { PerIteration = [i] };
    var function = new IrFunction("F", true, IrType.I32, [], [i, g, after], new IrBlock([
      new IrDeclare(i, new IrConst(IrType.I32, 0)), new IrDeclare(g, new IrNull(g.Type)), loop,
      new IrDeclare(after, new IrLocalRef(i)), new IrReturn(new IrBinary(IrBinaryOp.Add, new IrInvoke(IrType.I32, new IrLocalRef(g), []), new IrLocalRef(after)))]));
    var module = new IrModule("M", [function]);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(5));
    var es2015 = JavaScript(module, "es2015");
    Assert.That(es2015, Does.Contain("let i_1 = i;").And.Not.Contain("for (let i"), es2015);
    Assert.That(JavaScript(module, "es5"), Does.Contain("$c1 = { i: i };"));
  }

  /// <summary>The JavaScript frontend at its edges: a concise arrow of a call that returns nothing, a parameter of a lambda the body gives another type, a hoisted function that captures a variable of its block, two functions of one shape called with different values.</summary>
  [Test]
  public void JavaScript_Edges() {
    var module = Script("""
      function id(v) { return v; }
      export function f() {
        let s = 0;
        const add = (v) => { s = s + v; };
        const twice = (v) => add(v * 2);
        twice(3);
        return s;
      }
      export function g() { const h = (v) => { v = v + 0.5; return v; }; return h(1); }
      export function k() { let x = 2; return inner(); function inner() { return x; } }
      export function m(c) { const p = c > 0 ? id : (v) => v + 1; return p(1) + id(2.5); }
      """);
    Assert.That((IrInterpreter.Call(module, "f", []), IrInterpreter.Call(module, "g", []), IrInterpreter.Call(module, "k", []), IrInterpreter.Call(module, "m", [1.0])),
      Is.EqualTo((6.0, 1.5, 2.0, 3.5)));
  }

  /// <summary>Test design 10: what the JavaScript frontend refuses at its edges, by name.</summary>
  [TestCase("export function f(a) { const g = (b) => { if (b > 0) return 1; }; return g(a); }", "'a function that can end without returning its value' is not supported yet")]
  [TestCase("export function f() { function g() { return 1; } function g() { return 2; } return g(); }", "'a second declaration of g' is not supported yet")]
  public void JavaScript_RefusesByName(string source, string message) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.EqualTo(message), string.Join("\n", result.Diagnostics));
  }

}
