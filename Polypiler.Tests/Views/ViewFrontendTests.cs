using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Views;

/// <summary>
/// The views in both frontends (#1325): the C# members of <see cref="CSharpViewApi"/>, the constructs both
/// refuse by name, and <c>subarray</c>, <c>slice</c>, <c>set</c> and <c>fill</c> of JavaScript with the clamping
/// of ECMA-262.
/// </summary>
[TestFixture]
public sealed class ViewFrontendTests {

  private const string Usings = "using System; using System.Runtime.InteropServices; ";

  private static string Refused(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", Usings + PrototypeFixture.Wrap(methods));
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  /// <summary>Test design 14: every construct the table diagnoses is refused with PLY1002, naming it.</summary>
  [TestCase("public static int F(int[] a) { ref int r = ref MemoryMarshal.GetReference(a.AsSpan()); return r; }", "MemoryMarshal.GetReference")]
  [TestCase("public static int F(int[] a) { Span<int> s = MemoryMarshal.CreateSpan(ref a[0], 1); return s[0]; }", "MemoryMarshal.CreateSpan")]
  [TestCase("public static int F(int[] a) { ReadOnlySpan<int> s = MemoryMarshal.CreateReadOnlySpan(ref a[0], 1); return s[0]; }", "MemoryMarshal.CreateReadOnlySpan")]
  [TestCase("public static int F(int[] a) { ref int r = ref a[0]; return r; }", "a ref expression")]
  [TestCase("public static int F(Span<bool> a) => 1;", "System.Span<bool>")]
  [TestCase("public static int F(Span<char> a) => 1;", "System.Span<char>")]
  [TestCase("public static int F() { Span<int> s = stackalloc int[] { 1, 2 }; return s[0]; }", "stackalloc with an initializer")]
  [TestCase("public static int F(int[] a) { a.AsSpan().Reverse(); return a[0]; }", "MemoryExtensions.Reverse")]
  [TestCase("public static int F(int[] a) => a.AsSpan().IndexOf(3);", "MemoryExtensions.IndexOf")]
  [TestCase("public static int F(byte[] a) { Span<uint> w = MemoryMarshal.Cast<byte, uint>(a.AsSpan()); return (int)w[0]; }", "MemoryMarshal.Cast into a writable span")]
  [TestCase("public static int F(byte[] a) => MemoryMarshal.Read<byte>(a);", "MemoryMarshal.Read of u8")]
  [TestCase("public static void F(byte[] a, int d) { MemoryMarshal.Write(a.AsSpan(), 10 / d); }", "with a value that can trap or has an effect")]
  [TestCase("public static int F(int[] a) => a.AsSpan(1..2).Length;", "MemoryExtensions.AsSpan of anything but an array, at an int")]
  [TestCase("public static int F(int[] a, int i) => a.AsSpan(i)[1..^1].Length;", "an index from the end of a view that is not a variable")]
  [TestCase("public static int F(int[] a, int i) { ReadOnlySpan<int> s = a; return s[(i + 1)..(i + 2)].Length; }", "a range whose start is more than a variable or a constant")]
  [TestCase("public static int F(int[] a, int i) => a.AsSpan(i)[^1];", "an index from the end of an expression that is not a variable")]
  [TestCase("public static int F(int[] a) => new Span<int>(ref a[0]).Length;", "new span<i32> of a reference")]
  [TestCase("public static int F(long i) { ReadOnlySpan<int> s = new int[2]; return s[..(int)i].Length + s[(Index)i]; }", "System.Index")]
  public void DiagnosedConstruct_IsRefusedByName(string methods, string named) {
    var refused = Refused(methods);
    Assert.That(refused, Does.Contain(CSharpFrontend.Unsupported).And.Contain(named));
  }

  /// <summary>The table: every member once, found by its type, name and number of arguments, and none outside it.</summary>
  [Test]
  public void Table_FindsItsMembers() {
    Assert.That(CSharpViewApi.Members.Select(member => (member.Type, member.Member)), Is.Unique);
    Assert.That(CSharpViewApi.Find("System.Span<T>", "Slice", 3)!.Ir, Is.EqualTo("Slice"));
    Assert.That(CSharpViewApi.Find("System.Span<T>", "Slice", 4), Is.Null);
    Assert.That(CSharpViewApi.Find("System.Span<T>", "Reverse", 1), Is.Null);
    Assert.That(CSharpViewApi.Members.Select(member => member.Ir).Distinct(), Is.SubsetOf(IrViews.Operations.Select(op => op.ToString()).Append("LoadLittle").Append("StoreLittle")));
    Assert.That((CSharpViewApi.Syntax.Count, CSharpViewApi.Diagnosed.Count), Is.EqualTo((12, 8)));
  }

  /// <summary>Measurement "share of the construct table covered by fixtures": every member and every syntax of the table stands in a fixture.</summary>
  [Test]
  public void EveryMember_StandsInAFixture() {
    var fixtures = string.Join("\n", Directory.GetFiles(SpanFixtures.PathOf(""), "*.cs").Select(File.ReadAllText));
    foreach (var member in CSharpViewApi.Members.Select(member => member.Member).Distinct())
      Assert.That(fixtures, Does.Contain("." + member + (member is "Cast" or "Read" ? "<" : "(")), member);

    foreach (var construct in new[] { "stackalloc", "[^4..]", "[2..6]", "[..3]", "foreach", "IsEmpty", ".Length", "new Span<byte>(d, 1, 2)", "BinaryPrimitives.Write" })
      Assert.That(fixtures, Does.Contain(construct), construct);
  }

  /// <summary>
  /// <c>[SkipLocalsInit]</c> needs unsafe code, which the subset does not compile: the compiler refuses the
  /// method before anything could read a stackalloc's unspecified contents.
  /// </summary>
  [Test]
  public void SkipLocalsInit_IsRefusedByTheCompiler() {
    var source = Usings + PrototypeFixture.Wrap("[System.Runtime.CompilerServices.SkipLocalsInit] public static int F() { Span<int> s = stackalloc int[4]; return s[3]; }");
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That((result.Module, result.Diagnostics.Single().Message), Is.EqualTo(((IrModule?)null, result.Diagnostics.Single().Message)));
    Assert.That(result.Diagnostics.Single().ToString(), Does.Contain(CSharpFrontend.CompilerError).And.Contain("CS0227"));
  }

  // ---------------------------------------------------------------- JavaScript

  private static string JavaScriptRefused(string function) {
    var result = JavaScriptFrontend.Import("f.js", function);
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  /// <summary>Test design 14 for JavaScript: a reinterpretation in the host's order, the buffer used otherwise, a view of a plain Array.</summary>
  [TestCase("/** @param {uint8[]} b @returns {uint32} */ export function f(b) { return new Uint32Array(b.buffer, 4, 1)[0]; }", "Uint32Array")]
  [TestCase("/** @param {uint8[]} b @returns {int32} */ export function f(b) { const x = b.buffer; return 1; }", "b.buffer")]
  [TestCase("/** @param {int32} n @returns {int32} */ export function f(n) { const a = [1, 2, 3]; return a.subarray(1).length; }", "subarray on a plain Array")]
  [TestCase("/** @param {uint8[]} b @returns {uint32} */ export function f(b) { const v = b.subarray(1); const d = new DataView(v.buffer); return d.getUint32(0, true); }", "a DataView of span<u8>")]
  [TestCase("/** @param {uint8[]} b @param {int32} i @returns {int32} */ export function f(b, i) { return b.subarray(g(i)).length; } /** @param {int32} i @returns {int32} */ function g(i) { return i; }", "whose array or positions are more than")]
  [TestCase("/** @param {uint8[]} b @returns {int32} */ export function f(b) { return b.subarray(1, 2, 3).length; }", "subarray with 3 arguments")]
  [TestCase("/** @param {uint8[]} b @returns {int32} */ export function f(b) { b.set(b, 1, 2); return 1; }", "set with 3 arguments")]
  [TestCase("/** @param {uint8[]} b @returns {int32} */ export function f(b) { b.fill(); return 1; }", "fill with 0 arguments")]
  [TestCase("/** @param {uint8[]} b @param {int32[]} c @returns {int32} */ export function f(b, c) { b.set(c); return 1; }", "set of int32[] into uint8[]")]
  [TestCase("/** @param {uint8[]} b @returns {int32} */ export function f(b) { return b.subarray(true).length; }", "a position of boolean")]
  public void JavaScriptConstruct_IsRefusedByName(string function, string named)
    => Assert.That(JavaScriptRefused(function), Does.Contain("PLY1102").And.Contain(named));

  /// <summary>Test design 7: the positions of <c>subarray</c> clamp as ECMA-262 says, also those that are doubles; <c>slice</c> copies and <c>subarray</c> aliases.</summary>
  [TestCase("13, 17", 3)]
  [TestCase("5, 2", 0)]
  [TestCase("-4", 4)]
  [TestCase("0, -5", 11)]
  [TestCase("-20, 3", 3)]
  [TestCase("16", 0)]
  [TestCase("1.5, 4.9", 3)]
  [TestCase("-1.5", 1)]
  [TestCase("0, 0 / 0", 0)]
  [TestCase("", 16)]
  public void Subarray_Clamps(string positions, int length) {
    var module = JavaScriptFrontend.Import("f.js", $"/** @param {{uint8[]}} b @returns {{int32}} */ export function f(b) {{ return b.subarray({positions}).length; }}").Module!;
    Assert.That(IrInterpreter.Call(module, "f", [SpanFixtures.D]), Is.EqualTo(length));
  }

  /// <summary>The functions of <c>views.mjs</c> in the interpreter, with the values node gives: "3,0,4,11" and "0,99".</summary>
  [Test]
  public void ViewsFixture_ComputesWhatNodeComputes() {
    var module = JavaScriptFrontend.Import("views.mjs", File.ReadAllText(SpanFixtures.PathOf("views.mjs"))).Module!;
    Assert.That(IrInterpreter.Call(module, "word", [SpanFixtures.D]), Is.EqualTo(84148994u));
    Assert.That(IrInterpreter.Call(module, "tail", [SpanFixtures.D]), Is.EqualTo(54.0));
    Assert.That(IrInterpreter.Call(module, "lengths", [SpanFixtures.D]), Is.EqualTo(new[] { 3, 0, 4, 11 }));
    Assert.That(IrInterpreter.Call(module, "sliceVsSub", [SpanFixtures.D]), Is.EqualTo(new[] { 0, 99 }));
    Assert.That(IrInterpreter.Call(module, "copied", [SpanFixtures.D, 2]), Is.EqualTo(2.0 + 3 + 4 + 5 + 7 * 10 + 14 + 15));
    Assert.That(() => IrInterpreter.Call(module, "copied", [SpanFixtures.D, -2]), Throws.Nothing);
    var adler = JavaScriptFrontend.Import("adler32-view.mjs", File.ReadAllText(SpanFixtures.PathOf("adler32-view.mjs"))).Module!;
    Assert.That(IrInterpreter.Call(adler, "compute", [SpanFixtures.Wikipedia, 5, 9]), Is.EqualTo(0x11E60398u));
  }

  /// <summary><c>set</c> into a view too short is the trap of a copy, which JavaScript throws as a RangeError.</summary>
  [TestCase(12, null)]
  [TestCase(13, "argument out of range")]
  [TestCase(-1, "argument out of range")]
  public void Set_TrapsWhereTheSourceDoesNotFit(int offset, string? trap) {
    var module = JavaScriptFrontend.Import("f.js", "/** @param {uint8[]} b @param {int32} o @returns {int32} */ export function f(b, o) { b.set(b.subarray(0, 4), o); return b[15]; }").Module!;
    var outcome = PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("f", [SpanFixtures.D, offset]));
    Assert.That(outcome, trap is null ? Does.StartWith("3 | ") : Is.EqualTo("trap: " + trap));
  }

}
