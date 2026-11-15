using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Exceptions;

/// <summary>
/// Exceptions through both frontends and the per-profile forms of both backends (#1323): what each
/// frontend imports and refuses (test design 4, 9, 10), and the forms of a filter, a throw expression,
/// a catch without a binding and a cause at the levels around the one that brings each (5, 8).
/// </summary>
[TestFixture]
internal sealed class ExceptionImportTests {

  private static IrModule CSharp(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\n" + source);
    return result.Module ?? throw new AssertionException(string.Join("; ", result.Diagnostics));
  }

  private static Diagnostic CSharpRefusal(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\n" + source);
    Assert.That(result.Module, Is.Null);
    return result.Diagnostics[0];
  }

  private static IrModule Script(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    return result.Module ?? throw new AssertionException(string.Join("; ", result.Diagnostics));
  }

  private static Diagnostic ScriptRefusal(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    return result.Diagnostics[0];
  }

  private static string Emit(IrModule module, CSharpProfile profile) {
    Assert.That(LegalizedCSharp.Check(module, profile), Is.Empty, profile.Name);
    var text = LegalizedCSharp.Emit(module, profile)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    return text;
  }

  private static string Emit(IrModule module, string level) {
    var profile = JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm");
    Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty, level);
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  /// <summary>Test design 10: a library exception class outside the table, a constructor that takes no message, a member other than Message and InnerException.</summary>
  [TestCase("public static class C { public static int F() { try { return 1; } catch (System.TimeoutException) { return 2; } } }", "the library exception class TimeoutException")]
  [TestCase("public static class C { public static void F() { throw new System.TimeoutException(); } }", "the library exception class TimeoutException")]
  [TestCase("public static class C { public static void F() { throw new System.ArgumentOutOfRangeException(\"p\"); } }", "the constructor ArgumentOutOfRangeException(String)")]
  [TestCase("public static class C { public static string F(System.Exception e) => e.StackTrace; }", "Exception.StackTrace")]
  [TestCase("public sealed class X : System.Exception { public string F() => this.Source; } public static class C { }", "Exception.Source")]
  public void CSharp_RefusesByName(string source, string what) {
    var refusal = CSharpRefusal(source);
    Assert.That(refusal.Code, Is.EqualTo(CSharpFrontend.Unsupported));
    Assert.That(refusal.Message, Does.Contain(what));
  }

  /// <summary>Test design 9: a value that is no Error, observed by a catch or leaving the function, is refused; caught without a binding it is not.</summary>
  [TestCase("export function f() { try { throw 42; } catch (e) { return e + 1; } }", "the catch at line 1")]
  [TestCase("export function f() { try { throw 'x'; } catch (e) { return e.length; } }", "the catch at line 1")]
  [TestCase("export function f() { throw 2; }", "the caller of 'f'")]
  [TestCase("export function f() { try { throw 1; } finally { } }", "the caller of 'f'")]
  public void JavaScript_RefusesAnObservedValueThatIsNoError(string source, string place) {
    var refusal = ScriptRefusal(source);
    Assert.That(refusal.Code, Is.EqualTo(JavaScriptFrontend.NonErrorThrow));
    Assert.That(refusal.Message, Is.EqualTo($"a thrown value that is not an Error reaches {place}, which observes it"));
  }

  /// <summary>A string thrown out to the caller is an Error of that message (#1365), with the caveat that says so.</summary>
  [Test]
  public void JavaScript_ThrowsAStringToTheCallerAsAnErrorOfThatMessage() {
    var result = JavaScriptFrontend.Import("snippet.js", "export function f(a) { if (a > 0) throw 'not implemented'; try { if (a < 0) throw 'inner'; } finally { } return 1; }");
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Has.Member(JavaScriptFrontend.ThrowsTextCaveat));
    var module = result.Module!;
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "f", [0.0]), Is.EqualTo(1));
    Assert.That(() => IrInterpreter.Call(module, "f", [1.0]), Throws.Exception.With.Message.Contains("not implemented"));
    Assert.That(() => IrInterpreter.Call(module, "f", [-1.0]), Throws.Exception.With.Message.Contains("inner"));
    Assert.That(Emit(module, CSharpProfile.Default), Does.Contain("throw new global::System.Exception(\"not implemented\");"));
  }

  [TestCase("export function f() { try { throw {}; } catch { return 2; } }")]
  [TestCase("export function f() { try { throw 'x'; } catch (e) { return 2; } }")]
  public void JavaScript_AcceptsAValueNothingObserves(string source)
    => Assert.That(IrInterpreter.Call(Script(source), "f", []), Is.EqualTo(2.0));

  /// <summary>Test design 10: return, break and continue in finally, instanceof an unknown native error, new of an error other than Error.</summary>
  [TestCase("export function f() { try { return 1; } finally { return 2; } }", "return in finally")]
  [TestCase("export function f() { while (true) { try { return 1; } finally { break; } } return 0; }", "break in finally")]
  [TestCase("export function f() { for (let i = 0; i < 2; i++) { try { } finally { if (i > 0) continue; } } return 0; }", "continue in finally")]
  [TestCase("export function f() { try { return 1; } catch (e) { if (e instanceof SyntaxError) return 2; return 3; } }", "instanceof SyntaxError")]
  [TestCase("export function f() { throw new RangeError('r'); }", "new RangeError")]
  [TestCase("export function f() { throw new Error('a', 'b'); }", "the arguments of an Error")]
  public void JavaScript_RefusesByName(string source, string what) {
    var refusal = ScriptRefusal(source);
    Assert.That(refusal.Code, Is.EqualTo(JavaScriptFrontend.Unsupported));
    Assert.That(refusal.Message, Does.Contain(what));
  }

  /// <summary>A loop inside a finally block may be left by its break.</summary>
  [Test]
  public void JavaScript_BreakOfALoopInsideFinally_IsNoJump()
    => Assert.That(IrInterpreter.Call(Script("export function f() { let n = 0; try { n = 1; } finally { while (true) { n = n + 1; break; } } return n; }"), "f", []), Is.EqualTo(2.0));

  /// <summary>Test design 4: the JavaScript fixture in the interpreter computes what node computes of it.</summary>
  [TestCase("f", 0.0, 1)]
  [TestCase("f", 1.0, 2)]
  [TestCase("later", 0.0, 3.0)]
  [TestCase("later", 1.0, 4.0)]
  public void JavaScriptFixture_ComputesWhatNodeComputes(string function, double k, object expected)
    => Assert.That(IrInterpreter.Call(ExceptionFixtures.ImportedScript("errors.js"), function, [function == "f" ? (object)(int)k : k]), Is.EqualTo(expected));

  [Test]
  public void JavaScriptFixture_ChainsAndFinishes() {
    var module = ExceptionFixtures.ImportedScript("errors.js");
    Assert.That(IrInterpreter.Call(module, "chain", []), Is.EqualTo("outer/cause"));
    Assert.That(IrInterpreter.Call(module, "fin", []), Is.EqualTo(6));
    var thrown = ExceptionFixtures.ImportedScript("thrown-value.js");
    Assert.That(IrInterpreter.Call(thrown, "caught", []), Is.EqualTo(2.0));
  }

  /// <summary>The catch of a JavaScript source is one catch of every exception whose block keeps its tests; throw of its binding is a rethrow.</summary>
  [Test]
  public void JavaScriptCatch_IsACatchOfEveryException() {
    var text = Emit(ExceptionFixtures.ImportedScript("errors.js"), CSharpProfile.Parse("net10.0"));
    Assert.That(text, Does.Contain("} catch (global::System.Exception e) {\n        if (e is X) {").And.Contain("if (e is global::System.ArithmeticException) {").And.Contain("        throw;"));
    Assert.That(text, Does.Contain("public sealed class X : global::System.Exception {\n\n  public X(string m) : base(m) {"));
    Assert.That(text, Does.Contain("throw new global::System.Exception(\"outer\", inner);"));
  }

  /// <summary>Test design 5: a filter free of effects is native from C# 6 and a test of one catch block below, which tries the clauses after it as well.</summary>
  [Test]
  public void Filter_IsNativeFromCSharp6AndTestedBelow() {
    var module = ExceptionFixtures.Imported("Classes");
    Assert.That(Emit(module, CSharpProfile.Parse("net4.6")), Does.Contain("} catch (X e) when (string.Equals(e.Message, \"x\")) {"));
    var below = Emit(module, CSharpProfile.Parse("net3.5"));
    Assert.That(below, Does.Contain("} catch (global::System.Exception caught) {\n      X e = caught as X;\n      if ((e != null) && (string.Equals(e.Message, \"x\"))) {"));
    Assert.That(below, Does.Contain("} else if (caught is X) {\n        return 2;\n      } else {\n        throw;\n      }"));
  }

  /// <summary>Test design 5: a filter that reads what a finally block inside the try writes is refused below C# 6 and in JavaScript; one free of that is joined.</summary>
  [Test]
  public void Filter_ThatReadsWhatAFinallyWrites_IsRefusedBelowCSharp6() {
    var module = CSharp("""
      public static class C {
        public static int F(int k) {
          int seen = 0;
          try {
            try { throw new System.InvalidOperationException(); } finally { seen = 1; }
          } catch (System.InvalidOperationException) when (seen == 0) {
            return 1;
          } catch (System.InvalidOperationException) {
            return 2;
          }
        }
      }
      """);
    Assert.That(IrInterpreter.Call(module, "F", [0]), Is.EqualTo(1), "the filter runs before the finally block");
    Assert.That(LegalizedCSharp.Check(module, CSharpProfile.Parse("net3.5")).Select(problem => problem.Message), Has.One.Contains("runs before finally blocks"));
    Assert.That(LegalizedCSharp.Check(module, CSharpProfile.Parse("net4.6")), Is.Empty);
    Assert.That(LegalizedJavaScript.Check(module, JavaScriptProfile.Default).Select(problem => problem.Message), Has.One.Contains("runs before finally blocks"));
    Assert.That(Emit(ExceptionFixtures.Imported("Classes"), "es2022"), Does.Contain("if ((e instanceof X) && (e.message === \"x\")) {"));
  }

  /// <summary>Test design 8: a throw expression is native from C# 7 and the helper below.</summary>
  [Test]
  public void ThrowExpression_IsNativeFromCSharp7AndAHelperBelow() {
    var module = ExceptionFixtures.Imported("Classes");
    Assert.That(Emit(module, CSharpProfile.Parse("net4.8")), Does.Contain(": throw new X(\"none\");"));
    var below = Emit(module, CSharpProfile.Parse("net4.6"));
    Assert.That(below, Does.Contain(": Throw<string>(new X(\"none\"));").And.Contain("static T Throw<T>(global::System.Exception e) {\n    throw e;\n  }"));
    Assert.That(Emit(module, "es2022"), Does.Contain("$throw(new X(\"none\"))"));
  }

  /// <summary>Test design 8: a catch without a binding from ECMAScript 2019, with one below; the cause natively from ECMAScript 2022, assigned below.</summary>
  [Test]
  public void CatchBindingAndCause_FollowTheLevel() {
    var module = CSharp("public static class C { public static int F() { try { return 1; } catch { return 2; } } }");
    Assert.That(Emit(module, "es2020"), Does.Contain("} catch {"));
    Assert.That(Emit(module, "es2017"), Does.Contain("} catch ($e) {"));
    var classes = ExceptionFixtures.Imported("Classes");
    Assert.That(Emit(classes, "es2022"), Does.Contain("super(message, { cause: inner });"));
    Assert.That(Emit(classes, "es2020"), Does.Contain("super(message);\n      this.name = \"X\";\n      this.cause = inner;"));
    Assert.That(Emit(classes, "es5"), Does.Contain("this.stack = new Error(this.message).stack;"));
  }

  /// <summary>A test for a NullReferenceException leaves out the InvalidCastError of a cast, which is a TypeError too; one for an ArgumentException takes the ArgumentOutOfRangeError as well.</summary>
  [Test]
  public void TypeErrorAndArgumentTests_AreThoseOfTheTable() {
    var traps = Emit(ExceptionFixtures.Imported("Traps"), "es2022");
    Assert.That(traps, Does.Contain("} else if ((e instanceof TypeError) && !(e instanceof InvalidCastError)) {").Or.Contain("$e instanceof TypeError && !($e instanceof InvalidCastError)"));
    var argument = Emit(CSharp("public static class C { public static int F() { try { return 1; } catch (System.ArgumentException) { return 2; } } }"), "es2022");
    Assert.That(argument, Does.Contain("$e instanceof ArgumentError || $e instanceof ArgumentOutOfRangeError"));
  }

}
