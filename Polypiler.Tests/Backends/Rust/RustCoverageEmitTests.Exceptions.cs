using NUnit.Framework;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Exceptions of the module's classes: the constructors a <c>ply_error</c> can stand for and those it
/// cannot, the inner exception, the catch clauses of the library's classes, the analysis of what may
/// trap around a catch of a class a trap belongs to, and the regions refused with <c>PLY1560</c>.
/// </summary>
public sealed partial class RustCoverageEmitTests {

  private const string Region = "PLY1560 exception region in ";

  private const string Thrown = "public static int F(int k) { try { if (k == 0) throw new Z(\"a\"); return 1; } catch (X) { return 2; } }";

  [Test]
  public void ExceptionClass_WithAFieldOfItsOwn_IsRefused()
    => Assert.That(ProblemsWithExceptions(Thrown, "public sealed class Z : X { public int Code; public Z(string m) : base(m) { } } "),
      Has.Some.EqualTo(Region + "the module cannot be emitted under the trap policy 'panic': 'an exception class with members of its own (Z)' is not supported by the Rust backend"));

  [TestCase("public Z(string m) : base(m + \"!\") { }", "new Z(\"a\")", TestName = "ExceptionConstructor_ThatComputesItsBasesArgument_IsRefused")]
  [TestCase("public Z(string a, string b) : base(b) { }", "new Z(\"a\", \"b\")", TestName = "ExceptionConstructor_ThatDropsAnArgument_IsRefused")]
  public void ExceptionConstructor_ThatDoesMoreThanHandItsArgumentsOn_IsRefused(string constructor, string creation)
    => Assert.That(ProblemsWithExceptions($"public static int F(int k) {{ try {{ if (k == 0) throw {creation}; return 1; }} catch (X) {{ return 2; }} }}", $"public sealed class Z : X {{ {constructor} }} "),
      Has.Some.EqualTo(Region + "the function 'F' cannot be emitted under the trap policy 'panic': 'an exception constructor that does more than hand its arguments to its base' is not supported by the Rust backend"));

  [Test]
  public void ExceptionConstructor_ThatHandsOnAConstant_IsTheConstantsError()
    => Assert.That(EmittedWithExceptions("public static int F(int k) { try { if (k == 0) throw new Z(); return 1; } catch (X) { return 2; } }", "public sealed class Z : X { public Z() : base(\"fixed\") { } } "),
      Does.Contain("break 't0 ply_error(15, ply_utf16(\"fixed\"), None);"));

  [Test]
  public void ExceptionConstructor_ThatHandsOnNoInner_IsAnErrorWithoutOne() {
    Assert.That(EmittedWithExceptions(Thrown, "public sealed class Z : X { public Z(string a) : base(a, null) { } } "),
      Does.Contain("break 't0 ply_error(15, ply_utf16(\"a\"), None);"));
    Assert.That(EmittedWithExceptions("public static int F(int k) { try { if (k == 0) throw new X(\"a\", null); return 1; } catch (X) { return 2; } }"),
      Does.Contain("break 't0 ply_error(13, ply_utf16(\"a\"), None);"));
  }

  [Test]
  public void InnerException_HandedOn_IsTheOptionOfTheCaughtOne() {
    var text = EmittedWithExceptions("public static string F(int k) { try { throw new X(\"a\", new X(\"i\")); } catch (X e) { return new X(\"b\", e.InnerException).Message; } }");
    Assert.That(text, Does.Contain("Some(ply_error(13, ply_utf16(\"i\"), None))"));
    Assert.That(text, Does.Contain("ply_error(13, ply_utf16(\"b\"), error.inner.clone())\n            .message\n            .clone()"));
  }

  [Test]
  public void InnerException_Read_IsTheValueThatMayBeNone()
    => Assert.That(EmittedWithExceptions("public static string F(int k) { try { throw new X(\"a\", new X(\"i\")); } catch (X e) { return e.InnerException.Message; } }"),
      Does.Contain("if ply_is(error.class, 13) {\n        ply_non_null(&error.inner).message.clone()\n    }"));

  [TestCase("System.Func<int> f = () => { throw new X(\"a\"); }; return f();", TestName = "Closure_ThatThrows_IsRefused")]
  [TestCase("System.Func<int> f = () => { try { return 1; } catch (X) { return 2; } }; return f();", TestName = "Closure_ThatCatches_IsRefused")]
  public void Closure_WithAnExceptionRegion_IsRefused(string body)
    => Assert.That(ProblemsWithExceptions($"public static int F(int k) {{ {body} }}"),
      Has.Some.EqualTo(Region + "the function 'F' cannot be emitted under the trap policy 'panic': 'an exception thrown inside a closure' is not supported by the Rust backend"));

  [TestCase("static int G(int k) { if (k > 0) throw new X(\"x\"); return k; } public static int F(int k) { int r = 0; try { r = 1; return G(k); } finally { r = 2; } }",
    TestName = "Finally_AroundACallThatThrows_IsRefused")]
  [TestCase("public static int F(int k) { try { try { if (k > 0) throw new X(\"a\"); return 1; } finally { k = 2; } } catch (X) { return 3; } }",
    TestName = "Finally_AroundAThrow_IsRefused")]
  public void Finally_AroundABodyThatMayThrow_IsRefused(string methods)
    => Assert.That(ProblemsWithExceptions(methods),
      Has.Some.EqualTo(Region + "the function 'F' cannot be emitted under the trap policy 'panic': 'a finally block around a body that may throw or complete' is not supported by the Rust backend"));

  [Test]
  public void Finally_OfAFunctionWithoutAResult_RunsAfterTheBlock()
    => Assert.That(EmittedWithExceptions("public static void F(int k) { try { k = 1; return; } finally { k = 2; } }"),
      Does.Contain("    'f0: {\n        _k = 1;\n        break 'f0;\n    }\n    _k = 2;\n}"));

  [Test]
  public void Filter_AroundACallThatPropagates_IsRefused()
    => Assert.That(ProblemsWithExceptions("static int G(int k) { if (k > 0) throw new X(\"x\"); return k; } public static int F(int k) { try { return G(k); } catch (X) when (k > 2) { return 3; } }"),
      Has.Some.EqualTo(Region + "the function 'F' cannot be emitted under the trap policy 'panic': 'an exception filter that .NET runs before the finally blocks the exception leaves, or that may trap' is not supported by the Rust backend"));

  [Test]
  public void Filter_ThatDividesByAConstant_IsTheClassTestAndTheFilter()
    => Assert.That(EmittedWithExceptions("public static int F(int k) { try { if (k > 0) throw new X(\"a\"); return 1; } catch (X) when (k / 2 > 0) { return 3; } }"),
      Does.Contain("if ply_is(error.class, 13) && k / 2 > 0 {\n        3\n    } else {\n        ply_unhandled(&error);\n    }"));

  [Test]
  public void Filter_OnAClauseOfABodyThatCompletes_TestsTheOutcome() {
    var text = EmittedWithExceptions("public static int F(int k) { try { if (k > 0) throw new X(\"a\"); } catch (X) when (k > 2) { k = 1; } catch (Y) { k = 2; } return k; }");
    Assert.That(text, Does.Contain("let outcome: Result<(), Rc<PlyError>> = 't0: {\n        if k > 0 {\n            break 't0 Err(ply_error(13, ply_utf16(\"a\"), None));\n        }\n        Ok(())\n    };"));
    Assert.That(text, Does.Contain("if let Err(error) = outcome {\n        if ply_is(error.class, 13) && k > 2 {\n            k = 1;\n        } else if ply_is(error.class, 14) {\n            k = 2;\n        } else {\n            ply_unhandled(&error);\n        }\n    }"));
  }

  [Test]
  public void Try_WhoseBranchesBothThrow_BreaksWithEither()
    => Assert.That(EmittedWithExceptions("public static int F(int k) { try { if (k > 0) { throw new X(\"a\"); } else { throw new Y(\"b\"); } } catch (X) { return 3; } }"),
      Does.Contain("let error: Rc<PlyError> = 't0: {\n        if k > 0 {\n            break 't0 ply_error(13, ply_utf16(\"a\"), None);\n        } else {\n            break 't0 ply_error(14, ply_utf16(\"b\"), None);\n        }\n    };"));

  [Test]
  public void Try_WhoseBodyIsABlockThatThrows_IsTheErrorItself()
    => Assert.That(EmittedWithExceptions("public static int F(int k) { try { { throw new X(\"a\"); } } catch (X) { return 3; } }"),
      Does.Contain("let error: Rc<PlyError> = ply_error(13, ply_utf16(\"a\"), None);\n    if ply_is(error.class, 13) {\n        3\n    }"));

  [Test]
  public void Try_InsideATry_PassesWhatItDoesNotCatchOutward()
    => Assert.That(EmittedWithExceptions("public static int F(int k) { try { try { throw new X(\"a\"); } catch (Y) { return 1; } } catch (X) { return 3; } }"),
      Does.Contain("let error1: Rc<PlyError> = 't0: {\n        let error: Rc<PlyError> = ply_error(13, ply_utf16(\"a\"), None);\n        if ply_is(error.class, 14) {\n            return 1;\n        } else {\n            break 't0 error;\n        }\n    };"));

  [Test]
  public void Throw_InAConditionalOfATryBody_BreaksOutOfTheBody()
    => Assert.That(EmittedWithExceptions("public static int F(int k) { try { int r = k > 0 ? throw new X(\"x\") : 2; return r; } catch (Exception) { return 3; } }"),
      Does.Contain("let r: i32 = if k > 0 {\n            break 't0 ply_error(13, ply_utf16(\"x\"), None);\n        } else {\n            2\n        };"));

  [Test]
  public void CatchOfException_TakesEveryException_WithoutATestOrAName() {
    var text = EmittedWithExceptions("public static int F(int k) { try { if (k > 3) throw new X(\"a\"); return 2; } catch (Exception) { return 3; } }");
    Assert.That(text, Does.Contain("    let _error: Rc<PlyError> = 't0: {\n"));
    Assert.That(text, Does.Contain("        return 2;\n    };\n    3\n}"));
    Assert.That(text, Does.Not.Contain("ply_is("));
  }

  [Test]
  public void CatchOfException_AroundABodyThatCompletes_MatchesTheErrorWithoutAName()
    => Assert.That(EmittedWithExceptions("public static int F(int k) { try { if (k > 3) throw new X(\"a\"); } catch (Exception) { k = 1; } return k; }"),
      Does.Contain("    if let Err(_error) = outcome {\n        k = 1;\n    }\n    k\n"));

  [Test]
  public void CatchOfAnExceptionByName_ThatTakesEveryException_KeepsTheName()
    => Assert.That(EmittedWithExceptions("public static string F(int k) { try { if (k > 3) throw new X(\"a\"); return \"b\"; } catch (Exception e) { return e.Message; } }"),
      Does.Contain("    let error: Rc<PlyError> = 't0: {\n"));

  [Test]
  public void CatchOfALibraryClass_AroundCodeThatCannotTrap_TestsItsNumber()
    => Assert.That(EmittedWithExceptions("public static int F(int k) { try { if (k > 3) throw new X(\"a\"); return 2; } catch (DivideByZeroException) { return 3; } }"),
      Does.Contain("if ply_is(error.class, 2) {\n        3\n    } else {\n        ply_unhandled(&error);\n    }"));

  [Test]
  public void CatchOfAClassOfATrap_AroundOperationsThatCannotTrap_IsWritten() {
    var text = EmittedWithExceptions("static int s; public static int F(int k) { try { s = k; if (~k > 3 && (k > 1 ? k : 2) > 0) throw new X(\"a\"); return k / 3; } catch (Exception) { return 3; } }");
    Assert.That(text, Does.Contain("S.store(k, Ordering::Relaxed);\n        if !k > 3 && (if k > 1 { k } else { 2 }) > 0 {"));
    Assert.That(text, Does.Contain("return k / 3;"));
  }

  [TestCase("static int s; public static int F(int k) { try { s = k; if (-k > 3 && !(k < 0) && (k > 1 ? true : false)) throw new X(\"a\"); return checked(k + 1); } catch (OverflowException) { return 3; } }",
    TestName = "CatchOfAClassOfATrap_AroundCheckedArithmetic_IsRefused")]
  [TestCase("static int G(int k) => k + 1; static int H(int k) => H(k); public static int F(int k) { try { return G(k) + H(k); } catch (Exception) { return 3; } }",
    TestName = "CatchOfAClassOfATrap_AroundARecursiveCall_IsRefused")]
  [TestCase("public static int F(int k, int d) { try { return k / d; } catch (Exception) { return 3; } }",
    TestName = "CatchOfAClassOfATrap_AroundADivisionByAVariable_IsRefused")]
  public void CatchOfAClassOfATrap_AroundCodeThatMayTrap_IsRefused(string methods)
    => Assert.That(ProblemsWithExceptions(methods),
      Has.Some.EqualTo(Region + "the function 'F' cannot be emitted under the trap policy 'panic': 'a catch of a class a trap belongs to, around code that may trap' is not supported by the Rust backend"));

}
