using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Pointers;

/// <summary>
/// The bounded pointers in the C# backend (#1331): unsafe code in every profile, net2.0 to net10.0, in one form; the
/// generated C# of each profile compiles against its reference assemblies with AllowUnsafeBlocks and computes what
/// .NET's build of the fixture computes for every in-bounds call.
/// </summary>
[TestFixture]
public sealed class PointerCSharpTests {

  private static IEnumerable<TestCaseData> Profiles() => CSharpProfile.All.Select(profile => new TestCaseData(profile.Name).SetArgDisplayNames(profile.Name));

  private static IrModule? _pointers;

  private static IrModule Pointers => _pointers ??= PointerFixtures.Imported("Pointers");

  /// <summary>Test design 2 and 14: the fixture compiles in every profile and computes what .NET computes, FNV-1a too.</summary>
  [TestCaseSource(nameof(Profiles))]
  public void Fixture_ComputesInEveryProfileWhatDotNetComputes(string name) {
    var profile = CSharpProfile.Parse(name);
    Assert.That(CSharpEmitter.Check(Pointers, profile), Is.Empty);
    var text = CSharpEmitter.Emit(Pointers, profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, false);
    foreach (var call in PointerFixtures.Calls())
      Assert.That(build.Outcome(call), Is.EqualTo(PointerFixtures.DotNet("Pointers", call)), $"{call.Method} in {name}\n{text}");

    var fnv = CSharpEmitter.Emit(PointerFixtures.Imported("Fnv1aPointer"), profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(fnv, profile.Name, profile.LanguageVersionName), Is.Empty, fnv);
    Assert.That(GeneratedCSharp.Compile(fnv, false).Outcome(new PrototypeCall("Hash", ["a"u8.ToArray()])).Split(" | ")[0], Is.EqualTo("3826002220"));
  }

  /// <summary>The C# stays unsafe code, and says so: an unsafe class, the reinterpreting read as it was, the header's note.</summary>
  [Test]
  public void Output_IsUnsafeCodeAndSaysSo() {
    var text = CSharpEmitter.Emit(Pointers, CSharpProfile.Net20).Single().Content;
    Assert.That(text, Does.Contain("public static class Pointers").And.Contain("static unsafe uint Checksum(").And.Not.Contain("unsafe class"));
    Assert.That(text, Does.Contain("*(uint*)(p + at)"));
    Assert.That(text, Does.Contain("AllowUnsafeBlocks"));
    Assert.That(text, Does.Contain("fixed (byte* start = data)"));
    Assert.That(text, Does.Contain("int* p = stackalloc int[8];"));
    Assert.That(text, Does.Contain("&x"));
    Assert.That(text, Does.Contain("void* v = p;"));
    var plain = CSharpEmitter.Emit(PrototypeFixture.Import("Plain.cs", "public static class Plain { public static int F(int x) => x; }"), CSharpProfile.Net20).Single().Content;
    Assert.That(plain, Does.Not.Contain("unsafe").And.Not.Contain("AllowUnsafeBlocks"));
  }

  /// <summary>Only the methods that hold pointer code are <c>unsafe</c>: no class is, and no other method.</summary>
  [TestCase("net2.0")]
  [TestCase("net10.0")]
  public void Unsafe_MarksOnlyTheMethodsWithPointers(string name) {
    var module = PrototypeFixture.Import("Snippet.cs", "public static class Snippet { "
      + "private static unsafe int First(int[] a) { fixed (int* p = a) { return *p; } } "
      + "public static int G(int[] a) => First(a) + a.Length; }");
    var profile = CSharpProfile.Parse(name);
    var text = CSharpEmitter.Emit(module, profile).Single().Content;
    Assert.That(text, Does.Not.Contain("unsafe class").And.Contain("AllowUnsafeBlocks"), text);
    Assert.That(text, Does.Contain("private static unsafe int First(").And.Contain("public static int G("), text);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("G", [new[] { 4, 5 }])), Is.EqualTo("6 | [4,5]"));
  }

  /// <summary>Test design 14: no profile lacks unsafe code or stackalloc into a pointer; a table without them refuses or pins.</summary>
  [Test]
  public void EveryProfile_HasUnsafeCode() {
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpCapabilities.Default.Has(CSharpFeature.UnsafeCode, profile), Is.True, profile.Name);
      Assert.That(CSharpCapabilities.Default.Has(CSharpFeature.StackAllocPointer, profile), Is.True, profile.Name);
    }

    var withoutUnsafe = new CSharpCapabilities(CSharpCapabilities.Default.Rows.Where(row => row.Feature != CSharpFeature.UnsafeCode));
    Assert.That(CSharpEmitter.Check(Pointers, CSharpProfile.Net100, withoutUnsafe).Select(diagnostic => diagnostic.ToString()),
      Has.Some.Contains(CSharpEmitter.Inexpressible).And.Some.Contains("'unsafe code' cannot be expressed in C# profile 'net10.0'"));

    var withoutStack = new CSharpCapabilities(CSharpCapabilities.Default.Rows.Where(row => row.Feature != CSharpFeature.StackAllocPointer));
    var pinned = CSharpEmitter.Emit(Pointers, CSharpProfile.Net100, null, withoutStack).Single().Content;
    Assert.That(pinned, Does.Contain("fixed (int* p = new int[8])").And.Not.Contain("stackalloc"));
    Assert.That(GeneratedCSharp.Compile(pinned, false).Outcome(new PrototypeCall("Stack", [3])), Is.EqualTo("29"));
  }

  /// <summary>Test design 7: stackalloc stays for 256 bytes and becomes a pinned array above 1024 bytes, in a loop and for a length not known.</summary>
  [TestCase(64, true)]
  [TestCase(255, true)]
  [TestCase(256, false)]
  public void StackAlloc_StaysBelow1024Bytes(int count, bool onStack) {
    var n = new IrLocal("n", IrType.I32);
    var pointer = new IrLocal("p", IrType.Pointer(IrType.I32));
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [n], [pointer], new IrBlock([
      new IrDeclare(pointer, new IrIntrinsic(IrIntrinsicOp.StackAlloc, pointer.Type, [new IrConst(IrType.I32, count)])),
      new IrAssign(pointer, new IrIntrinsic(IrIntrinsicOp.PointerAdd, pointer.Type, [new IrLocalRef(pointer), new IrLocalRef(n)])),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.I32, [new IrLocalRef(pointer)]))
    ]))]);
    var text = CSharpEmitter.Emit(module, CSharpProfile.Net100).Single().Content;
    Assert.That(text.Contains("stackalloc", System.StringComparison.Ordinal), Is.EqualTo(onStack), text);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net10.0", CSharpProfile.Net100.LanguageVersionName), Is.Empty, text);
    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("F", [count - 1])), Is.EqualTo("0"));
  }

  /// <summary>A pinned span is a fixed of the span with spans, and of the segment's array without, the empty one the null pointer.</summary>
  [TestCase("net2.0")]
  [TestCase("netstandard2.0")]
  [TestCase("net10.0")]
  public void PinnedSpan_IsWrittenInEveryForm(string name) {
    var module = PrototypeFixture.Import("Snippet.cs", "using System; public static class Snippet { "
      + "public static unsafe int F(int[] a, int s, int n) { Span<int> v = a.AsSpan(s, n); fixed (int* p = v) { return p == null ? -1 : *p; } } "
      + "public static unsafe int G(int[] a) { fixed (int* p = a.AsSpan(1, 2)) { return p[1]; } } "
      + "public static unsafe int H(int[] a, int i) { Span<int> v = a; fixed (int* p = &v[i]) { return *p; } } }");
    var profile = CSharpProfile.Parse(name);
    var text = CSharpEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, false);
    Assert.That(build.Outcome(new PrototypeCall("F", [new[] { 1, 2, 3 }, 1, 2])), Is.EqualTo("2 | [1,2,3]"));
    Assert.That(build.Outcome(new PrototypeCall("F", [new[] { 1, 2, 3 }, 1, 0])), Is.EqualTo("-1 | [1,2,3]"));
    Assert.That(build.Outcome(new PrototypeCall("G", [new[] { 1, 2, 3 }])), Is.EqualTo("3 | [1,2,3]"));
    Assert.That(build.Outcome(new PrototypeCall("H", [new[] { 1, 2, 3 }, 2])), Is.EqualTo("3 | [1,2,3]"));
  }

}
