using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The JavaScript of classes in every profile: ECMAScript 2015 classes or constructor functions with
/// prototype methods, class fields and private names of ECMAScript 2022 or their replacements. Whether
/// the code computes what .NET computes is decided by node in the acceptance tests; here the text is
/// checked, parsed at the level it claims.
/// </summary>
[TestFixture]
public sealed class JavaScriptClassEmissionTests {

  private const string Counter = """
    public sealed class Counter {
      public static int Created;
      private int _count;
      public Counter(int start) { _count = start; ++Created; }
      public Counter() : this(0) { }
      public int Step { get; set; } = 1;
      public int Count => _count;
      public int Twice { get { return _count * 2; } set { _count = value / 2; } }
      public int Next() { _count += Step; return _count; }
      public static Counter Make() => new Counter(1);
      private int Hidden() => _count;
      public int Peek() => Hidden();
    }
    public static class C { public static int F() { var c = new Counter(10); c.Step = 3; c.Twice = 8; return c.Next() + Counter.Make().Count + c.Peek(); } }
    """;

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "G" : null, typed)
  ];

  private static string Emit(IrModule module, JavaScriptProfile profile) {
    Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty);
    var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  private static string Emit(string source, string level) => Emit(PrototypeFixture.Import("C.cs", source), JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm"));

  /// <summary>Test design 14: every node of the classes in every profile and module format; nothing is inexpressible.</summary>
  [Test]
  public void EveryClassNode_IsWrittenInEveryProfile() {
    var module = PrototypeFixture.Import("C.cs", "#nullable disable\n" + Counter + """
      public sealed record R(int Lo, int Hi);
      public sealed class N { public R Range; public N Next; public bool Same(N other) => ReferenceEquals(this, other) || other == null || Range == other.Range; }
      public static class D { }
      """.Replace("public static class D { }", ""));
    foreach (var profile in _profiles)
      Emit(module, profile);
  }

  /// <summary>Test design 7: the form of each row of the lowering table at the levels around it.</summary>
  [TestCase("es5", "function Counter(a0) {", "Counter.prototype.Next = function () {")]
  [TestCase("es5", "Counter.Created = 0;", "Object.defineProperty(Counter.prototype, \"Count\", {")]
  [TestCase("es5", "this._count = 0;", "Counter.Make = function () {")]
  [TestCase("es2015", "class Counter {", "  Next() {")]
  [TestCase("es2015", "  get Twice() {", "  set Twice(value) {")]
  [TestCase("es2020", "  static Make() {", "Counter.Created = 0;")]
  [TestCase("es2020", "this._count = 0;", "this.Step = 0;")]
  [TestCase("es2022", "  #count = 0;", "  static Created = 0;")]
  [TestCase("es2022", "  Step = 0;", "  #Hidden() {")]
  [TestCase("es2022", "this.#count", "this.#Hidden()")]
  public void LoweringTable_RowsAtTheirLevels(string level, string first, string second)
    => Assert.That(Emit(Counter, level), Does.Contain(first).And.Contain(second));

  /// <summary>The privacy convention below ECMAScript 2022 is stated in the header; at 2022 there is nothing to state.</summary>
  [Test]
  public void PrivacyBelowEs2022_IsStatedInTheHeader() {
    Assert.That(Emit(Counter, "es2020"), Does.Contain("// Private members are '_'-prefixed properties at this level."));
    Assert.That(Emit(Counter, "es2022"), Does.Not.Contain("'_'-prefixed"));
  }

  /// <summary>Several constructors are chosen by the number of arguments; a chained one calls its target's method.</summary>
  [Test]
  public void SeveralConstructors_AreChosenByArity() {
    var text = Emit(Counter, "es2022");
    Assert.That(text, Does.Contain("if (arguments.length === 1) {").And.Contain("this.$init1(a0);").And.Contain("this.$init2();"));
    Assert.That(text, Does.Contain("  $init2() {\n    this.$init1(0);\n  }"));
  }

  /// <summary>Constructors of one arity have no form: JavaScript tells them apart by the number of arguments only.</summary>
  [Test]
  public void ConstructorsOfOneArity_AreInexpressible() {
    var module = PrototypeFixture.Import("C.cs", "public sealed class K { public K(int a) { } public K(long a) { } } public static class C { public static int F() { new K(1); return 0; } }");
    Assert.That(JavaScriptEmitter.Check(module, JavaScriptProfile.Default).Select(problem => problem.Message),
      Is.EqualTo(new[] { "'constructors of one arity' of the class 'K' cannot be expressed in JavaScript profile 'es2022'" }));
  }

  /// <summary>Records compare through a helper that is null-safe; identity is <c>===</c>; overloaded methods get names of their own.</summary>
  [Test]
  public void RecordsIdentityAndOverloads() {
    var text = Emit("""
      #nullable disable
      public sealed record R(int Lo, int Hi);
      public sealed class O { public int Add(int a) => a; public int Add(int a, int b) => a + b; }
      public static class C {
        public static bool F(int a) => new R(a, a) == new R(a, a) && new O() != null && new O().Add(1) + new O().Add(1, 2) == 4;
      }
      """, "es2022");
    Assert.That(text, Does.Contain("function $equals_R(a, b) {\n  return a === b || a !== null && b !== null && a.Lo === b.Lo && a.Hi === b.Hi;\n}"));
    Assert.That(text, Does.Contain("!(new O() === null)").And.Contain(".Add(1)").And.Contain(".Add$(1, 2)"));
  }

  /// <summary>A public class is exported as the module format says; an internal one is not.</summary>
  [TestCase("es2022", "esm", "export class Counter {")]
  [TestCase("es2015", "cjs", "exports.Counter = Counter;")]
  [TestCase("es5", "iife", "    Counter: Counter")]
  public void PublicClass_IsExported(string level, string format, string form) {
    var text = Emit(PrototypeFixture.Import("C.cs", Counter + " internal sealed class Hidden { }"), JavaScriptProfile.Parse(level, format, format == "iife" ? "G" : null));
    Assert.That(text, Does.Contain(form).And.Not.Contain("exports.Hidden").And.Not.Contain("export class Hidden"));
  }

}
