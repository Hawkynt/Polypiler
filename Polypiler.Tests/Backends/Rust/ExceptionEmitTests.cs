using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Exceptions of the module's own classes as Rust writes them: a <c>PlyError</c> behind an <c>Rc</c>,
/// a <c>try</c> body a labelled block that gives the exception, the catch clauses an <c>if</c> chain
/// over the class, a function that lets one out a <c>Result</c> with <c>?</c> at its calls, a public
/// one that lets one out the panic of an unhandled exception - and the regions that stay refused
/// with <c>PLY1560</c> naming why.
/// </summary>
[TestFixture]
public sealed class ExceptionEmitTests {

  private const string Classes = "using System; public class X : Exception { public X() { } public X(string m) : base(m) { } public X(string m, Exception inner) : base(m, inner) { } } "
    + "public sealed class Y : X { public Y(string m) : base(m) { } } ";

  private static string Emitted(string methods) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", Classes + PrototypeFixture.Wrap(methods))));

  private static string[] Problems(string methods)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", Classes + PrototypeFixture.Wrap(methods))).Select(problem => $"{problem.Code} {problem.Message}")];

  [Test]
  public void CatchOfAThrow_IsABlockThatGivesTheException() {
    var text = Emitted("public static int F(int k) { try { if (k == 0) throw new Y(\"y\"); throw new X(\"x\"); } catch (Y) { return 1; } catch (X e) { return e.Message.Length; } }");
    Assert.That(text, Does.Contain("pub struct PlyError {\n    pub class: usize,\n    pub message: Vec<u16>,\n    pub inner: Option<Rc<PlyError>>,\n}"));
    Assert.That(text, Does.Contain("let error: Rc<PlyError> = 't0: {\n        if k == 0 {\n            break 't0 ply_error(14, ply_utf16(\"y\"), None);\n        }\n        ply_error(13, ply_utf16(\"x\"), None)\n    };"));
    Assert.That(text, Does.Contain("if ply_is(error.class, 14) {\n        1\n    } else if ply_is(error.class, 13) {\n        error.message.clone().len() as i32\n    } else {\n        ply_unhandled(&error);\n    }"));
    Assert.That(text, Does.Contain("pub static PLY_ERROR_NAMES: [&str; 15] = ["));
    Assert.That(text, Does.Contain("fn ply_is(class: usize, target: usize) -> bool {"));
  }

  [Test]
  public void TryThatMayComplete_GivesAResult() {
    var text = Emitted("public static int F(int k) { var r = 0; try { if (k > 0) throw new X(\"x\"); r = 1; } catch (X) { r = 2; } return r; }");
    Assert.That(text, Does.Contain("let outcome: Result<(), Rc<PlyError>> = 't0: {"));
    Assert.That(text, Does.Contain("Ok(())\n    };"));
    Assert.That(text, Does.Contain("if let Err(error) = outcome {"));
  }

  [Test]
  public void FunctionThatLetsAnExceptionOut_ReturnsAResult() {
    var text = Emitted("static int Check(int k) { if (k < 0) throw new X(\"negative\"); return k; } static void Touch(int k) { Check(k); } "
      + "public static int F(int k) { try { Touch(k); return Check(k) * 2; } catch (X) { return -1; } }");
    Assert.That(text, Does.Contain("fn check(k: i32) -> Result<i32, Rc<PlyError>> {\n    if k < 0 {\n        return Err(ply_error(13, ply_utf16(\"negative\"), None));\n    }\n    Ok(k)\n}"));
    Assert.That(text, Does.Contain("fn touch(k: i32) -> Result<(), Rc<PlyError>> {\n    check(k)?;\n    Ok(())\n}"));
    Assert.That(text, Does.Contain("match touch(k) {\n            Ok(value) => value,\n            Err(error) => break 't0 error,\n        }\n        return (match check(k) {"));
  }

  [Test]
  public void PublicFunctionThatLetsAnExceptionOut_PanicsWithTheClass() {
    var text = Emitted("static int Check(int k) { if (k < 0) throw new X(\"negative\"); return k; } public static int F(int k) => k > 9 ? throw new Y(\"big\") : Check(k);");
    Assert.That(text, Does.Contain("ply_unhandled(&ply_error(14, ply_utf16(\"big\"), None));"));
    Assert.That(text, Does.Contain("check(k).unwrap_or_else(|error| ply_unhandled(&error))"));
    Assert.That(text, Does.Contain("fn ply_unhandled(error: &PlyError) -> ! {\n    panic!(\"{}\", PLY_ERROR_NAMES[error.class])\n}"));
  }

  [Test]
  public void FilterAndRethrowAndInner_AreWritten() {
    var text = Emitted("public static string F(string m) { try { try { throw new X(m); } catch (X e) when (e.Message == \"a\") { throw new X(\"b\", e); } catch (X) { throw; } } "
      + "catch (X outer) { return outer.InnerException == null ? outer.Message : outer.InnerException.Message; } }");
    Assert.That(text, Does.Contain("if ply_is(error.class, 13) && error.message.clone() == ply_utf16(\"a\") {"));
    Assert.That(text, Does.Contain("Some(Rc::clone(&error))"));
    Assert.That(text, Does.Contain("break 't0 Rc::clone(&error);").Or.Contain("break 't0 Err(Rc::clone(&error));"));
    Assert.That(text, Does.Contain("ply_non_null(&error1.inner).message.clone()"));
  }

  [Test]
  public void DefaultMessage_IsTheClassesOwn()
    => Assert.That(Emitted("public static string F() => new Y(\"y\").Message + new X().Message;"), Does.Contain("ply_utf16(\"Exception of type 'X' was thrown.\")"));

  [Test]
  public void FinallyAroundABodyThatReturns_RunsBeforeTheReturn() {
    var text = Emitted("static int state; public static int F() { try { state = 1; return state; } finally { state = 2; } }");
    Assert.That(text, Does.Contain("let value = 'f0: {\n        STATE.store(1, Ordering::Relaxed);\n        break 'f0 STATE.load(Ordering::Relaxed);\n    };\n    STATE.store(2, Ordering::Relaxed);\n    value"));
  }

  [TestCase("public static int F(int a, int b) { try { return a / b; } catch (Exception) { return 0; } }", "'a catch of a class a trap belongs to, around code that may trap'")]
  [TestCase("public static int F(int a) { try { try { throw new X(\"x\"); } finally { a++; } } catch (X e) when (e.Message == \"x\") { return a; } }", "an exception filter that .NET runs before the finally blocks")]
  [TestCase("public static int F(int a) { try { if (a > 0) throw new X(\"x\"); } catch (X) { a = 1; } finally { a = 2; } return a; }", "'a try with both catch clauses and a finally block'")]
  [TestCase("public static int F(int a) { try { a = 1; } finally { a = 2; } return a; }", "'a finally block around a body that may throw or complete'")]
  [TestCase("public static int F(int a) { Func<int> f = () => throw new X(\"x\"); try { return f(); } catch (X) { return a; } }", "'an exception thrown inside a closure'")]
  [TestCase("public static int G(int k) => k < 0 ? throw new X(\"x\") : k; public static int F(int k) { try { return G(k); } catch (X) { return 0; } }", "'a public function that lets an exception out, called inside the module'")]
  public void RegionOutsideTheForm_IsRefusedWithTheCodeOfExceptions(string methods, string construct)
    => Assert.That(Problems(methods), Has.Some.StartsWith(RustEmitter.ExceptionRegion).And.Contain(construct));

  [Test]
  public void JumpOutOfATry_IsRefused()
    => Assert.That(Problems("public static int F(int n) { var s = 0; for (var i = 0; i < n; i++) { try { if (i == 3) break; if (i < 0) throw new X(\"x\"); s += i; } catch (X) { } } return s; }"),
      Has.Some.Contain("'a break out of a try'"));

}
