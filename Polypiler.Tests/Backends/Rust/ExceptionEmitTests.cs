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
    Assert.That(text, Does.Contain("let value = {\n        STATE.store(1, Ordering::Relaxed);\n        STATE.load(Ordering::Relaxed)\n    };\n    STATE.store(2, Ordering::Relaxed);\n    value"));
  }

  /// <summary>#1414: a try with catch clauses and a finally - the shape of async/Pipeline's Observed - runs the finally on every path, an exception no clause takes after it.</summary>
  [Test]
  public void TryWithCatchClausesAndAFinally_RunsTheFinallyOnEveryPath() {
    var text = Emitted("static string T(bool f) { if (f) throw new X(\"boom\"); return \"ok\"; } "
      + "public static string F(bool f) { var t = \"t1\"; try { t += \" \" + T(f); } catch (Y e) { t += \" caught \" + e.Message; } finally { t += \" finally\"; } return t; }");
    Assert.That(text, Does.Contain("let mut thrown = None;\n    'f0: {\n        let outcome: Result<(), Rc<PlyError>> = 't1: {"));
    Assert.That(text, Does.Contain("} else {\n                thrown = Some(error);\n                break 'f0;\n            }"));
    Assert.That(text, Does.Contain("t_2.extend_from_slice(&ply_utf16(\" finally\"));\n    if let Some(error1) = thrown {\n        ply_unhandled(&error1);\n    }\n    t_2\n}"));
    // The concatenation is one value: the part after a call that throws is not appended first.
    Assert.That(text, Does.Contain("t_2 = [\n                &t_2[..],\n                &ply_utf16(\" \")[..],\n                &(match t(f) {"));
  }

  /// <summary>#1414: a filter with a finally between it and the throw is run at the throw, before the finally (.NET's two passes), and the clause it chose is taken after.</summary>
  [Test]
  public void FilterBeforeAFinally_ChoosesTheClauseAtTheThrow() {
    var text = Emitted("public static int F(int a) { try { try { throw new X(\"x\"); } finally { a++; } } catch (X e) when (e.Message == \"x\") { return a; } }");
    Assert.That(text, Does.Contain("let thrown = ply_error(13, ply_utf16(\"x\"), None);\n            let selected = if ply_is(thrown.class, 13) && thrown.message.clone() == ply_utf16(\"x\") {\n                0\n            } else {\n                1\n            };\n            (thrown, selected)"));
    Assert.That(text, Does.Contain("let (error, selected1): (Rc<PlyError>, usize) = {"));
    Assert.That(text, Does.Contain("if selected1 == 0 {\n        a\n    } else {"));
  }

  /// <summary>#1414: a return through a finally keeps the value it read before the finally ran; a body that may also complete sets a variable for it.</summary>
  [Test]
  public void ReturnThroughAFinally_IsMadeAfterIt() {
    var text = Emitted("public static int F(int k) { var s = k; try { if (k > 0) return s * 2; s = -1; } finally { s += 100; } return s; }");
    Assert.That(text, Does.Contain("let mut returned = None;\n    'f0: {\n        if k > 0 {\n            returned = Some(s.wrapping_mul(2));\n            break 'f0;\n        }\n        s = -1;\n    }\n    s = s.wrapping_add(100);\n    if let Some(value) = returned {\n        return value;\n    }\n    s\n}"));
  }

  /// <summary>#1414: a break and a continue through a finally, each numbered, made after the finally.</summary>
  [Test]
  public void JumpsThroughAFinally_AreMadeAfterIt() {
    var text = Emitted("public static int F(int n) { var s = 0; for (var i = 0; i < n; ++i) { try { if (i == 5) break; if (i % 2 == 0) continue; s += i; } finally { s += 100; } } return s; }");
    Assert.That(text, Does.Contain("let mut jumped = 0;\n        'f1: {\n            if i == 5 {\n                jumped = 1;\n                break 'f1;\n            }"));
    Assert.That(text, Does.Contain("match jumped {\n            1 => {\n                break;\n            }\n            2 => {\n                continue;\n            }\n            _ => {}\n        }"));
  }

  /// <summary>#1414: a body of a finally left one way only, by an exception, gives it as its value and sets no variable.</summary>
  [Test]
  public void FinallyLeftOneWay_GivesNoVariableForIt() {
    var text = Emitted("public static int F(int n) { try { try { throw new X(\"x\"); } finally { n++; } } catch (X) { return n; } }");
    Assert.That(text, Does.Contain("let thrown = ply_error(13, ply_utf16(\"x\"), None);"));
    Assert.That(text, Does.Not.Contain("let mut thrown"));
  }

  /// <summary>#1414: a finally that throws replaces what leaves the body, which is still evaluated.</summary>
  [Test]
  public void FinallyThatThrows_ReplacesTheWayOut()
    => Assert.That(Emitted("public static string F() { try { try { throw new X(\"first\"); } finally { throw new X(\"second\"); } } catch (X e) { return e.Message; } }"),
      Does.Contain("'f1: {\n            let _ = ply_error(13, ply_utf16(\"first\"), None);\n            break 'f1;\n        }\n        ply_error(13, ply_utf16(\"second\"), None)"));

  /// <summary>#1414: a throw of a library class with a clause of its class around it in the same function is a value under panic too; one with none stays a trap.</summary>
  [Test]
  public void LibraryThrowWithAClauseAround_IsAValue() {
    var text = Emitted("public static int F(int k) { try { throw new InvalidOperationException(\"x\"); } catch (InvalidOperationException) { return k; } } "
      + "public static int G(int k) => k > 0 ? throw new InvalidOperationException(\"y\") : k;");
    Assert.That(text, Does.Contain("let error: Rc<PlyError> = ply_error(7, ply_utf16(\"x\"), None);"));
    Assert.That(text, Does.Contain("if k > 0 { panic!("));
  }

  /// <summary>#1414: a lambda inside a try with a finally returns from itself, not through the finally.</summary>
  [Test]
  public void ReturnOfALambdaInsideAFinallysBody_IsTheLambdas()
    => Assert.That(Problems("static int s; public static int F(int k) { try { System.Func<int, int> g = x => { if (x > 2) return x; return x + 1; }; return g(k); } finally { s = 1; } }"), Is.Empty);

  [TestCase("public static int F(int a, int b) { try { return a / b; } catch (Exception) { return 0; } }", "'a catch of a class a trap belongs to, around code that may trap'")]
  [TestCase("public static int F(int a) { try { try { try { throw new X(\"x\"); } finally { a++; } } catch (Y) { a--; } } catch (X e) when (e.Message == \"x\") { return a; } return a; }", "an exception filter that .NET runs before the finally blocks")]
  [TestCase("static int G(int k) { try { if (k > 0) throw new X(\"x\"); return k; } finally { k = 0; } } public static int F(int k) { try { return G(k); } catch (X) when (k > 2) { return 3; } }", "an exception filter that .NET runs before the finally blocks")]
  [TestCase("public static int F(int a) { Func<int> f = () => throw new X(\"x\"); try { return f(); } catch (X) { return a; } }", "'an exception thrown inside a closure'")]
  [TestCase("public static int G(int k) => k < 0 ? throw new X(\"x\") : k; public static int F(int k) { try { return G(k); } catch (X) { return 0; } }", "'a public function that lets an exception out, called inside the module'")]
  public void RegionOutsideTheForm_IsRefusedWithTheCodeOfExceptions(string methods, string construct)
    => Assert.That(Problems(methods), Has.Some.StartsWith(RustEmitter.ExceptionRegion).And.Contain(construct));

  /// <summary>#1414: a jump out of a try with catch clauses only names its loop, which Rust asks for across the labelled block of the body (E0695).</summary>
  [Test]
  public void JumpOutOfATry_NamesItsLoop() {
    var text = Emitted("public static int F(int n) { var s = 0; for (var i = 0; i < n; i++) { try { if (i == 3) break; if (i == 4) continue; if (i < 0) throw new X(\"x\"); s += i; } catch (X) { } } return s; }");
    Assert.That(text, Does.Contain("'l0: for i in 0..n {"));
    Assert.That(text, Does.Contain("if i == 3 {\n                break 'l0;\n            }\n            if i == 4 {\n                continue 'l0;\n            }"));
    Assert.That(text, Does.Contain("if ply_is(error.class, 13) {\n            } else {"));
  }

}
