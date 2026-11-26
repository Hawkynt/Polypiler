using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NUnit.Framework;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Tooling.Report;
using static Polypiler.Tests.Backends.Msil.MsilTestHarness;

namespace Polypiler.Tests.Backends.Msil;

/// <summary>Tests for <see cref="IrWriteLine"/>, numeric formatting, and P/Invoke foreign interop in the MSIL backend.</summary>
[TestFixture]
public sealed class MsilStdioAndInteropTests {

  private static readonly object _consoleLock = new();

  private static string FindRepoRoot() {
    var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Polypiler.slnx")))
      dir = dir.Parent;
    Assert.That(dir, Is.Not.Null, "Could not locate repository root containing Polypiler.slnx.");
    return dir!.FullName;
  }

  private static (object? Result, string StdOut) InvokeCapturingConsole(IrModule module, MsilProfile profile, string methodName, params object?[] args) {
    lock (_consoleLock) {
      var previous = Console.Out;
      using var writer = new StringWriter();
      try {
        Console.SetOut(writer);
        var result = Invoke(module, profile, null, methodName, args);
        return (result, writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
      } finally {
        Console.SetOut(previous);
      }
    }
  }

  [TestCase("msil/net10")]
  [TestCase("msil/net8")]
  public void StdioShowcase_TranslatesAndExecutesLive(string profileName) {
    var path = Path.Combine(FindRepoRoot(), "docs", "showcase", "17-stdio.cs");
    var source = File.ReadAllText(path);
    var imported = CSharpFrontend.Import(path, source);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(imported.Module, Is.Not.Null);

    var profile = MsilProfile.Parse(profileName);
    var legalized = Legalize(imported.Module!, profile);
    Assert.That(MsilEmitter.Check(legalized, profile), Is.Empty);

    var (result, stdout) = InvokeCapturingConsole(legalized, profile, "Run");
    Assert.That(result, Is.EqualTo(14));
    Assert.That(stdout, Is.EqualTo("step 1: sum=1\nstep 2: sum=5\nstep 3: sum=14\n"));
  }

  [Test]
  public void WriteLine_HandlesNullEmptyAndRegularStrings() {
    var arg = L("s", IrType.String);
    var mod = new IrModule("WriteNullTest", [
      Function("Run", IrType.I32, [arg],
        new IrWriteLine(new IrStrConst("before")),
        new IrWriteLine(new IrNull(IrType.String)),
        new IrWriteLine(new IrLocalRef(arg)),
        new IrWriteLine(new IrStrConst("")),
        new IrWriteLine(new IrStrConst("after")),
        new IrReturn(I(7)))
    ]);

    var (result, stdout) = InvokeCapturingConsole(mod, MsilProfile.Net10, "Run", new object?[] { null });
    Assert.That(result, Is.EqualTo(7));
    Assert.That(stdout, Is.EqualTo("before\n\n\n\nafter\n"));
  }

  [Test]
  public void FormatDotNetAndFormatEcma_AllNumericTypes_MatchInterpreter() {
    var pI8 = L("a", IrType.I8);
    var pU8 = L("b", IrType.U8);
    var pI16 = L("c", IrType.I16);
    var pU16 = L("d", IrType.U16);
    var pI32 = L("e", IrType.I32);
    var pU32 = L("f", IrType.U32);
    var pI64 = L("g", IrType.I64);
    var pU64 = L("h", IrType.U64);
    var pF32 = L("j", IrType.F32);
    var pF64 = L("k", IrType.F64);

    static IrIntrinsic Fmt(IrIntrinsicOp op, IrExpr value) => new(op, IrType.String, [value]);

    var mod = new IrModule("FormatAll", [
      Function("FmtI8", IrType.String, [pI8], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pI8)))),
      Function("FmtU8", IrType.String, [pU8], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pU8)))),
      Function("FmtI16", IrType.String, [pI16], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pI16)))),
      Function("FmtU16", IrType.String, [pU16], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pU16)))),
      Function("FmtI32", IrType.String, [pI32], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pI32)))),
      Function("FmtU32", IrType.String, [pU32], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pU32)))),
      Function("FmtI64", IrType.String, [pI64], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pI64)))),
      Function("FmtU64", IrType.String, [pU64], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pU64)))),
      Function("FmtF32", IrType.String, [pF32], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pF32)))),
      Function("FmtF64", IrType.String, [pF64], new IrReturn(Fmt(IrIntrinsicOp.FormatDotNet, R(pF64)))),
      Function("EcmaI32", IrType.String, [pI32], new IrReturn(Fmt(IrIntrinsicOp.FormatEcma, R(pI32)))),
      Function("EcmaU64", IrType.String, [pU64], new IrReturn(Fmt(IrIntrinsicOp.FormatEcma, R(pU64))))
    ]);

    AssertMatchesInterpreter(mod, "FmtI8", (sbyte)-42);
    AssertMatchesInterpreter(mod, "FmtU8", (byte)255);
    AssertMatchesInterpreter(mod, "FmtI16", (short)-12345);
    AssertMatchesInterpreter(mod, "FmtU16", (ushort)65535);
    AssertMatchesInterpreter(mod, "FmtI32", int.MinValue);
    AssertMatchesInterpreter(mod, "FmtU32", uint.MaxValue);
    AssertMatchesInterpreter(mod, "FmtI64", long.MinValue);
    AssertMatchesInterpreter(mod, "FmtU64", ulong.MaxValue);
    AssertMatchesInterpreter(mod, "FmtF32", -3.5f);
    AssertMatchesInterpreter(mod, "FmtF64", 1234.5);
    AssertMatchesInterpreter(mod, "EcmaI32", -99);
    AssertMatchesInterpreter(mod, "EcmaU64", ulong.MaxValue);
  }

  [TestCase("msil/net10")]
  [TestCase("msil/net8")]
  public void NativeInteropShowcase_EmitsValidPInvokeMetadataAndDisassembles(string profileName) {
    var path = Path.Combine(FindRepoRoot(), "docs", "showcase", "19-native-interop.cs");
    var source = File.ReadAllText(path);
    var imported = CSharpFrontend.Import(path, source);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(imported.Module, Is.Not.Null);

    var profile = MsilProfile.Parse(profileName);
    var legalized = Legalize(imported.Module!, profile);
    Assert.That(MsilEmitter.Check(legalized, profile), Is.Empty);

    var files = MsilEmitter.Emit(legalized, profile);
    Assert.That(files, Has.Count.EqualTo(1));
    var bytes = files[0].BinaryContent!;

    using (var stream = new MemoryStream(bytes, writable: false))
    using (var pe = new PEReader(stream)) {
      var reader = pe.GetMetadataReader();

      var pointType = reader.TypeDefinitions
        .Select(reader.GetTypeDefinition)
        .Single(t => reader.GetString(t.Name) == "NativePoint");
      Assert.That(pointType.Attributes & TypeAttributes.LayoutMask, Is.EqualTo(TypeAttributes.SequentialLayout));

      var modType = reader.TypeDefinitions
        .Select(reader.GetTypeDefinition)
        .Single(t => reader.GetString(t.Name) == "NativeInteropShowcase");
      var pinvoke = modType.GetMethods()
        .Select(reader.GetMethodDefinition)
        .Single(m => reader.GetString(m.Name) == "geom_norm");

      Assert.That(pinvoke.Attributes & MethodAttributes.PinvokeImpl, Is.EqualTo(MethodAttributes.PinvokeImpl));
      Assert.That(pinvoke.Attributes & MethodAttributes.MemberAccessMask, Is.EqualTo(MethodAttributes.Private));
      Assert.That(pinvoke.ImplAttributes & MethodImplAttributes.PreserveSig, Is.EqualTo(MethodImplAttributes.PreserveSig));
      Assert.That(pinvoke.RelativeVirtualAddress, Is.EqualTo(0));

      var import = pinvoke.GetImport();
      Assert.That(reader.GetString(import.Name), Is.EqualTo("geom_norm"));
      Assert.That(reader.GetString(reader.GetModuleReference(import.Module).Name), Is.EqualTo("geom"));
      Assert.That(import.Attributes & MethodImportAttributes.CallingConventionMask, Is.EqualTo(MethodImportAttributes.CallingConventionWinApi));
    }

    // Loads cleanly in AssemblyLoadContext.
    WithLoadedAssembly(legalized, assembly => {
      var type = assembly.GetType("NativeInteropShowcase")!;
      Assert.That(type.GetMethod("Measure", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
      var ext = type.GetMethod("geom_norm", BindingFlags.NonPublic | BindingFlags.Static);
      Assert.That(ext, Is.Not.Null);
      Assert.That(ext!.Attributes & MethodAttributes.PinvokeImpl, Is.EqualTo(MethodAttributes.PinvokeImpl));
    }, profile);

    // MsilDisassembler handles RVA=0 P/Invoke methods without throwing.
    var il = MsilDisassembler.Disassemble(bytes);
    Assert.That(il, Does.Contain("geom_norm").And.Contain("Measure"));
  }

  [Test]
  public void ForeignFunctions_AllConventionsCharSetsMarshallingAndStructLayouts_EmitValidMetadata() {
    var seqStruct = new IrStructType("PackedSeq") { Layout = new IrStructLayout(IrLayoutKind.Sequential, Pack: 4, Size: 16, []) };
    seqStruct.Define([new IrField("A", IrType.I32, false), new IrField("B", IrType.I64, false)]);

    var expStruct = new IrStructType("UnionExp") { Layout = new IrStructLayout(IrLayoutKind.Explicit, Pack: 1, Size: 8, [0, 4]) };
    expStruct.Define([new IrField("Lo", IrType.I32, false), new IrField("Hi", IrType.I32, false)]);

    var fCdecl = new IrForeignFunction("fn_cdecl", true, "liba", IrType.Bool, [
      new IrForeignParameter("flag", IrType.Bool, new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.U1 }),
      new IrForeignParameter("utf8", IrType.String, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Utf8 }),
      new IrForeignParameter("wide", IrType.String, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Unicode }),
      new IrForeignParameter("buf", IrType.Array(IrType.I32), new IrMarshal(IrMarshalKind.Array, IrMarshalDirection.In)),
      new IrForeignParameter("outVal", IrType.Array(IrType.I32), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.Out))
    ]) {
      EntryPoint = "c_entry",
      Convention = IrCallingConvention.Cdecl,
      CharSet = IrStringEncoding.Ansi,
      SetLastError = true,
      ExactSpelling = true,
      Result = new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.I1 }
    };

    var fStd = new IrForeignFunction("fn_std", false, "liba", IrType.I64, [
      new IrForeignParameter("h", IrType.I64, new IrMarshal(IrMarshalKind.Handle)),
      new IrForeignParameter("n", IrType.U64, new IrMarshal(IrMarshalKind.Blittable) { NativeSize = true }),
      new IrForeignParameter("ansi", IrType.String, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Ansi }),
      new IrForeignParameter("io", IrType.Array(IrType.I32), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.InOut)),
      new IrForeignParameter("inRef", IrType.Array(IrType.Struct(seqStruct)), new IrMarshal(IrMarshalKind.ByRef, IrMarshalDirection.In))
    ]) {
      Convention = IrCallingConvention.StdCall,
      CharSet = IrStringEncoding.Unicode,
      Result = new IrMarshal(IrMarshalKind.Handle)
    };

    var fThis = new IrForeignFunction("fn_this", true, "libb", IrType.U64, [
      new IrForeignParameter("s", IrType.I64, new IrMarshal(IrMarshalKind.Blittable) { NativeSize = true }),
      new IrForeignParameter("b", IrType.Bool, new IrMarshal(IrMarshalKind.Bool) { BoolForm = IrBoolForm.I1 }),
      new IrForeignParameter("arrOut", IrType.Array(IrType.U8), new IrMarshal(IrMarshalKind.Array, IrMarshalDirection.Out)),
      new IrForeignParameter("arrIo", IrType.Array(IrType.U8), new IrMarshal(IrMarshalKind.Array, IrMarshalDirection.InOut))
    ]) {
      Convention = IrCallingConvention.ThisCall,
      Result = new IrMarshal(IrMarshalKind.Blittable) { NativeSize = true }
    };

    var fFast = new IrForeignFunction("fn_fast", false, "libb", IrType.Void, [
      new IrForeignParameter("u", IrType.Struct(expStruct), new IrMarshal(IrMarshalKind.Struct)),
      new IrForeignParameter("plainBool", IrType.Bool, new IrMarshal(IrMarshalKind.Bool)),
      new IrForeignParameter("plainStr", IrType.String, new IrMarshal(IrMarshalKind.String) { Encoding = IrStringEncoding.Ansi })
    ]) {
      Convention = IrCallingConvention.FastCall
    };

    var arrI32 = L("a32", IrType.Array(IrType.I32));
    var arrU8 = L("a8", IrType.Array(IrType.U8));
    var arrSeq = L("aseq", IrType.Array(IrType.Struct(seqStruct)));
    var expLocal = L("u", IrType.Struct(expStruct));

    var caller = Function("CallAll", IrType.U64, [arrI32, arrU8, arrSeq, expLocal],
      new IrExprStmt(new IrForeignCall("fn_cdecl", IrType.Bool, [B(true), new IrStrConst("u8"), new IrStrConst("w"), R(arrI32), R(arrI32)])),
      new IrExprStmt(new IrForeignCall("fn_std", IrType.I64, [I64(1), U64(2), new IrStrConst("a"), R(arrI32), R(arrSeq)])),
      new IrExprStmt(new IrForeignCall("fn_fast", IrType.Void, [R(expLocal), B(false), new IrStrConst("s")])),
      new IrReturn(new IrForeignCall("fn_this", IrType.U64, [I64(3), B(true), R(arrU8), R(arrU8)])));

    var module = new IrModule("InteropMatrix", [caller]) {
      Types = [seqStruct, expStruct],
      ForeignFunctions = [fCdecl, fStd, fThis, fFast]
    };

    Assert.That(MsilEmitter.Check(module), Is.Empty);
    var files = MsilEmitter.Emit(module);
    var bytes = files[0].BinaryContent!;

    using var stream = new MemoryStream(bytes, writable: false);
    using var pe = new PEReader(stream);
    var reader = pe.GetMetadataReader();

    // Verify struct layouts.
    var packed = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(t => reader.GetString(t.Name) == "PackedSeq");
    Assert.That(packed.Attributes & TypeAttributes.LayoutMask, Is.EqualTo(TypeAttributes.SequentialLayout));
    Assert.That(packed.GetLayout().PackingSize, Is.EqualTo(4));
    Assert.That(packed.GetLayout().Size, Is.EqualTo(16));

    var union = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(t => reader.GetString(t.Name) == "UnionExp");
    Assert.That(union.Attributes & TypeAttributes.LayoutMask, Is.EqualTo(TypeAttributes.ExplicitLayout));
    Assert.That(union.GetLayout().PackingSize, Is.EqualTo(1));
    Assert.That(union.GetLayout().Size, Is.EqualTo(8));
    var unionOffsets = union.GetFields().Select(h => reader.GetFieldDefinition(h).GetOffset()).ToArray();
    Assert.That(unionOffsets, Is.EqualTo(new[] { 0, 4 }));

    // Verify P/Invoke imports and deduplicated ModuleReferences.
    var modType = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(t => reader.GetString(t.Name) == "InteropMatrix");
    var methods = modType.GetMethods().Select(reader.GetMethodDefinition).ToDictionary(m => reader.GetString(m.Name));

    var cdeclImport = methods["fn_cdecl"].GetImport();
    Assert.That(reader.GetString(cdeclImport.Name), Is.EqualTo("c_entry"));
    Assert.That(cdeclImport.Attributes & MethodImportAttributes.CallingConventionMask, Is.EqualTo(MethodImportAttributes.CallingConventionCDecl));
    Assert.That(cdeclImport.Attributes & MethodImportAttributes.CharSetMask, Is.EqualTo(MethodImportAttributes.CharSetAnsi));
    Assert.That(cdeclImport.Attributes & MethodImportAttributes.SetLastError, Is.EqualTo(MethodImportAttributes.SetLastError));
    Assert.That(cdeclImport.Attributes & MethodImportAttributes.ExactSpelling, Is.EqualTo(MethodImportAttributes.ExactSpelling));

    var stdImport = methods["fn_std"].GetImport();
    Assert.That(stdImport.Module, Is.EqualTo(cdeclImport.Module)); // deduplicated "liba"
    Assert.That(stdImport.Attributes & MethodImportAttributes.CallingConventionMask, Is.EqualTo(MethodImportAttributes.CallingConventionStdCall));
    Assert.That(stdImport.Attributes & MethodImportAttributes.CharSetMask, Is.EqualTo(MethodImportAttributes.CharSetUnicode));

    var thisImport = methods["fn_this"].GetImport();
    Assert.That(thisImport.Attributes & MethodImportAttributes.CallingConventionMask, Is.EqualTo(MethodImportAttributes.CallingConventionThisCall));

    var fastImport = methods["fn_fast"].GetImport();
    Assert.That(fastImport.Module, Is.EqualTo(thisImport.Module)); // deduplicated "libb"
    Assert.That(fastImport.Attributes & MethodImportAttributes.CallingConventionMask, Is.EqualTo(MethodImportAttributes.CallingConventionFastCall));

    // Verify return marshalling descriptor on fn_cdecl (seq 0 -> I1 = 0x05) and param descriptors (U1 = 0x06, LPUTF8Str = 0x30, LPWStr = 0x15).
    var cdeclParams = methods["fn_cdecl"].GetParameters().Select(reader.GetParameter).ToList();
    Assert.That(cdeclParams[0].SequenceNumber, Is.EqualTo(0));
    Assert.That(reader.GetBlobBytes(cdeclParams[0].GetMarshallingDescriptor()), Is.EqualTo(new byte[] { 0x05 }));
    Assert.That(reader.GetBlobBytes(cdeclParams[1].GetMarshallingDescriptor()), Is.EqualTo(new byte[] { 0x06 }));
    Assert.That(reader.GetBlobBytes(cdeclParams[2].GetMarshallingDescriptor()), Is.EqualTo(new byte[] { 0x30 }));
    Assert.That(reader.GetBlobBytes(cdeclParams[3].GetMarshallingDescriptor()), Is.EqualTo(new byte[] { 0x15 }));

    // Verify assembly loads cleanly and disassembles cleanly.
    WithLoadedAssembly(module, asm => Assert.That(asm.GetType("InteropMatrix"), Is.Not.Null));
    Assert.That(MsilDisassembler.Disassemble(bytes), Does.Contain("fn_cdecl").And.Contain("fn_fast"));
  }

  [Test]
  public void LastError_Win32AndPInvoke_ExecuteLive() {
    var mod = new IrModule("LastErrMod", [
      Function("Win32", IrType.I32, [], new IrReturn(new IrLastError(PInvoke: false))),
      Function("PInvoke", IrType.I32, [], new IrReturn(new IrLastError(PInvoke: true)))
    ]);

    Assert.That(MsilEmitter.Check(mod), Is.Empty);
    Assert.That(Invoke(mod, "Win32"), Is.InstanceOf<int>());
    Assert.That(Invoke(mod, "PInvoke"), Is.InstanceOf<int>());
  }

}
