using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// #1415: the helpers of strings and collections under the trap policy <c>result</c>. A helper that
/// traps has a form there that gives the trap as its value - an <c>Option</c> where it has one trap, a
/// <c>Result</c> of the errors it is given where it has more - and the call checks it, so that no call is refused
/// with <c>PLY1552</c> and the crate holds no panic; under <c>panic</c> the helpers stay as they were.
/// </summary>
[TestFixture]
public sealed class ResultHelperEmitTests {

  private static readonly RustProfile Result = RustProfile.Default.WithTrapPolicy(RustTrapPolicy.Result);

  private static string Emitted(string methods, RustProfile? profile = null) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), profile ?? Result));

  private static string EmittedFile(string source, RustProfile? profile = null) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", source), profile ?? Result));

  private static void IsPanicFree(string text) => Assert.That(text, Does.Not.Contain("panic!").And.Not.Contain(".expect(").And.Not.Contain(".unwrap()"));

  [Test]
  public void ListIndex_IsAnOptionCheckedAtTheCall() {
    var text = Emitted("public static int F(System.Collections.Generic.List<int> l, int i) => l[i];");
    Assert.That(text, Does.Contain("pub fn f(l: &[i32], i: i32) -> Result<i32, Error> {\n    Ok(l[ply_list_index(i, l.len()).ok_or(Error::ArgumentOutOfRange)?])\n}"));
    Assert.That(text, Does.Contain("fn ply_list_index(index: i32, count: usize) -> Option<usize> {\n    if index < 0 || index as usize >= count {\n        return None;\n    }\n    Some(index as usize)\n}"));
    IsPanicFree(text);
  }

  [Test]
  public void ListInsertRemoveAndSet_AreCheckedBeforeTheChange() {
    var text = Emitted("public static void F(System.Collections.Generic.List<int> l, int i) { l.Insert(i, 3); l.RemoveAt(0); l[i] = 2; }");
    Assert.That(text, Does.Contain("ply_list_insert_index(i, l.len()).ok_or(Error::ArgumentOutOfRange)?"));
    Assert.That(text, Does.Contain("l.remove(ply_list_index(0, l.len()).ok_or(Error::ArgumentOutOfRange)?);"));
    Assert.That(text, Does.Contain("let index = ply_list_index(i, l.len()).ok_or(Error::ArgumentOutOfRange)?;\n    l[index] = 2;"));
    IsPanicFree(text);
  }

  [Test]
  public void MapAdd_OfAKeyThatIsThere_IsTheDuplicateKeyError() {
    var text = Emitted("public static void F(System.Collections.Generic.Dictionary<int, int> m, int k) { m.Add(k, 1); }");
    Assert.That(text, Does.Contain("ply_map_add(m, k, 1).ok_or(Error::DuplicateKey)?;"));
    Assert.That(text, Does.Contain("-> Option<()> {").And.Contain("    Some(())\n}"));
    IsPanicFree(text);
  }

  [Test]
  public void EnumParse_OfTwoTraps_IsGivenTheErrorOfEach() {
    var text = EmittedFile("public enum Color { Red, Green } public static class S { public static int F(string s) => (int)System.Enum.Parse<Color>(s); }");
    Assert.That(text, Does.Contain("        &PLY_WHITESPACE_DOTNET,\n        Error::UnknownName,\n        Error::IntegerOverflow,\n    )? as i32"));
    Assert.That(text, Does.Contain("fn ply_enum_parse<E>(").And.Contain("    unknown: E,\n    overflow: E,\n) -> Result<u64, E> {").And.Contain("return Err(unknown);").And.Contain("    Ok(bits)\n}"));
    IsPanicFree(text);
  }

  [Test]
  public void NumberParses_AreCheckedAndNotInlined() {
    var text = Emitted("public static int F(string s) => int.Parse(s) + (int)double.Parse(s) + (int)float.Parse(s);");
    Assert.That(text, Does.Contain("ply_parse_integer(\n        s,\n        -2147483648,\n        2147483647,\n        Error::InvalidFormat,\n        Error::IntegerOverflow,\n    )? as i32"));
    // A helper that hands an error on with ? is no body to put in place of its call: the ? would leave the caller.
    Assert.That(text, Does.Contain("ply_parse_f64(s).ok_or(Error::InvalidFormat)?").And.Contain("ply_parse_f32(s).ok_or(Error::InvalidFormat)?"));
    Assert.That(text, Does.Contain("fn ply_parse_f64(text: &[u16]) -> Option<f64> {\n    Some(ply_float_text(text)?.parse().unwrap_or(f64::NAN))\n}"));
    IsPanicFree(text);
  }

  [Test]
  public void StringSearches_AreChecked_AndASearchFromTheStartTrapsNoMore() {
    var text = Emitted("public static int F(string s, string t) => s.IndexOf(t, 2, System.StringComparison.Ordinal) + s.Replace(t, \"x\").Length + (s.Contains(t) ? 1 : 0);");
    Assert.That(text, Does.Contain("ply_index_of(s, t, 2).ok_or(Error::IndexOutOfRange)?"));
    Assert.That(text, Does.Contain("ply_replace(s, t, &ply_utf16(\"x\")).ok_or(Error::InvalidOperation)?"));
    Assert.That(text, Does.Contain("let mut start = ply_position(from, text.len())?;"));
    IsPanicFree(text);
  }

  [Test]
  public void Views_AreChecked() {
    var text = Emitted("public static int F(int[] a, int s) { var v = new System.Span<int>(a, s, 2); var w = new System.ReadOnlySpan<int>(a).Slice(s); return v[0] + w[0]; }");
    Assert.That(text, Does.Contain("let v: &[i32] = ply_view(a, s, 2).ok_or(Error::ArgumentOutOfRange)?;"));
    Assert.That(text, Does.Contain("let w: &[i32] = ply_view_from(a, s).ok_or(Error::ArgumentOutOfRange)?;"));
    Assert.That(text, Does.Contain("fn ply_view<T>(source: &[T], start: i32, length: i32) -> Option<&[T]> {"));
    IsPanicFree(text);
  }

  [Test]
  public void CastThatMayBeNull_IsTheInvalidCastError() {
    var text = EmittedFile("public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } "
      + "public static class S { public static int F(bool c) { A a = c ? new B() : null; B b = (B)a; return b == null ? 0 : b.M(); } }");
    Assert.That(text, Does.Contain("let b: Option<Rc<B>> = ply_cast(a.clone(), PlyObject::into_b).ok_or(Error::InvalidCast)?;"));
    Assert.That(text, Does.Contain("Some(None) => None,\n        cast => Some(cast.flatten()),"));
    IsPanicFree(text);
  }

  [Test]
  public void UnderPanic_TheHelpersStayAsTheyWere() {
    var text = Emitted("public static int F(System.Collections.Generic.List<int> l, string s) => l[int.Parse(s)];", RustProfile.Default);
    Assert.That(text, Does.Contain("fn ply_list_index(index: i32, count: usize) -> usize {").And.Contain("panic!(\"ArgumentOutOfRange\");"));
    Assert.That(text, Does.Contain("fn ply_parse_integer(text: &[u16], low: i128, high: i128) -> i128 {"));
    Assert.That(text, Does.Not.Contain("Option<usize>").And.Not.Contain("Result<i128, E>"));
  }

  [Test]
  public void TrapInsideAClosure_StaysRefused()
    => Assert.That(RustCrates.Check(RustCrates.Import("public static int F(System.Collections.Generic.List<int> l, int i) { System.Func<int, int> g = x => l[x]; return g(i); }"), Result)
      .Select(problem => problem.Message), Has.Some.Contains("a trap inside a closure"));

  [Test]
  public void TryAroundAHelperOfTwoTraps_LeavesTheBodyWithEither() {
    var text = Emitted("public static int F(string s) { try { return int.Parse(s); } catch (System.FormatException) { return -1; } catch (System.OverflowException) { return -2; } }");
    Assert.That(text, Does.Contain("ply_trap(").And.Contain("Err(error) => break 't"));
    IsPanicFree(text);
  }

}
