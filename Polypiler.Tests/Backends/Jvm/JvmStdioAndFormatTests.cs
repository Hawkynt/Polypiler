using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Jvm.JvmTestRunner;

namespace Polypiler.Tests.Backends.Jvm;

/// <summary>Tests for <see cref="IrWriteLine"/> and numeric formatting in the JVM backend.</summary>
[TestFixture]
public sealed class JvmStdioAndFormatTests {

  private static string FindRepoRoot() {
    var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Polypiler.slnx")))
      dir = dir.Parent;
    Assert.That(dir, Is.Not.Null, "Could not locate repository root containing Polypiler.slnx.");
    return dir!.FullName;
  }

  [TestCase("jvm/8")]
  [TestCase("jvm/17")]
  [TestCase("jvm/21")]
  public void StdioShowcase_TranslatesVerifiesAndExecutesLive(string profileName) {
    var path = Path.Combine(FindRepoRoot(), "docs", "showcase", "17-stdio.cs");
    var source = File.ReadAllText(path);
    var imported = CSharpFrontend.Import(path, source);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(imported.Module, Is.Not.Null);

    var profile = JvmProfile.Parse(profileName);
    var legalized = JvmEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, profile);
    Assert.That(JvmEmitter.Check(legalized, profile), Is.Empty);

    var files = JvmEmitter.Emit(legalized, profile);
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          int result = StdioShowcase.Run();
          System.out.println("RET=" + result);
        }
      }
      """);
    Assert.That(output, Is.EqualTo("step 1: sum=1\nstep 2: sum=5\nstep 3: sum=14\nRET=14"));
  }

  [Test]
  public void WriteLine_NullAndEmptyStrings_WriteEmptyLineNotNull() {
    var text = L("text", IrType.String);
    var module = new IrModule("WriteNullJvm", [
      Function("Run", IrType.Void, [text],
        new IrWriteLine(new IrStrConst("before")),
        new IrWriteLine(new IrNull(IrType.String)),
        new IrWriteLine(R(text)),
        new IrWriteLine(new IrStrConst("")),
        new IrWriteLine(new IrStrConst("after")),
        new IrReturn(null))
    ]);

    Assert.That(JvmEmitter.Check(module), Is.Empty);
    var files = JvmEmitter.Emit(module, JvmProfile.Java21, new JvmEmitOptions { PackageFormat = JvmPackageFormat.Class });
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          WriteNullJvm.Run(null);
        }
      }
      """);
    Assert.That(output, Is.EqualTo("before\n\n\n\nafter"));
  }

  [Test]
  public void FormatDotNetAndFormatEcma_AllNumericTypes_VerifyAndExecute() {
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

    var module = new IrModule("FormatJvm", [
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

    Assert.That(JvmEmitter.Check(module), Is.Empty);
    var files = JvmEmitter.Emit(module, JvmProfile.Java21, new JvmEmitOptions { PackageFormat = JvmPackageFormat.Class });
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(FormatJvm.FmtI8((byte)-42));
          System.out.println(FormatJvm.FmtU8((byte)255));
          System.out.println(FormatJvm.FmtI16((short)-12345));
          System.out.println(FormatJvm.FmtU16((char)65535));
          System.out.println(FormatJvm.FmtI32(Integer.MIN_VALUE));
          System.out.println(FormatJvm.FmtU32(-1));
          System.out.println(FormatJvm.FmtI64(Long.MIN_VALUE));
          System.out.println(FormatJvm.FmtU64(-1L));
          System.out.println(FormatJvm.FmtF32(-3.5f));
          System.out.println(FormatJvm.FmtF64(1234.5));
          System.out.println(FormatJvm.EcmaI32(-99));
          System.out.println(FormatJvm.EcmaU64(-1L));
        }
      }
      """);
    Assert.That(output, Is.EqualTo(
      "-42\n255\n-12345\n65535\n-2147483648\n4294967295\n-9223372036854775808\n18446744073709551615\n-3.5\n1234.5\n-99\n18446744073709551615"));

    // FormatEcma on f64 is still diagnosed as unsupported intrinsic (PLY3152).
    var dParam = L("d", IrType.F64);
    var badMod = new IrModule("BadEcmaF64", [
      Function("F", IrType.String, [dParam], new IrReturn(Fmt(IrIntrinsicOp.FormatEcma, R(dParam))))
    ]);
    Assert.That(JvmEmitter.Check(badMod).Select(d => d.Code), Does.Contain(JvmEmitter.UnsupportedIntrinsicCode));
  }

}
