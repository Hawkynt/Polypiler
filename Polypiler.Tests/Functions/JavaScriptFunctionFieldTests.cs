using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Functions;

/// <summary>
/// Function values stored in fields and called through them, as the Cipher framework's
/// <c>BlockAbsorber</c> stores the <c>processBlock</c> a hash gives it and calls it back (#1408):
/// JSDoc function types of parameters, a field holding a function value, <c>this._f(x)</c>, arrows that
/// capture <c>this</c> of the method that makes them, and a call that passes more arguments than the
/// function takes. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptFunctionFieldTests {

  /// <summary>The absorber of test design 1: a buffer that hands full blocks to a stored callback and the rest to a finalizer.</summary>
  private const string Absorber = """
    class Absorber {
      /**
       * @param {int} size
       * @param {function(byte[]):void} process
       */
      constructor(size, process) {
        if (typeof process !== 'function') throw new Error('process must be a function');
        /** @type {int} */
        this._size = size;
        /** @type {function(byte[]):void} */
        this._process = process;
        /** @type {byte[]} */
        this._held = new Array(size).fill(0);
        this._pending = 0;
      }
      /** @param {byte[]} data */
      Absorb(data) {
        for (let i = 0; i < data.length; i++) {
          if (this._pending === this._size) {
            this._process(this._held);
            this._pending = 0;
          }
          this._held[this._pending] = data[i];
          this._pending += 1;
        }
      }
      /**
       * @template T
       * @param {function(byte[], int, int):T} finalize
       * @returns {T}
       */
      Finish(finalize) {
        return finalize(this._held.slice(0, this._pending), this._pending, this._size);
      }
    }
    class Summer {
      constructor() {
        this._sum = 0;
        this._blocks = 0;
        this._absorber = new Absorber(4, block => this._processBlock(block));
      }
      _processBlock(block) {
        this._blocks += 1;
        for (let i = 0; i < block.length; i++) this._sum = (this._sum * 31 + block[i]) % 65521;
      }
      /** @param {byte[]} data */
      Update(data) { this._absorber.Absorb(data); }
      Final() {
        this._absorber.Finish((held, pending) => {
          this._processBlock(held);
          this._sum = (this._sum * 7 + pending) % 65521;
        });
        return this._sum * 100 + this._blocks;
      }
    }
    /** @param {byte[]} data */
    export function digest(data) { const s = new Summer(); s.Update(data); return s.Final(); }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>The diagnostics of a source that is refused, as code and message.</summary>
  private static (string Code, string Message)[] Refusals(string source)
    => [.. JavaScriptFrontend.Import("m.mjs", source).Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message))];

  /// <summary>Runs a function of the module in the interpreter and in C#, and checks the JavaScript translations are at their level.</summary>
  private static void AssertEverywhere(IrModule module, string function, object expected, params object[] arguments) {
    Assert.That(IrInterpreter.Call(module, function, arguments), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, arguments), Is.EqualTo(expected), text);
    foreach (var profile in new[] { JavaScriptProfile.Default, JavaScriptProfile.Parse("es5", "cjs") }) {
      var script = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
    }
  }

  /// <summary>Scenario: an absorber calls back into its owner - Sum pushes 3 and 4, and total is 7 as in node.</summary>
  [Test]
  public void AnAbsorberCallsBackIntoItsOwner() {
    var module = Import("""
      class Buf {
        /** @param {function(int):void} f */
        constructor(f) {
          /** @type {function(int):void} */
          this._f = f;
        }
        /** @param {int} x */
        push(x) { this._f(x); }
      }
      export class Sum {
        constructor() { this.total = 0; this._buf = new Buf(v => this.total += v); }
        /** @param {int} x */
        add(x) { this._buf.push(x); }
      }
      export function run() { const s = new Sum(); s.add(3); s.add(4); return s.total; }
      """);
    var buffer = module.Types.OfType<IrClassType>().Single(type => type.Name == "Buf");
    Assert.That(buffer.Fields.Single(field => field.Name == "_f").Type.IsFunction, Is.True);
    AssertEverywhere(module, "run", 7.0);
  }

  /// <summary>Scenario: a closure that writes a captured local, passed on as a value, returns 1 as node does.</summary>
  [Test]
  public void AClosureThatWritesACapturedLocal_ReturnsWhatNodeDoes()
    => AssertEverywhere(Import("""
      function apply(g) { g(); }
      export function f() { let n = 0; const g = () => { n++; }; apply(g); return n; }
      """), "f", 1.0);

  /// <summary>Test design 1 and 4: the absorber of the framework's shape, empty, one byte, one block, one block and one byte, two blocks and one byte (node).</summary>
  [TestCase(new byte[0], 1.0)]
  [TestCase(new byte[] { 1 }, 801.0)]
  [TestCase(new byte[] { 1, 2, 3, 4 }, 2611101.0)]
  [TestCase(new byte[] { 1, 2, 3, 4, 5 }, 2310102.0)]
  [TestCase(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 1479603.0)]
  [TestCase(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 }, 1846802.0)]
  public void TheAbsorberShape_ComputesWhatNodeComputes(byte[] data, double expected)
    => AssertEverywhere(Import(Absorber), "digest", expected, data);

  /// <summary>
  /// The absorber's finalizer takes two of the three arguments Finish passes: its lambda has a parameter
  /// for the third, which it ignores; Finish returns what the finalizer returns, which is nothing.
  /// </summary>
  [Test]
  public void TheFinalizer_IgnoresTheArgumentItTakesNoParameterFor() {
    var module = Import(Absorber);
    IrFunction Method(string type, string name)
      => module.Functions.Single(function => function.Name == module.Types.OfType<IrClassType>().Single(owner => owner.Name == type).Methods.Single(method => method.Name == name).Function);
    Assert.That(Method("Absorber", "Finish").ReturnType, Is.EqualTo(IrType.Void));
    var finalizer = IrTree.Nodes(Method("Summer", "Final").Body).OfType<IrLambda>().Single();
    Assert.That(finalizer.Parameters.Select(parameter => parameter.Name), Is.EqualTo(new[] { "held", "pending", "ignored$2" }));
  }

  private const string Holder = """
    class Holder {
      /** @param {function(int):int} f */
      constructor(f) { this._f = f; }
      /** @param {int} x */
      call(x) { return this._f(x); }
    }
    function twice(v) { return v * 2; }
    export function arrow() { return new Holder(v => v + 1).call(4); }
    export function expression() { return new Holder(function (v) { return v * v; }).call(4); }
    export function declared() { return new Holder(twice).call(4); }
    const O = { thrice: function (v) { return v * 3; } };
    export function property() { return new Holder(O.thrice).call(4); }
    """;

  /// <summary>Test design 2: an arrow, a function expression, a function of the file and one of an object of functions stored and called (node: 5, 16, 8 and 12).</summary>
  [TestCase("arrow", 5.0)]
  [TestCase("expression", 16.0)]
  [TestCase("declared", 8.0)]
  [TestCase("property", 12.0)]
  public void EveryKindOfFunction_IsStoredAndCalled(string function, double expected)
    => AssertEverywhere(Import(Holder), function, expected);

  /// <summary>A method taken from its object is no function value of the subset: the parameter holds no function.</summary>
  [Test]
  public void AMethodOfAnObject_IsRefused()
    => Assert.That(Refusals("""
      class Holder { /** @param {function(int):int} f */ constructor(f) { this._f = f; } call(x) { return this._f(x); } }
      class K { /** @param {int} v */ m(v) { return v; } }
      export function f() { return new Holder(new K().m).call(4); }
      """), Does.Contain(("PLY1105", "'f' holds number where JSDoc states a function")));

  /// <summary>Test design 3: the JSDoc spellings of a function type, with what the field becomes.</summary>
  [TestCase("function(int):void", "", "v => { this.n = v; }", "fn(i32) -> void")]
  [TestCase("function(int)", "", "v => { this.n = v; }", "fn(i32) -> void")]
  [TestCase("function(int, string):boolean", "", "(v, s) => s.length === v", "fn(i32, str) -> bool")]
  [TestCase("function(int, T):T", "@template T", "(v, s) => s + v", "fn(i32, str) -> str")]
  [TestCase("function( ):int", "", "() => 3", "fn() -> i32")]
  [TestCase("function(Owner):int", "", "o => o.n", "fn(Owner) -> i32")]
  public void JsDocFunctionTypes_AreTheFieldsType(string spelling, string template, string arrow, string expected) {
    var arguments = expected[3..expected.IndexOf(')')].Split(',', System.StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim() switch {
      "i32" => "1",
      "str" => "'a'",
      _ => "this"
    });
    var module = Import($$"""
      class Box {
        /**
         * {{template}}
         * @param {{{spelling}}} f
         */
        constructor(f) { this._f = f; }
        /** @param {Owner} self */
        run(self) { return this._f({{string.Join(", ", arguments).Replace("this", "self")}}); }
      }
      export class Owner {
        constructor() { this.n = 0; this._box = new Box({{arrow}}); }
        go() { return this._box.run(this); }
      }
      export function f() { return new Owner().go(); }
      """);
    Assert.That(module.Types.OfType<IrClassType>().Single(type => type.Name == "Box").Fields.Single().Type.ToString(), Is.EqualTo(expected));
  }

  private const string Arity = """
    class Caller {
      /** @param {function(int, int, int):int} f */
      constructor(f) { this._f = f; }
      run() { return this._f(1, 2, 3); }
    }
    export function zero() { return new Caller(() => 7).run(); }
    export function one() { return new Caller(a => a * 10).run(); }
    export function three() { return new Caller((a, b, c) => a * 100 + b * 10 + c).run(); }
    """;

  /// <summary>Test design 4: a function of 0, 1 and 3 parameters called with three arguments (node: 7, 10 and 123).</summary>
  [TestCase("zero", 7)]
  [TestCase("one", 10)]
  [TestCase("three", 123)]
  public void FunctionsOfEveryArity_TakeWhatTheyName(string function, int expected)
    => AssertEverywhere(Import(Arity), function, expected);

  /// <summary>A function of the file as a value that takes fewer arguments than its calls pass has no function of the IR's signature.</summary>
  [Test]
  public void AFunctionOfTheFileOfFewerParameters_IsRefused()
    => Assert.That(Refusals("""
      class Caller { /** @param {function(int, int):int} f */ constructor(f) { this._f = f; } run() { return this._f(1, 2); } }
      function one(a) { return a; }
      export function f() { return new Caller(one).run(); }
      """), Does.Contain(("PLY1102", "'the function one as a value that calls pass 2 arguments, of which it takes 1' is not supported yet")));

  /// <summary>Semantic trap: a field of one class named like a method of another are two members; the arrow calls the method of the object that made it.</summary>
  [Test]
  public void AFieldNamedLikeAMethodOfAnotherClass_IsTheField()
    => AssertEverywhere(Import("""
      class A {
        /** @param {function(int):void} run */
        constructor(run) { this._run = run; }
        /** @param {int} x */
        go(x) { this._run(x); }
      }
      class B {
        constructor() { this.seen = 0; this._a = new A(x => this._run(x)); }
        /** @param {int} x */
        _run(x) { this.seen = this.seen * 10 + x; }
      }
      export function f() { const b = new B(); b._a.go(4); b._a.go(2); return b.seen; }
      """), "f", 42.0);

  /// <summary>A function that returns what a call of a function that returns nothing returns returns nothing, after the call.</summary>
  [Test]
  public void ReturningACallOfNothing_CallsItAndReturnsNothing()
    => AssertEverywhere(Import("""
      class C { constructor() { this.n = 0; } bump() { this.n += 1; } twice() { return this.bump(); } }
      export function f() { const c = new C(); c.twice(); c.twice(); return c.n; }
      """), "f", 2.0);

  /// <summary>An arrow whose body is an assignment yields the value it stores where reading the place again is that value: a local and a field, not an element.</summary>
  [Test]
  public void AnAssigningArrow_YieldsWhatItStores() {
    AssertEverywhere(Import("""
      class Box { /** @param {function(int):int} f */ constructor(f) { this._f = f; } /** @param {int} x */ put(x) { return this._f(x); } }
      export function f() { let s = 0; const b = new Box(v => s += v); return b.put(3) * 100 + b.put(4) * 10 + s; }
      """), "f", 377.0);
    Assert.That(Refusals("""
      class Box { /** @param {function(int):int} f */ constructor(f) { this._f = f; } /** @param {int} x */ put(x) { return this._f(x); } }
      export function f() { const a = [0]; const b = new Box(v => a[0] = v); return b.put(3); }
      """), Does.Contain(("PLY1102", "'an assignment inside an expression' is not supported yet")));
  }

  /// <summary>Test design 5: a function type of a spelling that is no type, a stated function no call passes, and a value that is no function.</summary>
  [TestCase("/** @param {function(Foo):void} f */ export function g(f) { f(1); }", "PLY1101", "parameter 'f' of 'g' has no supported JSDoc type")]
  [TestCase("/** @param {function(int):T} f */ export function g(f) { return f(1); }", "PLY1101", "parameter 'f' of 'g' has no supported JSDoc type")]
  [TestCase("/** @param {function(int):void} f */ export function g(f) { f(1); }", "PLY1101", "parameter 'f' of 'g' is a function that no call of the file passes")]
  [TestCase("/** @param {function(int):int} f */ function g(f) { return f + 1; } export function h() { return g(5); }", "PLY1105", "'f' holds number where JSDoc states a function")]
  [TestCase("/** @param {function(int):int} f */ export function g(f) { return 1; } export function h() { return g(x => x) + g(5); }", "PLY1105", "'f' holds both a function and number")]
  public void WhatHasNoType_IsRefused(string source, string code, string message)
    => Assert.That(Refusals(source), Does.Contain((code, message)));

}
