using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.Cpp;

namespace Polypiler.Tests.Frontends.Cpp;

[TestFixture]
public sealed class CppDiagnosticsAndEdgeCaseTests {

  private static Diagnostic ExpectSingleError(string source, string expectedCode, string expectedRule, CppImportOptions? options = null) {
    var result = CppFrontend.Import("err.cpp", source, options);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Has.Count.EqualTo(1));
    var diag = result.Diagnostics[0];
    Assert.That(diag.Code, Is.EqualTo(expectedCode));
    Assert.That(diag.Rule, Is.EqualTo(expectedRule));
    Assert.That(diag.Severity, Is.EqualTo(DiagnosticSeverity.Error));
    return diag;
  }

  [TestCase("/* unterminated comment")]
  [TestCase("int f() { char c = 'a; return 0; }")]
  [TestCase("int f() { char c = '\n'; return 0; }")]
  [TestCase("int f() { std::string s = \"abc\n\"; return 0; }")]
  [TestCase("int f() { std::string s = \"abc")]
  [TestCase("int f() { char c = '\\q'; return 0; }")]
  [TestCase("int f() { char c = '\\")]
  [TestCase("int f() { char c = '\\xZZ'; return 0; }")]
  [TestCase("int f() { return @; }")]
  [TestCase("int f( { return 0; }")]
  [TestCase("int 123() { return 0; }")]
  [TestCase("int f() { do { } (true); return 0; }")]
  [TestCase("int f() { switch (1) { int x = 0; } return 0; }")]
  [TestCase("int f() { try { } return 0; }")]
  [TestCase("int f() { auto x; return 0; }")]
  [TestCase("int f() { return 0b999; }")]
  [TestCase("int f() { return 1.2e+; }")]
  [TestCase("int f() { std::vector<int x; return 0; }")]
  [TestCase("int f() { return ; }")]
  public void SyntaxErrors_EmitPLY1400(string source)
    => ExpectSingleError(source, CppFrontend.SyntaxErrorCode, "cpp.frontend.syntax");

  [TestCase("#define FOO 1\nint f() { return 0; }")]
  [TestCase("class Foo { int x; };")]
  [TestCase("union U { int x; float y; };")]
  [TestCase("struct Base {}; struct Derived : Base {};")]
  [TestCase("struct S { virtual void f(); };")]
  [TestCase("struct S { int operator+(int x); };")]
  [TestCase("struct S { bool operator==(const S&) const = delete; };")]
  [TestCase("struct S { int m() { return 1; } };")]
  [TestCase("template<int N> int f() { return N; }")]
  [TestCase("template<typename T> struct Box { T v; };")]
  [TestCase("template<typename T> T gv = 0;")]
  [TestCase("int f() { goto end; end: return 0; }")]
  [TestCase("int f() { int* p = new int; return 0; }")]
  [TestCase("int f(int x) { return const_cast<int>(x); }")]
  [TestCase("int f(int x) { switch (x) { case 1: x = 2; case 2: return x; } return 0; }")]
  [TestCase("int f(double x) { switch (x) { default: return 0; } }")]
  [TestCase("int f(int x) { for (auto y : x) {} return 0; }")]
  [TestCase("int f() { throw 42; }")]
  [TestCase("int f(std::span<const int> s) { s[0] = 1; return 0; }")]
  [TestCase("int f() { 1 = 2; return 0; }")]
  [TestCase("int f() { auto x = std::nullopt; return 0; }")]
  [TestCase("int f() { auto x = nullptr; return 0; }")]
  [TestCase("int f(int x) { return *x; }")]
  [TestCase("int f() { int x = (1 + 2)++; return x; }")]
  [TestCase("int f(int x) { return x == nullptr ? 1 : 0; }")]
  [TestCase("int f(std::string a, std::string b) { return (a * b).size(); }")]
  [TestCase("int f(int a[2]) { int y = (a[0] = 1); return y; }")]
  [TestCase("int f(int x) { return reinterpret_cast<double>(x); }")]
  [TestCase("int f(int x) { return x[0]; }")]
  [TestCase("int f() { auto x = {1, 2}; return 0; }")]
  [TestCase("enum E { A = foo() };")]
  [TestCase("template<typename T> T id(T x) { return x; } int f() { return id<4>(1); }")]
  public void UnsupportedConstructs_EmitPLY1401(string source)
    => ExpectSingleError(source, CppFrontend.UnsupportedConstructCode, "cpp.frontend.construct");

  [Test]
  public void UnsupportedStandard_EmitsPLY1401()
    => ExpectSingleError("int f() { return 0; }", CppFrontend.UnsupportedConstructCode, "cpp.frontend.construct",
      new CppImportOptions { Standard = "cpp98" });

  [TestCase("UnknownType f() { return 0; }")]
  [TestCase("int f(void* p) { return 0; }")]
  [TestCase("enum class Bad : float { A = 0 };")]
  [TestCase("struct Bad { std::string s; };")]
  [TestCase("int f(std::span<int, 2> s) { return 0; }")]
  [TestCase("int f(std::array<int> a) { return 0; }")]
  [TestCase("int f(std::vector<int, int> v) { return 0; }")]
  [TestCase("int f(std::optional<int, int> o) { return 0; }")]
  [TestCase("int f(std::optional<std::string> o) { return 0; }")]
  [TestCase("int f() { try {} catch (const UnknownEx&) {} return 0; }")]
  [TestCase("struct P { int x; }; int f(P p) { return static_cast<int>(p); }")]
  public void UnsupportedTypes_EmitPLY1402(string source)
    => ExpectSingleError(source, CppFrontend.UnsupportedTypeCode, "cpp.frontend.type");

  [TestCase("int f() { return unknown_fn(1); }")]
  [TestCase("int g(int x) { return x; } int f() { return g(1, 2); }")]
  [TestCase("int f(int x) { return (x)(1); }")]
  [TestCase("template<typename T> T id(T x) { return x; } int f() { return id(1, 2); }")]
  [TestCase("template<typename T> T id(T x) { return x; } int f() { return id<int, int>(1); }")]
  [TestCase("template<typename T, typename U> T first(T x) { return x; } int f() { return first(1); }")]
  [TestCase("int f(int x) { return std::bit_cast<double>(x); }")]
  [TestCase("int f(int a, int b) { std::memcpy(a, b, 4); return 0; }")]
  [TestCase("int f(int a) { std::fill(a, 1); return 0; }")]
  [TestCase("int f(int a) { std::fill(a, a, 1); return 0; }")]
  [TestCase("int f(int a, int b) { std::copy(a, b); return 0; }")]
  [TestCase("int f(std::optional<int> o) { return o.reset(); }")]
  [TestCase("int f(std::string s) { return s.push_back('a'); }")]
  [TestCase("int f(std::span<int> s) { return s.unknown(); }")]
  [TestCase("int f(std::array<int, 2> a) { return a.unknown(); }")]
  [TestCase("int f() { std::vector<int> v; return v.unknown(); }")]
  [TestCase("int f(int x) { return x.unknown(); }")]
  public void UnsupportedCalls_EmitPLY1403(string source)
    => ExpectSingleError(source, CppFrontend.UnsupportedCallCode, "cpp.frontend.call");

  [TestCase("int f() { return missing_var; }")]
  [TestCase("int f() { missing_var = 1; return 0; }")]
  [TestCase("struct P { int x; }; int f(P p) { return p.missing; }")]
  [TestCase("struct P { int x; }; int f(P p) { p.missing = 1; return 0; }")]
  public void UnresolvedSymbols_EmitPLY1404(string source)
    => ExpectSingleError(source, CppFrontend.UnresolvedSymbolCode, "cpp.frontend.symbol");

  [Test]
  public void DiagnosticDescriptors_HaveExpectedMetadata() {
    var descriptors = new[] {
      (CppFrontend.SyntaxError, CppFrontend.SyntaxErrorCode, ExitCode.InvalidSource, "cpp.frontend.syntax"),
      (CppFrontend.UnsupportedConstruct, CppFrontend.UnsupportedConstructCode, ExitCode.UnsupportedConstruct, "cpp.frontend.construct"),
      (CppFrontend.UnsupportedType, CppFrontend.UnsupportedTypeCode, ExitCode.UnsupportedConstruct, "cpp.frontend.type"),
      (CppFrontend.UnsupportedCall, CppFrontend.UnsupportedCallCode, ExitCode.UnsupportedConstruct, "cpp.frontend.call"),
      (CppFrontend.UnresolvedSymbol, CppFrontend.UnresolvedSymbolCode, ExitCode.InvalidSource, "cpp.frontend.symbol")
    };
    Assert.That(descriptors.All(d => d.Item1.Code == d.Item2 && d.Item1.ExitCode == d.Item3 && d.Item1.Rule == d.Item4), Is.True);
  }

}
