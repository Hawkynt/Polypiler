using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The JavaScript of hierarchies in every profile: extends and super or the Object.create chain and
/// call, interface markers, instanceof, the cast helper and the forms of as. Whether the code computes
/// what .NET computes is decided by node in the acceptance tests; here the text is checked, parsed at
/// the level it claims, and every node is shown to be expressible in every profile of both backends.
/// </summary>
[TestFixture]
public sealed class JavaScriptDispatchEmissionTests {

  /// <summary>Every construct of the lowering tables in one file.</summary>
  private const string Everything = """
    #nullable disable
    public interface INamed { int Id { get; } int Tag { get; set; } }
    public interface IShape : INamed { double Area(); }
    public interface IOther { }
    public abstract class Shape : IShape {
      private static int _made;
      public Shape() { ++_made; }
      public abstract double Area();
      public virtual int Sides() => 0;
      protected double Twice() => 2 * this.Area();
      public int Id { get { return _made; } }
      public int Tag { get { return 3; } set { } }
      public double Doubled() => this.Twice();
    }
    public class Rect : Shape {
      private readonly double _w;
      public Rect(double w) : base() { _w = w; }
      public override double Area() => _w * _w;
      public sealed override int Sides() => 4 + base.Sides();
    }
    public sealed class Tile : Rect { public Tile() : base(1) { } }
    public static class C {
      private static Shape Make(bool rect) {
        if (rect)
          return new Tile();

        return new Rect(2);
      }
      public static double F(bool rect) {
        Shape s = Make(rect);
        IShape i = s;
        INamed n = s as INamed;
        double sum = i.Area() + n.Id + s.Sides() + s.Doubled();
        n.Tag = 5;
        if (s is Tile t)
          sum += t.Area();
        if (s is Tile u) {
          sum += u.Area();
        } else if (rect) {
          sum += 1;
        }
        if (s is Rect r) {
          sum += r.Area();
        } else {
          sum -= 1;
        }
        if (Make(rect) is IShape) sum += 1;
        if (Make(rect) as Tile == null) sum += 2;
        if (s as Tile == null) sum += 4;
        return sum + ((Rect)s).Area() + (((IOther)s) == null ? 16 : 0) + (s is INamed ? 8 : 0);
      }
    }
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

  /// <summary>Test design 12: every node of the hierarchies is written in every profile of both backends; nothing is inexpressible.</summary>
  [Test, Category("Slow")]
  public void EveryDispatchNode_IsWrittenInEveryProfile() {
    var module = PrototypeFixture.Import("C.cs", Everything);
    var nodes = module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).ToList();
    Assert.That(nodes.OfType<IrMethodCall>().Select(call => call.Kind).Distinct(), Is.EquivalentTo(new[] { IrMethodCallKind.Direct, IrMethodCallKind.Virtual, IrMethodCallKind.Interface, IrMethodCallKind.Base }));
    Assert.That(nodes.OfType<IrTypeTest>().Select(test => test.Target.InterfaceType is null).Distinct(), Is.EquivalentTo(new[] { true, false }));
    Assert.That(nodes.OfType<IrCast>().Select(cast => cast.Type.InterfaceType is null).Distinct(), Is.EquivalentTo(new[] { true, false }));
    Assert.That(nodes.OfType<IrAsCast>().Select(cast => cast.Type.InterfaceType is null).Distinct(), Is.EquivalentTo(new[] { true, false }));
    foreach (var profile in _profiles)
      Emit(module, profile);

    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    }
  }

  /// <summary>Test design 1 and 3: the forms of the lowering table at ECMAScript 5.</summary>
  [TestCase("Rect.prototype = Object.create(Shape.prototype);\nRect.prototype.constructor = Rect;\nRect.prototype.Area = function")]
  [TestCase("function Rect(w) {\n  this._w = 0;\n  Shape.call(this);")]
  [TestCase("function Tile() {\n  Rect.call(this, 1);")]
  [TestCase("return (4 + Shape.prototype.Sides.call(this)) | 0;")]
  [TestCase("Object.defineProperty(Shape.prototype, \"$is_IShape\", { value: true });\nObject.defineProperty(Shape.prototype, \"$is_INamed\", { value: true });")]
  [TestCase("Object.defineProperty(Tile.prototype, \"$is_IShape\", { value: true });")]
  [TestCase("Shape.prototype._Twice = function")]
  [TestCase("if (s instanceof Tile) {\n    var t = $cast_Tile(s);")]
  [TestCase("if ($is_IShape(Make(rect))) {")]
  [TestCase("if ($as_Tile(Make(rect)) === null) {")]
  [TestCase("(s instanceof Tile ? s : null) === null")]
  [TestCase("s != null && s.$is_INamed === true")]
  [TestCase("function $cast_IOther(value) {\n  if (value === null || value != null && value.$is_IOther === true) return value;\n  throw new InvalidCastError(\"invalid cast\");\n}")]
  public void Es5_WritesTheChain(string form) {
    var text = Emit(Everything, "es5");
    Assert.That(text, Does.Contain(form));
    Assert.That(text, Does.Not.Contain("Shape.prototype.Area ="));
    Assert.That(text, Does.Not.Contain("function IShape"));
  }

  /// <summary>Test design 1 and 3: the forms of the lowering table from ECMAScript 2015 on.</summary>
  [TestCase("es2015", "class Rect extends Shape {\n  constructor(w) {\n    super();\n    this._w = 0;")]
  [TestCase("es2022", "class Rect extends Shape {\n  #w = 0;\n  constructor(w) {\n    super();")]
  [TestCase("es2015", "return (4 + super.Sides()) | 0;")]
  [TestCase("es2022", "  _Twice() {")]
  [TestCase("es2022", "class Tile extends Rect {\n  constructor() {\n    super(1);")]
  public void Es2015_WritesExtendsAndSuper(string level, string form) => Assert.That(Emit(Everything, level), Does.Contain(form));

  /// <summary>Several constructors of a derived class: methods named after their class at ECMAScript 5, inexpressible where super() would stand in a method.</summary>
  [Test]
  public void SeveralConstructorsOfADerivedClass_NeedTheFunctionForm() {
    var module = PrototypeFixture.Import("C.cs", """
      public class B { public int V; public B(int v) { V = v; } public B() : this(1) { } }
      public sealed class D : B { public D(int v) : base(v) { } public D() : base() { } }
      public static class C { public static int F() => new D().V + new D(5).V; }
      """);
    var text = Emit(module, JavaScriptProfile.Parse("es5", "cjs"));
    Assert.That(text, Does.Contain("this.$init_B1(a0);").And.Contain("D.prototype.$init_D2 = function () {\n  B.call(this);"));
    Assert.That(JavaScriptEmitter.Check(module, JavaScriptProfile.Default).Select(diagnostic => diagnostic.Message),
      Is.EqualTo(new[] { "'several constructors of a derived class' of the class 'D' cannot be expressed in JavaScript profile 'es2022'" }));
  }

}
