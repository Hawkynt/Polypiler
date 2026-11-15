using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Interop;

/// <summary>
/// Native calls in the C# backend (#1332): the declarations of LibC in every profile, each compiling against its
/// reference assemblies - with the interop generator where <c>[LibraryImport]</c> is written - and, compiled for the
/// running runtime, calling the C library of the operating system with the results of the fixture's own build.
/// </summary>
[TestFixture]
public sealed class ForeignCSharpTests {

  private static IEnumerable<TestCaseData> Profiles() => CSharpProfile.All.Select(profile => new TestCaseData(profile.Name).SetArgDisplayNames(profile.Name));

  private static IrModule? _libc;

  private static IrModule LibC => _libc ??= InteropFixtures.Imported(InteropFixtures.LibC);

  private static readonly Dictionary<string, string> _texts = [];

  /// <summary>The C# of LibC for a profile, emitted once per run.</summary>
  private static string TextOf(string profile) {
    lock (_texts) {
      if (!_texts.TryGetValue(profile, out var text))
        _texts[profile] = text = LegalizedCSharp.Emit(LibC, CSharpProfile.Parse(profile)).Single().Content;

      return text;
    }
  }

  private static object? Call(Type type, string method, params object?[] arguments) => InteropFixtures.Invoke(InteropFixtures.Method(type, method), arguments);

  /// <summary>The form of StrLenUtf8 per profile: test design 3, 4 and 5.</summary>
  [TestCase("net2.0", "public static extern global::System.UIntPtr StrLenUtf8_native(byte[] s);")]
  [TestCase("net4.6", "public static extern global::System.UIntPtr StrLenUtf8_native(byte[] s);")]
  [TestCase("net4.8", "[global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string s")]
  [TestCase("netstandard2.0", "public static extern global::System.UIntPtr StrLenUtf8_native(byte[] s);")]
  [TestCase("netstandard2.1", "[global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string s")]
  [TestCase("netcoreapp3.1", "[global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string s")]
  [TestCase("net8.0", "public static partial global::System.UIntPtr StrLenUtf8(string s);")]
  [TestCase("net10.0", "StringMarshalling = global::System.Runtime.InteropServices.StringMarshalling.Utf8)]")]
  public void StrLenUtf8_HasTheFormOfTheProfile(string profile, string form) {
    var text = TextOf(profile);
    Assert.That(text, Does.Contain(form.Replace("public static extern global::System.UIntPtr StrLenUtf8_native", "private static extern global::System.UIntPtr StrLenUtf8_native")), text);
    var libraryImport = profile is "net8.0" or "net10.0";
    Assert.That(text, libraryImport ? Does.Contain("LibraryImport(").And.Contain("public static partial class LibC").And.Contain("AllowUnsafeBlocks")
      : Does.Not.Contain("LibraryImport(").And.Contain("public static class LibC"), text);
    foreach (var name in new[] { "StrLen", "Abs", "Div", "MemSet", "StrToL" })
      Assert.That(text, Does.Match($@"\[global::System\.Runtime\.InteropServices\.DllImport\({System.Text.RegularExpressions.Regex.Escape($"\"{InteropFixtures.Library}\"")}[^\]]*\]\n\s*public static extern [^ ]+ {name}\("), name);
  }

  /// <summary>Test design 1: every profile compiles against its reference assemblies and, run here, calls the C library as the fixture does.</summary>
  [TestCaseSource(nameof(Profiles))]
  public void EveryProfile_CompilesAndCallsTheLibrary(string name) {
    var profile = CSharpProfile.Parse(name);
    var text = TextOf(name);
    Assert.That(InteropBuild.Complaints(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var translated = InteropBuild.Compile(text);
    var fixture = InteropFixtures.CompiledLibC;
    foreach (var type in new[] { fixture, translated }) {
      Assert.That(Call(type, "StrLen", "hello"), Is.EqualTo((nuint)5), type.FullName);
      Assert.That(Call(type, "StrLen", ""), Is.EqualTo((nuint)0), type.FullName);
      Assert.That(Call(type, "StrLenUtf8", "Grüße"), Is.EqualTo((nuint)7), type.FullName);
      Assert.That(Call(type, "Abs", -7), Is.EqualTo(7));
      Assert.That(Call(type, "Abs", int.MinValue), Is.EqualTo(int.MinValue), "abs(INT_MIN) is not repaired");
      Assert.That(Call(type, "Abs", -1), Is.EqualTo(1));
      Assert.That(Call(type, "Abs", 0), Is.EqualTo(0));
      Assert.That(Fields(Call(type, "Div", 7, 2)!), Is.EqualTo((3, 1)));
      Assert.That(Fields(Call(type, "Div", -7, 2)!), Is.EqualTo((-3, -1)));
      object?[] strtol = ["ff", null, 16];
      Assert.That(Convert.ToInt64(InteropFixtures.Invoke(InteropFixtures.Method(type, "StrToL"), strtol)), Is.EqualTo(255));
    }

    foreach (var (method, arguments) in new (string, object[])[] { ("Length", ["Grüße"]), ("Length", ["hello"]), ("Quotient", [7, 2]), ("Quotient", [-7, 2]), ("Parsed", ["7f", 16]) })
      Assert.That(Call(translated, method, arguments), Is.EqualTo(Call(fixture, method, arguments)), method);
  }

  /// <summary>The quotient and the remainder of a <c>div_t</c>, whichever build's struct it is.</summary>
  private static (int, int) Fields(object value) => ((int)value.GetType().GetField("Quot")!.GetValue(value)!, (int)value.GetType().GetField("Rem")!.GetValue(value)!);

  /// <summary>Test design 7: memset over a 4-byte array with a count of 0, 3 and the length, in .NET and in the translated C# of net2.0 and net10.0.</summary>
  [TestCase("net2.0", 0, new byte[] { 0, 0, 0, 0 })]
  [TestCase("net2.0", 3, new byte[] { 171, 171, 171, 0 })]
  [TestCase("net10.0", 3, new byte[] { 171, 171, 171, 0 })]
  [TestCase("net10.0", 4, new byte[] { 171, 171, 171, 171 })]
  public void MemSet_FillsTheArrayInAndOut(string profile, int count, byte[] expected) {
    Assert.That(Call(InteropFixtures.CompiledLibC, "Filled", count), Is.EqualTo(expected));
    Assert.That(Call(InteropBuild.Compile(TextOf(profile)), "Filled", count), Is.EqualTo(expected));
    var buffer = new byte[4];
    Call(InteropBuild.Compile(TextOf(profile)), "MemSet", buffer, 0xAB, (nuint)count);
    Assert.That(buffer, Is.EqualTo(expected));
  }

  /// <summary>Test design 10: a missing library and a missing entry point throw as .NET throws, in the fixture and in the translated C#.</summary>
  [Test]
  public void MissingNativeCode_FailsAsDotNetFails() {
    var module = InteropFixtures.Imported("Missing.cs");
    var translated = InteropBuild.Compile(LegalizedCSharp.Emit(module, CSharpProfile.Net100).Single().Content);
    var fixture = typeof(InteropFixtures).Assembly.GetType("Polypiler.Fixtures.Prototype.Interop.Missing", true)!;
    foreach (var type in new[] { fixture, translated }) {
      Assert.Throws<DllNotFoundException>(() => Call(type, "NoLibrary"));
      Assert.Throws<EntryPointNotFoundException>(() => Call(type, OperatingSystem.IsWindows() ? "NoEntryWindows" : "NoEntryLinux"));
    }
  }

  /// <summary>Test design 9: sequential, explicit and packed layouts reproduced exactly, with the same size before and after.</summary>
  [Test]
  public void Layouts_AreReproduced() {
    const string types = """
      [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)] public struct A { public byte X; public int Y; }
      [StructLayout(LayoutKind.Explicit)] public struct B { [FieldOffset(0)] public int X; [FieldOffset(0)] public float Y; }
      [StructLayout(LayoutKind.Sequential)] public struct C { public byte X; public long Y; }
      """;
    var module = PrototypeFixture.Import("Snippet.cs",
      "using System.Runtime.InteropServices; " + types + " public static class Snippet { [DllImport(\"lib\")] static extern int f(A a, B b, C c); public static int G() => f(default, default, default); }");
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Net20).Single().Content;
    Assert.That(text, Does.Contain("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1, Size = 16)]"));
    Assert.That(text, Does.Contain("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit)]"));
    Assert.That(text, Does.Contain("[global::System.Runtime.InteropServices.FieldOffset(0)]\n  public float Y;"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net2.0", "2"), Is.Empty, text);
    var translated = InteropBuild.Compile(text).Assembly;
    foreach (var (name, size) in new[] { ("A", 16), ("B", 4), ("C", 16) })
      Assert.That(Marshal.SizeOf(translated.GetType(name, true)!), Is.EqualTo(size), name);
  }

  /// <summary>Test design 2: every marshalling kind in its form, in the profiles before and after the attributes and generators it needs.</summary>
  [TestCase("net2.0", "GetLastWin32Error()", "[global::System.Runtime.InteropServices.In] ref double c")]
  [TestCase("net10.0", "GetLastPInvokeError()", "in double c")]
  public void MarshallingKinds_AreWrittenInTheFormOfTheProfile(string name, string lastError, string inParameter) {
    var module = PrototypeFixture.Import("Snippet.cs", """
      using System; using System.Runtime.InteropServices;
      public struct P { public int X; public int Y; }
      public static class Snippet {
        [DllImport("lib", EntryPoint = "e", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.U1)] static extern bool f([MarshalAs(UnmanagedType.I1)] bool a, bool b, [MarshalAs(UnmanagedType.LPWStr)] string s,
          [In] int[] i, [Out] int[] o, [In, Out] int[] io, [MarshalAs(UnmanagedType.LPArray)] byte[] l, IntPtr h, UIntPtr n, out int x, ref P p, in double c);
        public static int G() {
          int[] a = new int[1];
          P p = default;
          bool r = f(true, false, "", a, a, a, new byte[1], IntPtr.Zero, (nuint)3, out int x, ref p, 1.5);
          return r ? x + p.X + Marshal.GetLastPInvokeError() : Marshal.GetLastWin32Error();
        }
      }
      """);
    var profile = CSharpProfile.Parse(name);
    var text = LegalizedCSharp.Emit(module, profile).Single().Content;
    const string Interop = "global::System.Runtime.InteropServices.";
    Assert.That(text, Does.Contain($"[{Interop}DllImport(\"lib\", EntryPoint = \"e\", CharSet = {Interop}CharSet.Unicode, CallingConvention = {Interop}CallingConvention.Cdecl, SetLastError = true, ExactSpelling = true)]"));
    Assert.That(text, Does.Contain($"[return: {Interop}MarshalAs({Interop}UnmanagedType.U1)]"));
    Assert.That(text, Does.Contain($"[{Interop}MarshalAs({Interop}UnmanagedType.I1)] bool a, bool b, [{Interop}MarshalAs({Interop}UnmanagedType.LPWStr)] string s"));
    Assert.That(text, Does.Contain($"[{Interop}In] int[] i, [{Interop}Out] int[] o, [{Interop}In] [{Interop}Out] int[] io, [{Interop}MarshalAs({Interop}UnmanagedType.LPArray)] byte[] l"));
    Assert.That(text, Does.Contain("global::System.IntPtr h, global::System.UIntPtr n, out int x, ref P p, " + inParameter));
    Assert.That(text, Does.Contain(Interop + "Marshal." + lastError));
    Assert.That(text, Does.Contain("(global::System.IntPtr)0L").Or.Contain("(global::System.IntPtr)0"));
    Assert.That(InteropBuild.Complaints(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
  }

  /// <summary>
  /// The UTF-8 wrapper forwards references, handles and numbers of the size of a pointer as they are; a private one stays
  /// private. A LibraryImport of UTF-16 keeps its StringMarshalling where LibraryImport is written and is CharSet.Unicode
  /// below; explicit ANSI, an [In] string, SetLastError and ExactSpelling of false and a pointer parameter are written again.
  /// </summary>
  [TestCase("net2.0")]
  [TestCase("net10.0")]
  public void DeclarationForms_CompileInBothEnds(string name) {
    var module = PrototypeFixture.Import("Snippet.cs", """
      using System; using System.Runtime.InteropServices;
      public static unsafe partial class Snippet {
        [LibraryImport("lib", StringMarshalling = StringMarshalling.Utf8)] private static partial int u(string s, out int a, IntPtr h, nuint n);
        [LibraryImport("lib", StringMarshalling = StringMarshalling.Utf16, SetLastError = false)] private static partial int w(string s);
        [DllImport("lib", ExactSpelling = false)] private static extern int a([MarshalAs(UnmanagedType.LPStr)] string s, [In] string t, byte* p);
        [DllImport("lib")] private static extern void r(ref IntPtr h);
        [LibraryImport("lib", StringMarshalling = StringMarshalling.Utf8)] private static partial void v(string s);
        [LibraryImport("lib")] private static partial void n(int a);
        public static int G(byte[] b) { int x = 0; u("", out x, IntPtr.Zero, 3); r(ref Unused); fixed (byte* p = b) { return x + w("") + a("", "", p); } }
        private static IntPtr Unused;
      }
      """.Replace("r(ref Unused); ", "").Replace("private static IntPtr Unused;", ""));
    var profile = CSharpProfile.Parse(name);
    var text = LegalizedCSharp.Emit(module, profile).Single().Content;
    const string Interop = "global::System.Runtime.InteropServices.";
    if (name == "net2.0") {
      Assert.That(text, Does.Contain("private static int u(string s, out int a, global::System.IntPtr h, global::System.UIntPtr n) {").And.Contain("u_native(global::System.Text.Encoding.UTF8.GetBytes(s + \"\\u0000\"), out a, h, n)"), text);
      Assert.That(text, Does.Contain($"CharSet = {Interop}CharSet.Unicode, SetLastError = false)]"), text);
      Assert.That(text, Does.Contain("private static void v(string s) {\n    v_native(global::System.Text.Encoding.UTF8.GetBytes(s + \"\\u0000\"));"), text);
    } else {
      Assert.That(text, Does.Contain($"StringMarshalling = {Interop}StringMarshalling.Utf16, SetLastError = false)]\n  private static partial int w(string s);"), text);
      Assert.That(text, Does.Contain($"[{Interop}LibraryImport(\"lib\")]\n  private static partial void n(int a);"), text);
    }

    Assert.That(text, Does.Contain($"[{Interop}DllImport(\"lib\", ExactSpelling = false)]\n  private static extern unsafe int a([{Interop}MarshalAs({Interop}UnmanagedType.LPStr)] string s, [{Interop}In] string t, byte* p);"), text);
    Assert.That(text, Does.Contain("private static extern void r(ref global::System.IntPtr h);").And.Contain("global::System.IntPtr hNative = (global::System.IntPtr)h[0];").And.Contain("h[0] = (long)hNative;"), text);
    Assert.That(InteropBuild.Complaints(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
  }

  /// <summary>A record struct of an explicit layout puts the offsets on the fields behind its parameters; a module without native calls says nothing of them.</summary>
  [Test]
  public void ExplicitRecordStruct_HasItsOffsetsOnTheFields() {
    var pair = new IrStructType("Pair", [new IrField("X", IrType.I32, true), new IrField("Y", IrType.F32, true)]) {
      IsEquatable = true, Layout = new IrStructLayout(IrLayoutKind.Explicit, 0, 0, [0, 0])
    };
    var module = new IrModule("M", [new IrFunction("G", true, IrType.Struct(pair), [], [], new IrBlock([new IrReturn(new IrStructDefault(IrType.Struct(pair)))]))]) { Types = [pair] };
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Net100).Single().Content;
    Assert.That(text, Does.Contain("record struct Pair([field: global::System.Runtime.InteropServices.FieldOffset(0)] int X, [field: global::System.Runtime.InteropServices.FieldOffset(0)] float Y)"), text);
    Assert.That(text, Does.Not.Contain("Native calls"));
    Assert.That(InteropBuild.Complaints(text, "net10.0", "14"), Is.Empty, text);
  }

  /// <summary>A handle passed by reference goes through the adapter, which writes it back to the one-element array; the translated StrToL runs.</summary>
  [Test]
  public void HandleByReference_GoesThroughTheAdapter() {
    var text = TextOf("net2.0");
    Assert.That(text, Does.Contain("private static ").And.Contain("StrToL_(string text, long[] end, int radix)").And.Contain("end[0] = (long)endNative;"), text);
    Assert.That(Call(InteropBuild.Compile(text), "Parsed", "10", 10), Is.EqualTo(11L));
  }

  /// <summary>Test design 13: the interpreter runs no foreign call; it traps InvalidOperation after evaluating the arguments.</summary>
  [Test]
  public void Interpreter_TrapsAForeignCall() {
    Assert.That(PrototypeFixture.InterpretedOutcome(LibC, new PrototypeCall("Quotient", [7, 2])), Is.EqualTo(PrototypeFixture.Trap(IrTrap.InvalidOperation)));
    Assert.That(IrForeign.Trap, Is.EqualTo("foreign call"));
  }

  /// <summary>The capability rows of the native calls, by the reference assemblies of 2026-11-14.</summary>
  [Test]
  public void CapabilityRows_FollowTheReferenceAssemblies() {
    var table = CSharpCapabilities.Default;
    Assert.That(CSharpProfile.All.Where(profile => table.HasApi("LibraryImportAttribute", profile)).Select(profile => profile.Name), Is.EqualTo(new[] { "net8.0", "net10.0" }));
    Assert.That(CSharpProfile.All.Where(profile => table.HasApi("UnmanagedType.LPUTF8Str", profile)).Select(profile => profile.Name),
      Is.EqualTo(new[] { "net4.8", "netstandard2.1", "netcoreapp3.1", "net8.0", "net10.0" }));
    Assert.That(CSharpProfile.All.Where(profile => table.HasApi("Marshal.GetLastPInvokeError", profile)).Select(profile => profile.Name), Is.EqualTo(new[] { "net8.0", "net10.0" }));
    Assert.That(CSharpProfile.All.All(profile => table.HasApi("DllImportAttribute", profile)), Is.True);
  }

}
