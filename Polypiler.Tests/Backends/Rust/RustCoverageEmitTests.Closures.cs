using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Function values, shared captures and throws: the functions that cannot be values, a static method
/// of a hierarchy as one, the test for null of a closure made in place, the steps of a captured
/// variable as statements and as values, the throws of the library's classes as the panics of their
/// traps, and a negation of a negative literal.
/// </summary>
public sealed partial class RustCoverageEmitTests {

  [Test]
  public void FunctionThatLetsAnExceptionOut_AsAFunctionValue_IsRefused()
    => Assert.That(ProblemsWithExceptions("static int G(int k) { if (k < 0) throw new X(\"x\"); return k; } public static int F() { System.Func<int, int> f = G; return f(1); }"),
      Has.Some.StartWith("PLY1550 'a function that lets an exception out as a function value' cannot be expressed"));

  [Test]
  public void StaticMethodOfAHierarchy_AsAFunctionValue_IsItsFreeFunction()
    => Assert.That(EmittedFile("public class A { public static int T(int x) => x; public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } "
      + "public static class S { public static int F() { System.Func<int, int> g = A.T; return g(1); } }"),
      Does.Contain("    let g: Rc<dyn Fn(i32) -> i32> = Rc::new(a_t);\n    g(1)\n").And.Contain("fn a_t(x: i32) -> i32 {\n    x\n}"));

  [Test]
  public void ClosureMadeInPlace_ComparedWithNull_IsTheTestOfItsOption()
    => Assert.That(Emitted("public static bool F() => (System.Func<int>)(() => 1) == null;"), Does.Contain("    Some(Rc::new(|| 1)).is_none()\n"));

  [Test]
  public void CapturedVariable_SteppedAsStatements_SetsTheCell()
    => Assert.That(Emitted("public static int F() { int x = 0; System.Action g = () => x++; g(); x++; x--; return x; }"),
      Does.Contain("    g();\n    x.set(x.get().wrapping_add(1));\n    x.set(x.get().wrapping_sub(1));\n    x.get()\n"));

  [Test]
  public void CapturedVariable_SteppedAsValues_GivesTheOldOrTheNewValue() {
    var text = Emitted("public static int F() { int x = 0; System.Func<int> g = () => x++; return g() + ++x; }");
    Assert.That(text, Does.Contain("        move || {\n            let old = x.get();\n            x.set(old.wrapping_add(1));\n            old\n        }"));
    Assert.That(text, Does.Contain("    g().wrapping_add({\n        x.set(x.get().wrapping_add(1));\n        x.get()\n    })\n"));
  }

  [Test]
  public void Throw_OfANewLibraryException_IsThePanicOfItsTrap()
    => Assert.That(Emitted("public static void F(int k) { if (k > 0) throw new System.IndexOutOfRangeException(); throw new System.InvalidCastException(); }"),
      Does.Contain("        panic!(\"IndexOutOfRange\");\n    }\n    panic!(\"InvalidCast\");\n"));

  [Test]
  public void Throw_OfALibraryExceptionAVariableHolds_IsRefused()
    => Assert.That(Problems("public static void F() { var e = new System.InvalidOperationException(); throw e; }"),
      Has.Some.StartWith("PLY1550 'the throw of an exception that is no new object' cannot be expressed"));

  // A negation as hand-built IR, which the optimizer would have folded.

  [Test]
  public void Negation_OfANegativeFloatLiteral_KeepsItsParentheses()
    => Assert.That(EmittedModule(new IrModule("m", [new IrFunction("F", true, IrType.F64, [], [], new IrBlock([new IrReturn(new IrUnary(IrUnaryOp.Negate, new IrConst(IrType.F64, System.BitConverter.DoubleToInt64Bits(-1.5))))]))])),
      Does.Contain("    -(-1.5)\n"));

}
