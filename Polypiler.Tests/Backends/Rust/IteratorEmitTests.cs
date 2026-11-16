using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Lazy sequences as Rust writes them (#1391): an iterator function a struct of its state and of the
/// variables it keeps, with an <c>impl Iterator</c> whose <c>next</c> resumes in the state it stopped
/// in and an <c>impl Drop</c> for the finally blocks pending at a yield; its consumers loops, arguments
/// and variables; and the forms refused with PLY1558, by name.
/// </summary>
[TestFixture]
public sealed class IteratorEmitTests {

  private const string Usings = "using System; using System.Collections.Generic; ";

  private static string Emitted(string methods) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", Usings + PrototypeFixture.Wrap(methods))));

  private static string[] Problems(string source)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", Usings + source)).Select(problem => $"{problem.Code} {problem.Message}")];

  private static string[] MethodProblems(string methods) => Problems(PrototypeFixture.Wrap(methods));

  private const string Counter = "static int Calls; static IEnumerable<int> Count() { var i = 0; while (true) { ++Calls; yield return i; ++i; } } "
    + "public static int Taken() { Calls = 0; var r = 0; var taken = 0; foreach (var v in Count()) { r = r * 10 + v; if (++taken == 5) break; } return r * 100 + Calls; }";

  [Test]
  public void InfiniteCounter_IsAStructWhoseNextResumesInItsState() {
    var text = Emitted(Counter);
    Assert.That(text, Does.Contain("#[derive(Clone)]\npub struct Count {\n    state: u32,\n    i: i32,\n}"));
    Assert.That(text, Does.Contain("impl Iterator for Count {\n    type Item = i32;\n\n    fn next(&mut self) -> Option<i32> {\n        loop {\n            match self.state {\n                0 => {\n                    self.i = 0;\n                    self.state = 1;\n                }"));
    Assert.That(text, Does.Contain("                    self.state = 2;\n                    return Some(self.i);\n                }\n                2 => {\n                    self.i = self.i.wrapping_add(1);\n                    self.state = 1;\n                }\n                _ => return None,"));
    Assert.That(text, Does.Contain("fn count() -> Count {\n    Count { state: 0, i: 0 }\n}"));
    Assert.That(text, Does.Contain("    for v in count() {"));
    Assert.That(text, Does.Not.Contain("impl Drop"));
  }

  [Test]
  public void IteratorWhoseArmsAllReturn_HasNoLoop() {
    var text = Emitted("static IEnumerable<long> Wide() { yield return 1L << 40; yield return -3; } public static long F() { long s = 0; foreach (var v in Wide()) s += v; return s; }");
    Assert.That(text, Does.Contain("    fn next(&mut self) -> Option<i64> {\n        match self.state {\n            0 => {\n                self.state = 1;\n                Some(1099511627776)\n            }\n            1 => {\n                self.state = 2;\n                Some(-3)\n            }\n            _ => None,\n        }\n    }"));
  }

  [Test]
  public void YieldBreak_EndsTheSequence() {
    var text = Emitted("static IEnumerable<int> Gate(int n) { if (n < 0) yield break; yield return n + 1; } public static int F(int n) { var c = 0; foreach (var v in Gate(n)) ++c; return c; }");
    Assert.That(text, Does.Contain("                0 => {\n                    if self.n >= 0 {\n                        self.state = 1;\n                        continue;\n                    }\n                    self.state = 2;\n                    return None;\n                }"));
    Assert.That(text, Does.Contain("    for _v in gate(n) {"));
  }

  [Test]
  public void CountedLoop_JumpsToTheEndDirectly() {
    var text = Emitted("static IEnumerable<int> Up(int n) { for (var i = 1; i <= n; ++i) yield return i; } public static int F(int n) { var c = 0; foreach (var v in Up(n)) c += v; return c; }");
    Assert.That(text, Does.Contain("                    if self.i > self.n {\n                        self.state = 3;\n                        return None;\n                    }"));
    Assert.That(text, Does.Contain("fn up(n: i32) -> Up {\n    Up { state: 0, n, i: 0 }\n}"));
  }

  [Test]
  public void SequenceParameterOfAnIterator_IsAGenericOfItsStruct() {
    var text = Emitted("static IEnumerable<int> Fib() { var a = 0; var b = 1; while (true) { yield return a; var c = a + b; a = b; b = c; } } "
      + "static IEnumerable<int> Take(IEnumerable<int> source, int count) { if (count <= 0) yield break; var taken = 0; foreach (var item in source) { yield return item; if (++taken == count) yield break; } } "
      + "static int Sum(IEnumerable<int> values) { var sum = 0; foreach (var v in values) sum += v; return sum; } public static int F(int n) => Sum(Take(Fib(), n));");
    Assert.That(text, Does.Contain("pub struct Take<S> {\n    state: u32,\n    source: S,\n    count: i32,\n    taken: i32,\n    cursor0: Option<S>,\n}"));
    Assert.That(text, Does.Contain("impl<S: Iterator<Item = i32> + Clone> Iterator for Take<S> {"));
    Assert.That(text, Does.Contain("fn take<S: Iterator<Item = i32> + Clone>(source: S, count: i32) -> Take<S> {"));
    Assert.That(text, Does.Contain("self.cursor0 = Some(self.source.clone());"));
    Assert.That(text, Does.Contain("let item = match self.cursor0.as_mut().and_then(Iterator::next) {\n                        Some(value) => value,\n                        None => {\n                            self.state = 3;\n                            continue;\n                        }\n                    };"));
    Assert.That(text, Does.Contain("                3 => {\n                    self.cursor0 = None;\n                    self.state = 5;\n                    return None;\n                }"));
    Assert.That(text, Does.Contain("let c: i32 = self.a.wrapping_add(self.b);"));
    Assert.That(text, Does.Contain("fn sum(values: impl Iterator<Item = i32> + Clone) -> i32 {"));
    Assert.That(text, Does.Contain("for v in values.clone() {"));
    Assert.That(text, Does.Contain("sum(take(fib(), n))"));
  }

  [Test]
  public void YieldFrom_KeepsACursorOfTheInnerIterator() {
    var text = Emitted("static IEnumerable<int> Up(int n) { for (var i = 1; i <= n; ++i) yield return i; } "
      + "static IEnumerable<int> Doubled(int n) { foreach (var v in Up(n)) yield return v; foreach (var v in Up(n)) yield return v * 2; } "
      + "public static int F(int n) { var s = 0; foreach (var v in Doubled(n)) s = s * 10 + v; return s; }");
    Assert.That(text, Does.Contain("pub struct Doubled {\n    state: u32,\n    n: i32,\n    cursor0: Option<Up>,\n    cursor1: Option<Up>,\n}"));
    Assert.That(text, Does.Contain("self.cursor0 = Some(up(self.n));"));
    Assert.That(text, Does.Contain("                    let item = match self.cursor0.as_mut().and_then(Iterator::next) {"));
    Assert.That(text, Does.Contain("                    self.state = 1;\n                    return Some(item);"));
  }

  [Test]
  public void SequenceVariable_IsClonedForEveryEnumeration() {
    var text = Emitted("static IEnumerable<int> Up(int n) { for (var i = 1; i <= n; ++i) yield return i; } static int Sum(IEnumerable<int> values) { var sum = 0; foreach (var v in values) sum += v; return sum; } "
      + "public static int F() { var s = Up(2); var t = s; return Sum(s) + Sum(t); }");
    Assert.That(text, Does.Contain("let s = up(2);"));
    Assert.That(text, Does.Contain("let t = s.clone();"));
    Assert.That(text, Does.Contain("sum(s.clone()).wrapping_add(sum(t.clone()))"));
  }

  [Test]
  public void ArrayAsASequence_IsItsCopiedElements() {
    var text = Emitted("static int Sum(IEnumerable<int> values) { var sum = 0; foreach (var v in values) sum += v; return sum; } "
      + "public static int F() { var digits = new[] { 4, 5, 6 }; IEnumerable<int> held = digits; return Sum(digits) + Sum(held); }");
    Assert.That(text, Does.Contain("sum(digits.iter().copied())"));
    Assert.That(text, Does.Contain("let held = digits.iter().copied();"));
  }

  [Test]
  public void IteratorTakingAnArray_BorrowsIt() {
    var text = Emitted("static IEnumerable<int> Evens(int[] a) { foreach (var x in a) if (x % 2 == 0) yield return x; } "
      + "static IEnumerable<int> Pairs(int[] a, int[] b) { for (var i = 0; i < a.Length && i < b.Length; ++i) yield return a[i] * b[i]; } "
      + "public static int F() { var a = new[] { 1, 2, 4 }; var b = new[] { 3, 5 }; var e = Evens(a); var s = 0; foreach (var v in e) s += v; foreach (var v in Pairs(a, b)) s += v; return s; }");
    Assert.That(text, Does.Contain("pub struct Evens<'a> {\n    state: u32,\n    a: &'a [i32],"));
    Assert.That(text, Does.Contain("impl<'a> Iterator for Evens<'a> {"));
    Assert.That(text, Does.Contain("fn evens(a: &[i32]) -> Evens<'_> {"));
    Assert.That(text, Does.Contain("fn pairs<'a>(a: &'a [i32], b: &'a [i32]) -> Pairs<'a> {"));
    Assert.That(text, Does.Contain("let e = evens(&a);"));
  }

  [Test]
  public void IteratorTakingAString_BorrowsIt() {
    var text = Emitted("static IEnumerable<char> Chars(string s) { for (var i = 0; i < s.Length; ++i) yield return s[i]; } public static int F(string s) { var n = 0; foreach (var c in Chars(s)) n += c; return n; }");
    Assert.That(text, Does.Contain("pub struct Chars<'a> {\n    state: u32,\n    s: &'a [u16],"));
    Assert.That(text, Does.Contain("fn chars(s: &[u16]) -> Chars<'_> {"));
  }

  [Test]
  public void FinallyAroundAYield_RunsWhereTheBodyLeavesTheTryAndOnDrop() {
    var text = Emitted("static int T; static IEnumerable<int> Traced() { T = T * 10 + 7; try { T = T * 10 + 1; yield return 1; T = T * 10 + 2; yield return 2; } finally { T = T * 10 + 9; } T = T * 10 + 6; } "
      + "public static int F() { T = 0; foreach (var v in Traced()) { T = T * 10 + 8; break; } return T; }");
    Assert.That(text, Does.Contain("impl Drop for Traced {\n    fn drop(&mut self) {\n        if self.state == 1 || self.state == 2 {"));
    Assert.That(text, Does.Contain("    if let Some(_v) = traced().next() {"));
  }

  [Test]
  public void NestedFinallyBlocks_DropInnermostFirstByState() {
    var text = Emitted("static int T; static IEnumerable<int> Inner() { try { yield return 1; yield return 2; } finally { T = T * 10 + 8; } } "
      + "static IEnumerable<int> Outer() { try { foreach (var v in Inner()) yield return v; T = T * 10 + 3; yield return 3; } finally { T = T * 10 + 9; } } "
      + "public static int F() { T = 0; foreach (var v in Outer()) { T = T * 10 + v; if (v == 2) break; } return T; }");
    Assert.That(text, Does.Contain("impl Drop for Outer {\n    fn drop(&mut self) {\n        match self.state {"));
    Assert.That(text, Does.Contain("self.cursor0 = None;\n                T.store("));
    Assert.That(text, Does.Contain("            _ => {}"));
  }

  [Test]
  public void KeptVariables_StartAtTheZeroOfTheirType() {
    var text = Emitted("public enum Color { Red, Green } public struct P { public int X; } "
      + "static IEnumerable<int> Kinds(int n) { var s = \"\"; var c = Color.Green; var p = new P(); int? o = null; var f = 0.5; var b = false; "
      + "for (var i = 0; i < n; ++i) { yield return s.Length + (int)c + p.X + (o ?? 1) + (int)f + (b ? 1 : 0); s += \"x\"; c = Color.Red; p.X++; o = i; f += 1; b = !b; } } "
      + "public static int F(int n) { var t = 0; foreach (var v in Kinds(n)) t = t * 10 + v; return t; }");
    Assert.That(text, Does.Contain("s: Vec::new(),"));
    Assert.That(text, Does.Contain("c: Color::default(),"));
    Assert.That(text, Does.Contain("p: P::default(),"));
    Assert.That(text, Does.Contain("o: None,"));
    Assert.That(text, Does.Contain("f: 0.0,"));
    Assert.That(text, Does.Contain("b: false,"));
  }

  [Test]
  public void ElementsOfEveryKind_AreTheItem() {
    var text = Emitted("public sealed class Box { public int V; public Box(int v) { V = v; } } "
      + "static IEnumerable<double> Halves() { yield return 0.5; } static IEnumerable<bool> Flags() { yield return true; } static IEnumerable<string> Words() { yield return \"a\"; } "
      + "static IEnumerable<Box> Boxes() { yield return new Box(3); } static IEnumerable<Func<int>> Makers(int n) { for (var i = 0; i < n; ++i) { var k = i; yield return () => k * 10; } } "
      + "public static int F() { var t = 0; foreach (var h in Halves()) t += (int)(h * 2); foreach (var b in Flags()) t += b ? 1 : 0; foreach (var w in Words()) t += w.Length; "
      + "foreach (var x in Boxes()) t += x.V; foreach (var m in Makers(2)) t += m(); return t; }");
    Assert.That(text, Does.Contain("type Item = f64;"));
    Assert.That(text, Does.Contain("type Item = bool;"));
    Assert.That(text, Does.Contain("type Item = Vec<u16>;"));
    Assert.That(text, Does.Contain("type Item = Rc<Box>;"));
    Assert.That(text, Does.Contain("type Item = Rc<dyn Fn() -> i32>;"));
  }

  [Test]
  public void FunctionValueOfAnIterator_IsCalledThroughItsField() {
    var text = Emitted("static IEnumerable<int> Mapped(Func<int, int> f, int n) { for (var i = 0; i < n; ++i) yield return f(i); } public static int F() { var t = 0; foreach (var v in Mapped(x => x * 3, 3)) t += v; return t; }");
    Assert.That(text, Does.Contain("return Some((self.f)(self.i));"));
  }

  [Test]
  public void ObjectParameterOfAnIterator_IsItsOwnRc() {
    var text = Emitted("public sealed class Box { public int V; public Box(int v) { V = v; } } static IEnumerable<int> Ups(Box b, int n) { for (var i = 0; i < n; ++i) yield return b.V + i; } "
      + "public static int F() { var t = 0; foreach (var v in Ups(new Box(4), 2)) t = t * 10 + v; return t; }");
    Assert.That(text, Does.Contain("    b: Rc<Box>,"));
    Assert.That(text, Does.Contain("fn ups(b: &Rc<Box>, n: i32) -> Ups {"));
    Assert.That(text, Does.Contain("b: Rc::clone(b),"));
  }

  [Test]
  public void IteratorMethod_KeepsItsObjectInTheStruct() {
    var text = RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", Usings
      + "public sealed class Counter { public int Start; public Counter(int start) { Start = start; } public IEnumerable<int> Upwards(int n) { for (var i = 0; i < n; ++i) yield return Start + i; } } "
      + "public static class S { public static int F(int s) { var c = new Counter(s); var t = 0; foreach (var v in c.Upwards(3)) t = t * 10 + v; return t; } }")));
    Assert.That(text, Does.Contain("    pub fn upwards(self: &Rc<Self>, n: i32) -> CounterUpwards {\n        CounterUpwards {\n            state: 0,\n            counter: Rc::clone(self),"));
    Assert.That(text, Does.Contain("return Some(self.counter.start.get().wrapping_add(self.i));"));
    Assert.That(text, Does.Contain("for v in c.upwards(3) {"));
  }

  [Test]
  public void LoopsThatSuspend_JumpBetweenStates() {
    var text = Emitted("static IEnumerable<int> Odd(int n) { var i = 0; do { ++i; if (i % 2 == 0) continue; if (i > 7) break; yield return i; } while (i < n); "
      + "for (var j = 0; j < 3; ++j) { if (j == 1) continue; yield return -j; } while (n > 0) { --n; if (n == 2) break; yield return n * 100; } } "
      + "public static int F(int n) { var t = 0; foreach (var v in Odd(n)) t = t * 3 + v; return t; }");
    Assert.That(text, Does.Contain("impl Iterator for Odd {"));
    Assert.That(text, Does.Contain("self.n = self.n.wrapping_sub(1);"));
  }

  [Test]
  public void BreakOutOfALoopOverASequence_DropsItsCursor() {
    var text = Emitted("static int T; static IEnumerable<int> Inner() { try { yield return 1; yield return 2; } finally { T = T * 10 + 8; } } "
      + "static IEnumerable<int> First() { foreach (var v in Inner()) { yield return v; break; } yield return 5; } public static int F() { T = 0; foreach (var v in First()) T = T * 10 + v; return T; }");
    Assert.That(text, Does.Contain("self.cursor0 = None;"));
  }

  [Test]
  public void YieldBreakInsideATry_RunsTheFinallyFirst() {
    var text = Emitted("static int T; static IEnumerable<int> Cut(int n) { try { yield return 1; if (n > 0) yield break; yield return 2; } finally { T = T * 10 + 9; } } "
      + "public static int F(int n) { T = 0; foreach (var v in Cut(n)) T = T * 10 + v; return T; }");
    Assert.That(Regex(text, "wrapping_add\\(9\\)"), Is.EqualTo(3), "after the yield break, at the end of the try, in drop");
  }

  [Test]
  public void StatementsAfterAYieldBreak_AreNotWritten() {
    var text = Emitted("static IEnumerable<int> Empty() { yield break; yield return 1; } public static int F() { var c = 0; foreach (var v in Empty()) ++c; return c; }");
    Assert.That(text, Does.Contain("            0 => {\n                self.state = 1;\n                None\n            }"));
    Assert.That(text, Does.Not.Contain("Some(1)"));
  }

  [Test]
  public void ElementAssignedInTheLoop_IsMutable() {
    var text = Emitted("static IEnumerable<int> One() { yield return 1; } public static int F() { var t = 0; foreach (var v in One()) { var w = v; w += 2; t += w; } return t; }");
    Assert.That(text, Does.Contain("for v in one() {"));
  }

  [Test]
  public void LoopThatReturnsOnItsFirstPass_TakesOneValue() {
    var text = Emitted("static IEnumerable<int> One() { yield return 1; yield return 2; } public static int F() { foreach (var v in One()) return v; return 0; }");
    Assert.That(text, Does.Contain("if let Some(v) = one().next() {\n        return v;\n    }"));
  }

  [Test]
  public void PrintedIterator_ReadsBack() {
    var text = Emitted("static IEnumerable<int> Take(IEnumerable<int> source, int count) { var taken = 0; foreach (var item in source) { if (taken++ == count) yield break; yield return item; } } "
      + "static IEnumerable<int> Evens(int[] a) { foreach (var x in a) if (x % 2 == 0) yield return x; } public static int F() { var a = new[] { 2, 3, 4 }; var s = 0; foreach (var v in Take(Evens(a), 1)) s += v; return s; }");
    var file = RustSyntaxReader.Read(text);
    Assert.That(Polypiler.Core.Backends.Rust.Printing.RustPrinter.Print(file), Is.EqualTo(text));
    Assert.That(file.Items.OfType<RsImpl>().Select(implementation => implementation.Generics), Has.Some.EqualTo("<'a>").And.Some.EqualTo("<S: Iterator<Item = i32> + Clone>"));
    Assert.That(file.Items.OfType<RsStruct>().Select(structure => structure.Generics), Has.Some.EqualTo("<S>"));
  }

  private static int Regex(string text, string pattern) => System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;

  [TestCase("public abstract class A { public abstract int V(); public IEnumerable<int> Ups(int n) { for (var i = 0; i < n; ++i) yield return V() + i; } } public sealed class B : A { public override int V() => 1; } public static class S { public static int F() { var t = 0; foreach (var v in new B().Ups(2)) t += v; return t; } }", "an iterator method of a class with a hierarchy")]
  [TestCase("public static class S { static IEnumerable<int> L(List<int> l) { foreach (var x in l) yield return x; } public static int F() { var l = new List<int>(); l.Add(1); var t = 0; foreach (var v in L(l)) t += v; return t; } }", "an iterator function that takes a collection")]
  [TestCase("public static class S { static IEnumerable<int> W(int[] a) { a[0] = 1; yield return a[0]; } public static int F() { var t = 0; foreach (var v in W(new int[1])) t += v; return t; } }", "an iterator function that writes an array it takes, or takes null for it")]
  [TestCase("public static class S { static IEnumerable<int> W(string s) { s += \"x\"; yield return s.Length; } public static int F() { var t = 0; foreach (var v in W(\"a\")) t += v; return t; } }", "an iterator function that assigns a string it takes")]
  [TestCase("public static class S { static IEnumerable<int> K() { var a = new int[2]; yield return 1; yield return a[0]; } public static int F() { var t = 0; foreach (var v in K()) t += v; return t; } }", "an array kept across a yield")]
  [TestCase("public static class S { static IEnumerable<int> K() { var a = new List<int>(); yield return 1; yield return a.Count; } public static int F() { var t = 0; foreach (var v in K()) t += v; return t; } }", "a collection kept across a yield")]
  [TestCase("public static class S { static IEnumerable<int> Up() { yield return 1; } static IEnumerable<int> K() { var s = Up(); yield return 1; foreach (var v in s) yield return v; } public static int F() { var t = 0; foreach (var v in K()) t += v; return t; } }", "a sequence kept across a yield")]
  [TestCase("public sealed class B { public int V; } public static class S { static IEnumerable<int> K() { var b = new B(); yield return 1; yield return b.V; } public static int F() { var t = 0; foreach (var v in K()) t += v; return t; } }", "a variable of the type B kept across a yield, which has no first value")]
  [TestCase("public static class S { static IEnumerable<Func<int>> K() { var i = 0; yield return () => i; i = 2; } public static int F() { var t = 0; foreach (var f in K()) t += f(); return t; } }", "a closure that captures a variable an iterator keeps across a yield")]
  [TestCase("public static class S { static IEnumerable<int> K(int n) { switch (n) { case 1: yield return 1; break; default: yield return 2; break; } } public static int F() { var t = 0; foreach (var v in K(1)) t += v; return t; } }", "a Switch that suspends")]
  [TestCase("public static class S { static IEnumerable<int> K() { var l = new List<int> { 1 }; foreach (var x in l) yield return x; } public static int F() { var t = 0; foreach (var v in K()) t += v; return t; } }", "a ForEachCollection that suspends")]
  [TestCase("public static class S { static IEnumerable<int> K(int n) { yield return n; if (n > 0) foreach (var v in K(n - 1)) yield return v; } public static int F() { var t = 0; foreach (var v in K(3)) t += v; return t; } }", "a recursive iterator function, whose struct would hold itself")]
  [TestCase("public static class S { static IEnumerable<int> A(int n) { foreach (var v in B(n)) yield return v; } static IEnumerable<int> B(int n) { if (n > 0) foreach (var v in A(n - 1)) yield return v; yield return n; } public static int F() { var t = 0; foreach (var v in A(2)) t += v; return t; } }", "a recursive iterator function, whose struct would hold itself")]
  [TestCase("public static class S { static IEnumerable<int> Up() { yield return 1; } static IEnumerable<int> K(bool c) { var s = c ? Up() : Up(); foreach (var v in s) yield return v; } public static int F() { var t = 0; foreach (var v in K(true)) t += v; return t; } }", "a loop inside an iterator over a sequence that is no call of an iterator function and no sequence parameter")]
  [TestCase("public static class S { static IEnumerable<int> Up() { yield return 1; } public static int F(bool c) { var t = 0; foreach (var v in c ? Up() : Up()) t += v; return t; } }", "a sequence made by Conditional")]
  [TestCase("public static class S { static IEnumerable<int> Up() { yield return 1; } public static int F() { var s = Up(); s = Up(); var t = 0; foreach (var v in s) t += v; return t; } }", "a sequence variable assigned again")]
  [TestCase("public static class S { static int Count(IEnumerable<string> s) { var t = 0; foreach (var w in s) t += w.Length; return t; } public static int F() { var a = new[] { \"x\" }; return Count(a); } }", "an array as a sequence that is no variable of numbers")]
  [TestCase("public static class S { static int Sum(IEnumerable<int> s) { var t = 0; foreach (var v in s) t += v; return t; } public static int F() { var a = new[] { 1 }; IEnumerable<int> s = a; a[0] = 2; return Sum(s); } }", "an array or a string a sequence borrows, written while a variable holds the sequence")]
  [TestCase("public static class S { static IEnumerable<int> E(int[] a) { foreach (var x in a) yield return x; } public static int F() { var a = new[] { 1, 2 }; var t = 0; foreach (var v in E(a)) t += v + a[0]; return t; } }", "an array or a string a sequence borrows, used in the loop over it")]
  [TestCase("public static class S { static int Sum(IEnumerable<int> s, int[] b) { var t = 0; foreach (var v in s) t += v + b[0]; return t; } public static int F() { var a = new[] { 1 }; return Sum(a, a); } }", "an array or a string a sequence borrows, passed with the sequence")]
  [TestCase("public static class S { static IEnumerable<int> E(int[] a) { foreach (var x in a) yield return x; } static IEnumerable<int> K() { var a = new[] { 1, 2 }; foreach (var v in E(a)) yield return v; } public static int F() { var t = 0; foreach (var v in K()) t += v; return t; } }", "a sequence an iterator keeps that borrows an array or a string of its own")]
  [TestCase("public sealed class Oops : Exception { } public static class S { static IEnumerable<int> K(int n) { if (n < 0) throw new Oops(); yield return n; } public static int F(int n) { try { var t = 0; foreach (var v in K(n)) t += v; return t; } catch (Oops) { return -1; } } }", "an iterator function that lets an exception out")]
  [TestCase("public sealed class B { public int V; } public static class S { static IEnumerable<B> K() { yield return null; } public static int F() { var t = 0; foreach (var b in K()) t += 1; return t; } }", "a sequence that gives null")]
  [TestCase("public static class S { static IEnumerable<int[]> K() { yield return new int[1]; } public static int F() { var t = 0; foreach (var a in K()) t += a.Length; return t; } }", "a sequence of i32[]")]
  [TestCase("public static class S { static IEnumerable<int> K(int n) { try { if (n > 0) yield break; } catch (InvalidOperationException) { } yield return 1; } public static int F(int n) { var t = 0; foreach (var v in K(n)) t += v; return t; } }", "a jump out of a try that has a catch clause, or out of a finally block, inside an iterator")]
  public void FormOutsideTheIterators_IsRefusedByName(string source, string construct)
    => Assert.That(Problems(source), Has.Some.StartsWith(RustEmitter.IteratorForm).And.Some.Contain($"'{construct}"));

  [Test]
  public void SinglePassSequenceEnumeratedTwice_IsRefused() {
    var imported = Polypiler.Core.Frontends.JavaScript.JavaScriptFrontend.Import("gen.mjs",
      "function* up() { yield 1; yield 2; } export function f() { const g = up(); let t = 0; for (const v of g) { t += v; } for (const v of g) { t += v; } return t; }");
    Assert.That(RustCrates.Check(imported.Module!).Select(problem => problem.Message), Has.Some.Contain("'a single-pass sequence enumerated twice'"));
  }

  [Test]
  public void SinglePassSequenceEnumeratedOnce_IsMoved() {
    var imported = Polypiler.Core.Frontends.JavaScript.JavaScriptFrontend.Import("gen.mjs",
      "function* up() { yield 1; yield 2; } export function f() { const g = up(); let t = 0; for (const v of g) { t += v; } return t; }");
    var text = RustCrates.ModuleText(RustCrates.Emit(imported.Module!));
    Assert.That(text, Does.Contain("for v in g {"));
  }

  [Test]
  public void DelegationToAnArray_KeepsACursorOfItsElements() {
    var imported = Polypiler.Core.Frontends.JavaScript.JavaScriptFrontend.Import("gen.mjs",
      "function* all(a) { yield* a; yield 7; } export function f() { let t = 0; for (const v of all([1, 2])) { t += v; } return t; }");
    var text = RustCrates.ModuleText(RustCrates.Emit(imported.Module!));
    Assert.That(text, Does.Contain("cursor0: Option<std::iter::Copied<std::slice::Iter<'a, i32>>>,"));
    Assert.That(text, Does.Contain("self.cursor0 = Some(self.a.iter().copied());"));
  }

  [Test]
  public void Iterator_IsNoLongerALazySequenceRefusal() {
    var problems = MethodProblems("static IEnumerable<int> Up() { yield return 1; } public static int F() { var s = 0; foreach (var v in Up()) s += v; return s; }");
    Assert.That(problems, Is.Empty);
  }

}
