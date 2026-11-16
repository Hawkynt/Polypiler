using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// A variable that names the array a field of an object holds, <c>var s = this._s;</c>, as Rust writes
/// it: the field's cell borrowed for as long as the variable lives, which is sound only where nothing
/// else in the function touches the field after it - otherwise refused by name.
/// </summary>
[TestFixture]
public sealed class FieldAliasEmitTests {

  private const string Holder = "public sealed class H { private readonly int[] _s = new int[4]; private int _k; ";

  private static string Emitted(string members) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", Holder + members + " } public static class S { public static int F() => new H().G(); }")));

  private static string[] Problems(string members)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", Holder + members + " } public static class S { public static int F() => new H().G(); }")).Select(problem => problem.Message)];

  [Test]
  public void AliasThatIsWritten_BorrowsTheCellExclusively() {
    var text = Emitted("public int G() { var s = this._s; s[1] = 5; s[2] = s[1] + _k; Fill(s); return s.Length; } static void Fill(int[] a) => a[0] = 1;");
    Assert.That(text, Does.Contain("let mut s = self.s.borrow_mut();"));
    Assert.That(text, Does.Contain("s[1] = 5;"));
    Assert.That(text, Does.Contain("fill(&mut s[..]);"));
    Assert.That(text, Does.Contain("s.len() as i32"));
  }

  [Test]
  public void AliasOnlyRead_BorrowsTheCellShared() {
    var text = Emitted("public int G() { var s = this._s; var sum = 0; foreach (var v in s) sum += v; return sum + Sum(s); } static int Sum(int[] a) => a[0];");
    Assert.That(text, Does.Contain("let s = self.s.borrow();"));
    Assert.That(text, Does.Contain("for &v in &s[..] {"));
    Assert.That(text, Does.Contain("sum(&s[..])"));
  }

  [Test]
  public void FieldWrittenBeforeTheAlias_IsAllowed() {
    var text = Emitted("public int G() { this._k = 2; var s = this._s; return s[0] + _k; }");
    Assert.That(text, Does.Contain("let s = self.s.borrow();"));
  }

  [TestCase("public int G() { var s = this._s; s[0] = 1; return _s[0]; }")]
  [TestCase("public int G() { var s = this._s; s[0] = 1; return Read(); } int Read() => _s[0];")]
  [TestCase("public int G() { var s = this._s; s = new int[2]; return s[0]; }")]
  [TestCase("public int G() => Give()[0]; int[] Give() { var s = this._s; return s; }", "'a variable that names a field's array given away as a whole'")]
  public void FieldTouchedWhileTheAliasLives_IsRefused(string members, string construct = "'a variable that names a field's array where the function touches the field otherwise while it lives'")
    => Assert.That(Problems(members), Has.Some.Contain(construct));

}
