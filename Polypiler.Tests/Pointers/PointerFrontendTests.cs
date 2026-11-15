using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Pointers;

/// <summary>
/// The unsafe subset of the C# frontend (#1331): the fixtures import and the interpreter computes what .NET's build
/// of them computes for every in-bounds call, the traps the IR adds to C#'s undefined behaviour, and every construct
/// of the table that is refused, by name.
/// </summary>
[TestFixture]
public sealed class PointerFrontendTests {

  private const string Usings = "using System; using System.Runtime.InteropServices; using System.Runtime.CompilerServices; ";

  private static IrModule? _pointers;

  private static IrModule Pointers => _pointers ??= PointerFixtures.Imported("Pointers");

  private static readonly IReadOnlyList<PrototypeCall> _calls = [.. PointerFixtures.Calls()];

  private static IEnumerable<int> CallIndices => Enumerable.Range(0, PointerFixtures.Calls().Count());

  private static string Refused(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", Usings + PrototypeFixture.Wrap(methods));
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  private static IrModule Imported(string methods) => PrototypeFixture.Import("Snippet.cs", Usings + PrototypeFixture.Wrap(methods));

  /// <summary>Test design 2 and 9: every in-bounds call of the fixture, interpreted, is what .NET computes.</summary>
  [TestCaseSource(nameof(CallIndices))]
  public void Interpreter_IsDotNet(int call)
    => Assert.That(PrototypeFixture.InterpretedOutcome(Pointers, _calls[call]), Is.EqualTo(PointerFixtures.DotNet("Pointers", _calls[call])));

  /// <summary>The values of the scenario, in .NET and in the interpreter.</summary>
  [Test]
  public void ScenarioValues_HoldInDotNetAndTheInterpreter() {
    (PrototypeCall Call, string Expected)[] cases = [
      (new("Checksum", [PointerFixtures.D]), "3838341128"), (new("Word", [PointerFixtures.D, 1]), "67305985"),
      (new("Distance", [new int[10], 2, 7]), "5"), (new("Distance", [new int[10], 7, 2]), "-5"),
      (new("Stack", [3]), "29"), (new("Local", []), "7"), (new("EmptyIsNull", []), "true"), (new("Bits", []), PrototypeFixture.Show(1.5)),
      (new("Word", [PointerFixtures.D, 12]), "252579084")
    ];
    foreach (var (call, expected) in cases) {
      Assert.That(PointerFixtures.DotNet("Pointers", call).Split(" | ")[0], Is.EqualTo(expected), call.Method);
      Assert.That(PrototypeFixture.InterpretedOutcome(Pointers, call).Split(" | ")[0], Is.EqualTo(expected), call.Method);
    }
  }

  /// <summary>Test design 1: FNV-1a 32 over a pinned array of "a" is the published 0xE40C292C, and of nothing the offset basis.</summary>
  [Test]
  public void Fnv1a_IsThePublishedValue() {
    var module = PointerFixtures.Imported("Fnv1aPointer");
    Assert.That(IrInterpreter.Call(module, "Hash", ["a"u8.ToArray()]), Is.EqualTo(0xE40C292Cu));
    Assert.That(IrInterpreter.Call(module, "Hash", [System.Array.Empty<byte>()]), Is.EqualTo(0x811C9DC5u));
    Assert.That(Polypiler.Fixtures.Prototype.Fnv1aPointer.Hash("a"u8.ToArray()), Is.EqualTo(0xE40C292Cu));
  }

  /// <summary>Test design 4, 5, 6 and 11: the traps of the IR where C# is undefined.</summary>
  [Test]
  public void Traps_AreTheIrs() {
    foreach (var (call, trap) in PointerFixtures.Traps())
      Assert.That(PrototypeFixture.InterpretedOutcome(Pointers, call), Is.EqualTo(PrototypeFixture.Trap(trap)), call.Method);
  }

  /// <summary>The fixture lowers fixed to a block that pins, sizeof to a constant and p[i] to a load of a moved pointer.</summary>
  [Test]
  public void Fixture_LowersToTheOperations() {
    var operations = Pointers.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrIntrinsic>().Select(node => node.Op).ToHashSet();
    Assert.That(operations, Is.SupersetOf(IrPointers.Operations));
    Assert.That(Pointers.Functions.Single(function => function.Name == "SumOf").Parameters[0].Type, Is.EqualTo(IrType.Pointer(IrType.I32)));
  }

  /// <summary>An unsafe block, a pointer of a method that is unsafe itself, void* both ways, and fixed of several pointers.</summary>
  [Test]
  public void UnsafeBlockAndSeveralPins_Import() {
    var module = Imported("public static int F(int[] a, int[] b) { unsafe { fixed (int* p = a, q = &b[1]) { void* v = q; return *p + *(int*)v; } } }");
    Assert.That(IrInterpreter.Call(module, "F", [new[] { 5 }, new[] { 1, 7 }]), Is.EqualTo(12));
    var viaVoid = Imported("public static unsafe int F(int[] a) { fixed (void* v = a) { return *(int*)v; } }");
    Assert.That(IrInterpreter.Call(viaVoid, "F", [new[] { 3 }]), Is.EqualTo(3));
    var moved = Imported("public static unsafe long F(long[] a) { fixed (long* p = a) { long* q = 1 + p; q--; return q[0] + *(p + 1L); } }");
    Assert.That(IrInterpreter.Call(moved, "F", [new[] { 3L, 4L }]), Is.EqualTo(7L));
    var stored = Imported("public static unsafe int F(int[] a) { fixed (int* p = a) { p[1] += 2; *p = 9; return p[1] * *p; } }");
    Assert.That(IrInterpreter.Call(stored, "F", [new[] { 1, 2 }]), Is.EqualTo(36));
    var compared = Imported("public static unsafe int F(int[] a, byte[] b) { int n = 0; fixed (int* p = a) { fixed (byte* r = b) { int* q = p + 1; if (q != p) n++; if (q > p) n++; if (q >= p) n++; if (p <= q) n++; if (null != p) n++; if ((void*)r != p) n++; } } return n; }");
    Assert.That(IrInterpreter.Call(compared, "F", [new[] { 1, 2 }, new byte[] { 1 }]), Is.EqualTo(6));
    var casts = compared.Functions[0].Body.Statements.SelectMany(IrTree.Nodes).OfType<IrIntrinsic>().Where(node => node.Op == IrIntrinsicOp.PointerCast).ToList();
    Assert.That(casts.Select(cast => cast.Type.ToString()), Is.EqualTo(new[] { "void*", "void*" }));
  }

  /// <summary>Test design 12: every construct the table diagnoses is refused with PLY1002, naming it.</summary>
  [TestCase("public static unsafe byte F() { byte* p = (byte*)0x1000; return *p; }", "a cast from int to byte*")]
  [TestCase("public static unsafe long F(byte[] a) { fixed (byte* p = a) return (long)p; }", "a cast from byte* to long")]
  [TestCase("public static unsafe ulong F(byte[] a) { fixed (byte* p = a) return (ulong)p; }", "a cast from byte* to ulong")]
  [TestCase("public static unsafe int F(int n) { int* p = (int*)n; return *p; }", "a cast from int to int*")]
  [TestCase("public static unsafe int F(delegate*<int> f) => f();", "delegate*")]
  [TestCase("public static unsafe int F(string s) { fixed (char* p = s) return p[0]; }", "char*")]
  [TestCase("public static unsafe int F() { bool b = true; bool* p = &b; return 1; }", "bool*")]
  [TestCase("struct S { public int X; } public static unsafe int F() { S s = default; S* p = &s; return 1; }", "a pointer to a struct, Snippet.S*")]
  [TestCase("static unsafe int* Kept; public static int F() => 1;", "stored in a field")]
  [TestCase("public static unsafe int F(int[] a) { int*[] all = new int*[1]; return 1; }", "int*[]")]
  [TestCase("static unsafe int* G(int* p) => p; public static int F() => 1;", "returned")]
  [TestCase("public static unsafe int F(int* p) => *p;", "as a parameter of a public method")]
  [TestCase("public static unsafe int F(int[] a) { Func<int> f = () => { int* q = null; return 1; }; return f(); }", "in a lambda or a local function")]
  [TestCase("public static unsafe int F(int[] a) { int* q = null; fixed (int* p = a) { q = p; } return *q; }", "a pointer outlives the block of its region")]
  [TestCase("public static unsafe int F(int[] a) { int* q = null; { int x = 1; q = &x; } return *q; }", "a pointer outlives the block of its region")]
  [TestCase("public static unsafe int F(int[] a) { fixed (int* p = a) { int* q = p; return *q++; } }", "a step of a pointer whose value is used")]
  [TestCase("public static unsafe int F(int[] a, bool c) { fixed (int* p = a) { int* q = c ? p : p + 1; return *q; } }", "a conditional expression of pointers")]
  [TestCase("[SkipLocalsInit] public static unsafe int F() { int* p = stackalloc int[2]; return p[1]; }", "[SkipLocalsInit]")]
  [TestCase("struct S { public int X; } public static unsafe int F() { S s = default; return (&s)->X; }", "a pointer to a struct")]
  [TestCase("public static unsafe int F(int[] a) { int x = 0; fixed (int* p = a) { int* q = p; return (int)(q - (int*)x); } }", "a cast from int to int*")]
  [TestCase("public static unsafe int F(int[] a) { fixed (int* p = a) { return *(p + 1.5); } }", "")]
  [TestCase("public static unsafe IntPtr F(int[] a) { fixed (int* p = a) { return (IntPtr)p; } }", "nint")]
  [TestCase("public static unsafe int F(int[] a) { return Unsafe.Add(ref a[0], 1); }", "Unsafe")]
  [TestCase("public static unsafe int F(int[] a) { GCHandle h = GCHandle.Alloc(a, GCHandleType.Pinned); return 1; }", "GCHandle")]
  [TestCase("public static unsafe int F() { IntPtr m = Marshal.AllocHGlobal(4); return 1; }", "nint")]
  [TestCase("public static System.Collections.Generic.IEnumerable<int> F(int[] a) { int t = 0; unsafe { fixed (int* p = a) t = *p; } yield return t; }", "a pointer in an iterator function")]
  [TestCase("public static System.Collections.Generic.IEnumerable<int> F() { int t; unsafe { int* p = stackalloc int[2]; p[1] = 3; t = p[1]; } yield return t; }", "a pointer in an iterator function")]
  public void DiagnosedConstruct_IsRefusedByName(string methods, string named) {
    var refused = Refused(methods);
    Assert.That(refused, Does.Contain(CSharpFrontend.Unsupported).Or.Contain(CSharpFrontend.CompilerError));
    Assert.That(refused, Does.Contain(named));
  }

  /// <summary>Measurement "share of the construct table covered by fixtures": every supported syntax stands in a fixture.</summary>
  [Test]
  public void EverySyntax_StandsInAFixture() {
    var fixtures = string.Join("\n", Directory.GetFiles(PointerFixtures.PathOf(""), "*.cs").Select(File.ReadAllText));
    foreach (var (syntax, _) in CSharpPointerApi.Supported)
      Assert.That(fixtures, Does.Contain(syntax), syntax);

    Assert.That(CSharpPointerApi.Supported.Select(entry => entry.Syntax), Is.Unique);
    Assert.That(CSharpPointerApi.Diagnosed, Is.Unique.And.Count.EqualTo(16));
  }

}
