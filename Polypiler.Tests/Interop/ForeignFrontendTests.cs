using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Interop;

/// <summary>
/// The P/Invoke declarations and calls of the C# frontend (#1332): the fixture's declarations with their identity and
/// marshalling, calls of them as foreign calls, out and ref arguments written back, struct layouts, and the construct
/// table's refusals by name.
/// </summary>
[TestFixture]
public sealed class ForeignFrontendTests {

  private const string Usings = "using System; using System.Text; using System.Runtime.InteropServices; ";

  private static IrModule Imported(string members, string types = "")
    => PrototypeFixture.Import("Snippet.cs", $"{Usings}{types} public static partial class Snippet {{ {members} }}");

  private static string Refused(string members, string types = "") {
    var result = CSharpFrontend.Import("Snippet.cs", $"{Usings}{types} public static partial class Snippet {{ {members} }}");
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  private static IrForeignFunction Foreign(IrModule module, string name) => module.ForeignFunctions.Single(function => function.Name == name);

  /// <summary>Test design 1 and 3: both variants of LibC import, every declaration with its library, symbol and origin.</summary>
  [TestCase("LibC.Linux.cs", "libc.so.6", "i64")]
  [TestCase("LibC.Windows.cs", "msvcrt", "i32")]
  public void LibC_DeclaresItsFunctions(string file, string library, string strtol) {
    var module = InteropFixtures.Imported(file);
    Assert.That(module.ForeignFunctions.Select(function => function.Name), Is.EqualTo(new[] { "StrLen", "StrLenUtf8", "Abs", "Div", "MemSet", "StrToL" }));
    Assert.That(module.ForeignFunctions.Select(function => function.Library).Distinct(), Is.EqualTo(new[] { library }));
    Assert.That(module.ForeignFunctions.Select(function => function.Symbol), Is.EqualTo(new[] { "strlen", "strlen", "abs", "div", "memset", "strtol" }));
    Assert.That(Foreign(module, "StrLenUtf8").Origin, Is.EqualTo(IrForeignOrigin.LibraryImport));
    Assert.That(Foreign(module, "StrLen").Origin, Is.EqualTo(IrForeignOrigin.DllImport));
    Assert.That(Foreign(module, "StrToL").ReturnType.ToString(), Is.EqualTo(strtol));
    Assert.That(module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "Length", "Quotient", "Filled", "Parsed" }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>Test design 2: the marshalling of every parameter of the fixture.</summary>
  [Test]
  public void LibC_MarshalsEveryParameter() {
    var module = InteropFixtures.Imported(InteropFixtures.LibC);
    Assert.That(Foreign(module, "StrLen").Parameters[0].Marshal, Is.EqualTo(new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Ansi }));
    Assert.That(Foreign(module, "StrLen").Result, Is.EqualTo(IrMarshal.Blittable with { NativeSize = true }));
    Assert.That(Foreign(module, "StrLen").CharSet, Is.EqualTo(IrStringEncoding.Ansi));
    Assert.That(Foreign(module, "StrLenUtf8").Parameters[0].Marshal.Encoding, Is.EqualTo(IrStringEncoding.Utf8));
    Assert.That(Foreign(module, "Div").Result.Kind, Is.EqualTo(IrMarshalKind.Struct));
    Assert.That(Foreign(module, "Div").ReturnType.StructType!.Layout, Is.EqualTo(new IrStructLayout(IrLayoutKind.Sequential, 0, 0, [])));
    Assert.That(Foreign(module, "MemSet").Parameters.Select(parameter => parameter.Marshal.Kind), Is.EqualTo(new[] { IrMarshalKind.Array, IrMarshalKind.Blittable, IrMarshalKind.Blittable }));
    Assert.That(Foreign(module, "MemSet").Parameters[0].Marshal.Direction, Is.EqualTo(IrMarshalDirection.InOut));
    Assert.That(Foreign(module, "MemSet").Parameters[2].Type, Is.EqualTo(IrType.U64));
    Assert.That(Foreign(module, "StrToL").Parameters[1], Is.EqualTo(new IrForeignParameter("end", IrType.Array(IrType.I64), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.Out) { NativeSize = true })));
  }

  /// <summary>A call of a native function from translated code is a foreign call; a discarded out argument a new one-element array.</summary>
  [Test]
  public void Calls_AreForeignCalls() {
    var module = InteropFixtures.Imported(InteropFixtures.LibC);
    var calls = module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrForeignCall>().ToList();
    Assert.That(calls.Select(call => call.Function), Is.EquivalentTo(new[] { "StrLen", "StrLenUtf8", "Div", "Div", "MemSet", "StrToL", "Abs" }));
    Assert.That(calls.Single(call => call.Function == "StrToL").Arguments[1], Is.TypeOf<IrNewArray>());
    Assert.That(calls.Single(call => call.Function == "MemSet").Arguments[2].Type, Is.EqualTo(IrType.U64));
  }

  /// <summary>An out, a ref and an in argument of a local: a one-element array, the element written back to the local after the call.</summary>
  [Test]
  public void ByReference_WritesBack() {
    var module = Imported("""
      [DllImport("libc.so.6")] static extern int f(out int a, ref long b, in double c);
      public static long G() { long b = 2; double c = 1.5; int r = f(out int a, ref b, in c); r = f(out a, ref b, in c); f(out _, ref b, in c); return a + b + r; }
      """);
    var body = module.Functions.Single().Body.Statements;
    Assert.That(IrTree.Nodes(module.Functions.Single().Body).OfType<IrForeignCall>().Count(), Is.EqualTo(3));
    Assert.That(body.OfType<IrAssign>().Count(assign => assign.Value is IrArrayLoad), Is.EqualTo(4));
    Assert.That(body.OfType<IrDeclare>().Count(declare => declare.Local.Name == "a" && declare.Initializer is IrArrayLoad), Is.EqualTo(1));
    Assert.That(Foreign(module, "f").Parameters.Select(parameter => parameter.Marshal.Direction), Is.EqualTo(new[] { IrMarshalDirection.Out, IrMarshalDirection.InOut, IrMarshalDirection.In }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>The attributes of a declaration and of its parameters, each kept.</summary>
  [Test]
  public void Attributes_AreKept() {
    var module = Imported("""
      [DllImport("lib", EntryPoint = "e", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl, SetLastError = true, ExactSpelling = false)]
      [return: MarshalAs(UnmanagedType.U1)] static extern bool f([MarshalAs(UnmanagedType.I1)] bool a, bool b, [MarshalAs(UnmanagedType.LPUTF8Str)] string s,
        [MarshalAs(UnmanagedType.LPWStr)] string t, [In] int[] i, [Out] int[] o, [In, Out] int[] io, [MarshalAs(UnmanagedType.LPArray)] byte[] l, IntPtr h);
      public static int G() => f(true, false, "", "", new int[1], new int[1], new int[1], new byte[1], IntPtr.Zero) ? Marshal.GetLastWin32Error() : Marshal.GetLastPInvokeError();
      """);
    var function = Foreign(module, "f");
    Assert.That((function.EntryPoint, function.CharSet, function.Convention, function.SetLastError, function.ExactSpelling),
      Is.EqualTo(("e", (IrStringEncoding?)IrStringEncoding.Unicode, IrCallingConvention.Cdecl, (bool?)true, (bool?)false)));
    Assert.That(function.Result, Is.EqualTo(new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.U1, Attributed = true }));
    Assert.That(function.Parameters.Select(parameter => IrForeign.Describe(parameter.Marshal)),
      Is.EqualTo(new[] { "Bool", "Bool", "String(Utf8)", "String(Unicode)", "Array(In)", "Array(Out)", "Array(InOut)", "Array(InOut)", "Handle" }));
    Assert.That(function.Parameters[0].Marshal.BoolForm, Is.EqualTo(IrBoolForm.I1));
    Assert.That(function.Parameters[7].Marshal.Attributed, Is.True);
    Assert.That(IrTree.Nodes(module.Functions.Single().Body).OfType<IrLastError>().Select(error => error.PInvoke), Is.EqualTo(new[] { false, true }));
  }

  /// <summary>Every calling convention of <c>[DllImport]</c>, kept as the source wrote it.</summary>
  [TestCase("Winapi", IrCallingConvention.Winapi)]
  [TestCase("Cdecl", IrCallingConvention.Cdecl)]
  [TestCase("StdCall", IrCallingConvention.StdCall)]
  [TestCase("ThisCall", IrCallingConvention.ThisCall)]
  [TestCase("FastCall", IrCallingConvention.FastCall)]
  public void CallingConventions_AreKept(string convention, IrCallingConvention expected)
    => Assert.That(Foreign(Imported($"[DllImport(\"lib\", CallingConvention = CallingConvention.{convention})] static extern void f();"), "f").Convention, Is.EqualTo(expected));

  /// <summary>A LibraryImport of UTF-16 that keeps the error; one without strings, which needs no StringMarshalling; a handle as the result; a bool marshalled as BOOL by name.</summary>
  [Test]
  public void LibraryImportAndResults_AreKept() {
    var module = Imported("""
      [LibraryImport("lib", EntryPoint = "e", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] private static partial int f(string s);
      [LibraryImport("lib")] private static partial void g(int a);
      [DllImport("lib")] static extern IntPtr h([MarshalAs(UnmanagedType.Bool)] bool b, [MarshalAs(UnmanagedType.LPStr)] string s, [In] string t);
      public static long G() { g(f("") + 1); return (long)h(true, "", ""); }
      """);
    Assert.That((Foreign(module, "f").CharSet, Foreign(module, "f").SetLastError, Foreign(module, "f").Symbol), Is.EqualTo(((IrStringEncoding?)IrStringEncoding.Unicode, (bool?)true, "e")));
    Assert.That(Foreign(module, "f").Parameters[0].Marshal.Encoding, Is.EqualTo(IrStringEncoding.Unicode));
    Assert.That(Foreign(module, "g").CharSet, Is.Null);
    Assert.That((Foreign(module, "h").ReturnType, Foreign(module, "h").Result.Kind), Is.EqualTo((IrType.I64, IrMarshalKind.Handle)));
    Assert.That(Foreign(module, "h").Parameters.Select(parameter => parameter.Marshal), Is.EqualTo(new[] {
      new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.Bool, Attributed = true }, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Ansi, Attributed = true },
      new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Ansi, Directed = true } }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>A native function of no result passing a local by reference, a discard declared with var, and numbers of the size of a pointer from a native call.</summary>
  [Test]
  public void ReferencesAndNativeSizes_Lower() {
    var module = Imported("""
      [DllImport("lib")] static extern void g(ref int a, out long b);
      [DllImport("lib")] static extern nuint n();
      [DllImport("lib")] static extern void m(nuint a, nuint b, IntPtr c);
      public static int G() { int a = 1; g(ref a, out var _); m(n(), (nuint)n(), IntPtr.Zero); return a; }
      """);
    var body = module.Functions.Single().Body.Statements;
    Assert.That(body.OfType<IrExprStmt>().Select(statement => ((IrForeignCall)statement.Expression).Function), Is.EqualTo(new[] { "g", "m" }));
    Assert.That(body.OfType<IrAssign>().Single().Value, Is.TypeOf<IrArrayLoad>());
    var m = body.OfType<IrExprStmt>().Select(statement => (IrForeignCall)statement.Expression).Last();
    Assert.That(m.Arguments.Select(argument => argument.Type), Is.EqualTo(new[] { IrType.U64, IrType.U64, IrType.I64 }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>Test design 9: sequential, explicit and packed layouts.</summary>
  [Test]
  public void Layouts_AreKept() {
    var module = Imported("""
      [DllImport("lib")] static extern int f(A a, B b, C c);
      public static int G() => f(default, default, default);
      """, """
      [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)] public struct A { public byte X; public int Y; }
      [StructLayout(LayoutKind.Explicit)] public struct B { [FieldOffset(0)] public int X; [FieldOffset(0)] public float Y; }
      public struct C { public long X; }
      """);
    var types = module.Types.OfType<IrStructType>().ToDictionary(type => type.Name, type => type.Layout);
    Assert.That(types["A"], Is.EqualTo(new IrStructLayout(IrLayoutKind.Sequential, 1, 16, [])));
    Assert.That(types["B"], Is.EqualTo(new IrStructLayout(IrLayoutKind.Explicit, 0, 0, [0, 0])));
    Assert.That(types["C"], Is.Null);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>Test design 11 and the scenario: what lies outside the marshalling table is refused, by name.</summary>
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(StringBuilder s);", "StringBuilder")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(Microsoft.Win32.SafeHandles.SafeFileHandle s);", "SafeFileHandle")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(Func<int> callback);", "delegate")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = \"M\")] int o);", "MarshalType")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(object o);", "the class object at the boundary of a native function")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([Out] string s);", "a string marshalled out")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(char c);", "a char")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(ref bool b);", "the ref parameter of the type bool")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([MarshalAs(UnmanagedType.BStr)] string s);", "BStr")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([MarshalAs(UnmanagedType.SafeArray)] int[] s);", "SafeArray")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([MarshalAs(UnmanagedType.I4)] bool b);", "I4")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(params int[] a);", "params")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([In] int a);", "the parameter a of the type int")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern string f();", "returns string")]
  [TestCase("[DllImport(\"libc.so.6\")] [return: MarshalAs(UnmanagedType.I4)] static extern int f();", "[return: MarshalAsAttribute]")]
  [TestCase("[DllImport(\"libc.so.6\", CharSet = CharSet.Auto)] static extern int f();", "CharSet.Auto")]
  [TestCase("[DllImport(\"libc.so.6\", PreserveSig = false)] static extern int f();", "DllImport with PreserveSig")]
  [TestCase("static extern int f();", "an extern method without [DllImport]")]
  [TestCase("[DllImport(\"libc.so.6\"), System.Security.SuppressUnmanagedCodeSecurity] static extern int f();", "SuppressUnmanagedCodeSecurityAttribute")]
  [TestCase("[LibraryImport(\"libc.so.6\")] private static partial int f(string s);", "without StringMarshalling")]
  [TestCase("[LibraryImport(\"libc.so.6\", StringMarshalling = StringMarshalling.Custom)] private static partial int f(string s);", "StringMarshalling.Custom")]
  [TestCase("[LibraryImport(\"libc.so.6\", StringMarshallingCustomType = typeof(int))] private static partial int f();", "StringMarshallingCustomType")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(int a); public static Func<int, int> G() => f;", "the native function f used as a value")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(out int a); public static int G() => f(out int a) + a;", "an out argument of a native call inside an expression")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(out IntPtr a); public static int G() { f(out IntPtr a); return 0; }", "nint")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(int a, int b); public static int G() => f(b: 1, a: 2);", "a named argument")]
  [TestCase("[LibraryImport(\"libc.so.6\")] private static partial int f<T>();", "a generic native function")]
  [TestCase("[System.Runtime.InteropServices.LibraryImportAttribute(\"\")] private static partial void f();", "a native function without the name of its library")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([MarshalAs(UnmanagedType.I4)] int a);", "the parameter a of the type int")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f(long[][] a);", "long[][]")]
  [TestCase("[DllImport(\"libc.so.6\"), Obsolete] static extern int f();", "the attribute [ObsoleteAttribute] on a native function")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([System.Diagnostics.CodeAnalysis.NotNull] int a);", "the attribute [NotNullAttribute] on a parameter")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern int f([In] ref int a);", "the ref parameter of the type int")]
  [TestCase("[DllImport(\"libc.so.6\")] static extern void f(nuint a); public static void G() => f(new UIntPtr(3u));", "'nuint' is not supported")]
  public void OutsideTheTable_IsRefusedByName(string members, string named) {
    var message = Refused(members);
    Assert.That(message, Does.Contain("PLY1002"));
    Assert.That(message, Does.Contain(named));
  }

  /// <summary>A struct layout outside the table is refused, by name.</summary>
  [TestCase("[StructLayout(LayoutKind.Auto)] public struct A { public int X; }", "LayoutKind.Auto")]
  [TestCase("[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] public struct A { public int X; }", "StructLayout with CharSet")]
  [TestCase("public struct A { public bool X; }", "the field X of the type bool")]
  [TestCase("public enum A { B }", "the enum A passed to a native function")]
  [TestCase("[StructLayout(LayoutKind.Explicit)] public struct A { public static int S; [FieldOffset(0)] public int X; }", "a static field of a struct")]
  public void LayoutOutsideTheTable_IsRefusedByName(string type, string named) {
    var message = Refused("[DllImport(\"lib\")] static extern int f(A a); public static int G() => f(default);", type);
    Assert.That(message, Does.Contain("PLY1002"));
    Assert.That(message, Does.Contain(named));
  }

}
