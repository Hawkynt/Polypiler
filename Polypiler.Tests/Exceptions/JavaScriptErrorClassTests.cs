using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Exceptions;

/// <summary>
/// The error classes the JavaScript backend generates for the traps (#1323): declared only where used,
/// with their chain, as a class from ECMAScript 2015 and as a constructor function on an
/// <c>Object.create</c> chain below, each at the level it claims. That node computes the same is
/// decided in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class JavaScriptErrorClassTests {

  private static string Emit(string methods, string level) {
    var profile = JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm");
    var module = PrototypeFixture.Import("C.cs", PrototypeFixture.Wrap(methods));
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  [TestCase("Exception", "Error")]
  [TestCase("ArithmeticException", "ArithmeticError")]
  [TestCase("DivideByZeroException", "DivideByZeroError")]
  [TestCase("OverflowException", "OverflowError")]
  [TestCase("IndexOutOfRangeException", "IndexOutOfRangeError")]
  [TestCase("NullReferenceException", "TypeError")]
  [TestCase("InvalidCastException", "InvalidCastError")]
  [TestCase("InvalidOperationException", "InvalidOperationError")]
  [TestCase("ArgumentException", "ArgumentError")]
  [TestCase("ArgumentOutOfRangeException", "ArgumentOutOfRangeError")]
  public void LibraryClass_HasItsJavaScriptClass(string library, string expected)
    => Assert.That(JavaScriptEmitter.ErrorClass(IrExceptionClass.All.Single(type => type.Name == library)), Is.EqualTo(expected));

  [TestCase("es2015")]
  [TestCase("es2017")]
  [TestCase("es2020")]
  [TestCase("es2022")]
  public void FromEcmaScript2015_AnErrorIsAClassThatExtendsItsBase(string level) {
    var text = Emit("public static int Div(int a, int b) => a / b;", level);
    Assert.That(text, Does.Contain("class ArithmeticError extends RangeError {\n  constructor(message) {\n    super(message);\n    this.name = \"ArithmeticError\";\n  }\n}"));
    Assert.That(text, Does.Contain("class DivideByZeroError extends ArithmeticError {"));
    Assert.That(text, Does.Contain("class OverflowError extends ArithmeticError {"));
    Assert.That(text.IndexOf("class ArithmeticError", System.StringComparison.Ordinal), Is.LessThan(text.IndexOf("class DivideByZeroError", System.StringComparison.Ordinal)));
    Assert.That(text, Does.Contain("throw new DivideByZeroError(\"divide by zero\");").And.Contain("throw new OverflowError(\"integer overflow\");"));
  }

  [Test]
  public void AtEcmaScript5_AnErrorIsAConstructorOnAnObjectCreateChain() {
    var text = Emit("public static int Div(int a, int b) => a / b;", "es5");
    Assert.That(text, Does.Contain(string.Join('\n',
      "function DivideByZeroError(message) {",
      "  this.name = \"DivideByZeroError\";",
      "  this.message = message;",
      "  this.stack = new RangeError(message).stack;",
      "}",
      "DivideByZeroError.prototype = Object.create(ArithmeticError.prototype);",
      "DivideByZeroError.prototype.constructor = DivideByZeroError;")));
    Assert.That(text, Does.Contain("ArithmeticError.prototype = Object.create(RangeError.prototype);"));
    Assert.That(text, Does.Not.Contain("class "));
  }

  /// <summary>Only the classes the code throws, and those of their chains, are declared.</summary>
  [Test]
  public void OnlyTheClassesUsed_AreDeclared() {
    var text = Emit("public static int Get(int[] a, int i) => a[i];", "es2022");
    Assert.That(text, Does.Contain("class IndexOutOfRangeError extends RangeError"));
    Assert.That(text, Does.Not.Contain("ArithmeticError").And.Not.Contain("OverflowError").And.Not.Contain("InvalidCastError"));
    Assert.That(Emit("public static int Add(int a, int b) => a + b;", "es2022"), Does.Not.Contain("Error"));
  }

  [TestCase("public static double R(double x, int d) => System.Math.Round(x, d);", "ArgumentOutOfRangeError extends RangeError", "throw new ArgumentOutOfRangeError(\"argument out of range\");")]
  [TestCase("public static int S(double x) => System.Math.Sign(x);", "ArithmeticError extends RangeError", "throw new ArithmeticError(\"invalid operation\");")]
  [TestCase("public static int V(int? x) => x.Value;", "InvalidOperationError extends Error", "throw new InvalidOperationError(\"missing value\");")]
  public void EachTrap_ThrowsTheClassOfItsTable(string method, string declared, string thrown) {
    var text = Emit(method, "es2022");
    Assert.That(text, Does.Contain("class " + declared).And.Contain(thrown));
  }

  /// <summary>A name of the module that a generated class has is renamed, so the class keeps it.</summary>
  [Test]
  public void ANameOfTheModule_GivesWayToAnErrorClass() {
    var text = Emit("public static int OverflowError(int a, int b) => checked(a + b);", "es2022");
    Assert.That(text, Does.Contain("class OverflowError extends ArithmeticError").And.Contain("export function OverflowError$("));
  }

  /// <summary>At ECMAScript 5 the stack comes from a native error of the root of the chain.</summary>
  [TestCase("public static int Div(int a, int b) => a / b;", "RangeError")]
  [TestCase("public static int V(int? x) => x.Value;", "Error")]
  public void AtEcmaScript5_TheStackIsThatOfTheRoot(string method, string root)
    => Assert.That(Emit(method, "es5"), Does.Contain($"this.stack = new {root}(message).stack;"));

}
