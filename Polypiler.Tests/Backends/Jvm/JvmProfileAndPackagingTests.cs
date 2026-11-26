using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Jvm.JvmTestRunner;

namespace Polypiler.Tests.Backends.Jvm;

[TestFixture]
public sealed class JvmProfileAndPackagingTests {

  [Test]
  public void Profiles_HaveExpectedMetadataAndParsing() {
    Assert.That(JvmProfile.Default, Is.EqualTo(JvmProfile.Java21));
    Assert.That((JvmProfile.Java21.MajorVersion, JvmProfile.Java17.MajorVersion, JvmProfile.Java8.MajorVersion), Is.EqualTo(((ushort)65, (ushort)61, (ushort)52)));
    Assert.That(JvmProfile.Names, Is.EqualTo(new[] { "jvm/21", "jvm/17", "jvm/8" }));
    Assert.That(JvmProfile.PackageFormatNames, Is.EqualTo(new[] { "jar", "class" }));
    Assert.That(JvmProfile.Java17.ToString(), Is.EqualTo("jvm/17"));
    Assert.That(JvmEmitter.Runtime, Is.EqualTo("jdk"));

    Assert.That(JvmProfile.Parse(), Is.EqualTo(JvmProfile.Java21));
    Assert.That(JvmProfile.Parse("jvm/21", "jar"), Is.EqualTo(JvmProfile.Java21));
    Assert.That(JvmProfile.Parse("jvm/17", "class"), Is.EqualTo(JvmProfile.Java17 with { PackageFormat = JvmPackageFormat.Class }));
    Assert.That(JvmProfile.Parse("jvm/8", null), Is.EqualTo(JvmProfile.Java8));

    Assert.That(JvmProfile.TryParse("jvm/99", null, out var badProfile, out var err1), Is.False);
    Assert.That(badProfile, Is.Null);
    Assert.That(err1, Is.EqualTo("unknown JVM profile 'jvm/99'; the profiles are jvm/21, jvm/17 and jvm/8"));

    Assert.That(JvmProfile.TryParse("jvm/21", "zip", out var badFmt, out var err2), Is.False);
    Assert.That(badFmt, Is.Null);
    Assert.That(err2, Is.EqualTo("unknown JVM package format 'zip'; the formats are jar and class"));

    Assert.That(() => JvmProfile.Parse("jvm/bad"), Throws.ArgumentException);
    Assert.That(() => JvmProfile.Parse("jvm/21", "bad"), Throws.ArgumentException);
  }

  [TestCase("jvm/8", 52)]
  [TestCase("jvm/17", 61)]
  [TestCase("jvm/21", 65)]
  public void Profiles_EmitValidClassfileMajorVersionAndExecute(string profileName, int expectedMajor) {
    var x = L("x", IrType.I32);
    var module = new IrModule("VerCheck", [
      Function("Double", IrType.I32, [x], new IrReturn(Bin(IrBinaryOp.Multiply, R(x), I(2))))
    ]);

    var profile = JvmProfile.Parse(profileName, "class");
    var files = JvmEmitter.Emit(module, profile);
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Path, Is.EqualTo("VerCheck.class"));
    var bytes = files[0].BinaryContent!;
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)), Is.EqualTo(0xCAFEBABEu));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2)), Is.EqualTo((ushort)0));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6, 2)), Is.EqualTo((ushort)expectedMajor));

    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          System.out.println(VerCheck.Double(21));
        }
      }
      """);
    Assert.That(output, Is.EqualTo("42"));
  }

  [Test]
  public void JarPackaging_IsDeterministicAndContainsSortedEntriesWithFixedTimestamp() {
    var a = L("a", IrType.I32);
    var module = new IrModule("123-My!Mod", [
      Function("Inc", IrType.I32, [a], new IrReturn(Bin(IrBinaryOp.Add, R(a), I(1))))
    ]);
    var opts = new JvmEmitOptions { PackageName = "com.example.demo", MainClass = "com.example.demo._123MyMod" };

    var first = JvmEmitter.Emit(module, JvmProfile.Java21, opts);
    var second = JvmEmitter.Emit(module, JvmProfile.Java21, opts);
    Assert.That(first[0].Path, Is.EqualTo("_123MyMod.jar"));
    Assert.That(first[0].BinaryContent, Is.EqualTo(second[0].BinaryContent));

    using var ms = new MemoryStream(first[0].BinaryContent!);
    using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
    Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "META-INF/MANIFEST.MF", "com/example/demo/_123MyMod.class" }));
    foreach (var entry in zip.Entries)
      Assert.That(entry.LastWriteTime.DateTime, Is.EqualTo(new DateTime(1980, 1, 1, 0, 0, 0)));

    using var reader = new StreamReader(zip.Entries[0].Open(), Encoding.UTF8);
    var manifest = reader.ReadToEnd();
    Assert.That(manifest, Does.Contain("Manifest-Version: 1.0\r\n"));
    Assert.That(manifest, Does.Contain("Main-Class: com.example.demo._123MyMod\r\n"));

    var output = RunHarness(first, """
      public class Harness {
        public static void main(String[] args) throws Exception {
          new com.example.demo._123MyMod();
          System.out.println(com.example.demo._123MyMod.Inc(41));
        }
      }
      """);
    Assert.That(output, Is.EqualTo("42"));
  }

  [Test]
  public void MainEntryPoints_ExecuteDirectlyViaJavaDashJar() {
    // 1. Exported Main() -> i32 returning 0 and non-zero exit code
    var modZero = new IrModule("ExitZero", [
      Function("Main", IrType.I32, [], new IrReturn(I(0)))
    ]);
    var resZero = RunJar(JvmEmitter.Emit(modZero).Single());
    Assert.That(resZero.ExitCode, Is.EqualTo(0));

    var mod42 = new IrModule("Exit42", [
      Function("Main", IrType.I32, [], new IrReturn(I(42)))
    ]);
    var res42 = RunJar(JvmEmitter.Emit(mod42).Single());
    Assert.That(res42.ExitCode, Is.EqualTo(42));

    // 2. Exported Main(str[]) -> i32 checking argument count
    var argsLocal = L("args", IrType.Array(IrType.String));
    var modArgs = new IrModule("ArgCount", [
      Function("Main", IrType.I32, [argsLocal], new IrReturn(new IrArrayLength(R(argsLocal))))
    ]);
    var resArgs = RunJar(JvmEmitter.Emit(modArgs).Single(), "one", "two", "three");
    Assert.That(resArgs.ExitCode, Is.EqualTo(3));

    // 3. Exported main(str[]) -> void (direct JVM signature, no duplicate bridge)
    var modDirect = new IrModule("DirectMain", [
      Function("main", IrType.Void, [argsLocal], new IrReturn(null))
    ]);
    var resDirect = RunJar(JvmEmitter.Emit(modDirect).Single());
    Assert.That(resDirect.ExitCode, Is.EqualTo(0));

    // 4. Exported Main() -> void with sanitized empty-symbol name
    var modEmptyName = new IrModule("!!!", [
      Function("Main", IrType.Void, [], new IrReturn(null))
    ]);
    var filesEmpty = JvmEmitter.Emit(modEmptyName);
    Assert.That(filesEmpty[0].Path, Is.EqualTo("_.jar"));
    Assert.That(RunJar(filesEmpty[0]).ExitCode, Is.EqualTo(0));
  }

  [Test]
  public void ClassfileEdgeCases_LdcWide_WideLocals_ModifiedUtf8_AndZeroPcLoop() {
    // Test >255 constant pool entries (triggers ldc_w), >255 local slots (triggers wide iload/istore),
    // modified UTF-8 (NUL, 2-byte, 3-byte, surrogate pair), and 0-local loop whose target is at PC 0.
    var stmts = new List<IrStmt>();
    var sum = L("sum", IrType.I32);
    stmts.Add(new IrDeclare(sum, I(0)));
    for (var i = 0; i < 260; ++i) {
      var loc = L($"v{i}", IrType.I32);
      stmts.Add(new IrDeclare(loc, I(100000 + i)));
      stmts.Add(new IrAssign(sum, Bin(IrBinaryOp.Add, R(sum), R(loc))));
    }

    stmts.Add(new IrReturn(R(sum)));

    var n = L("n", IrType.I32);
    var zeroLocalLoop = Function("CountDown", IrType.I32, [n],
      new IrWhile(Bin(IrBinaryOp.Greater, R(n), I(0)), Block(
        new IrAssign(n, Bin(IrBinaryOp.Subtract, R(n), I(1)))
      )),
      new IrReturn(R(n)));

    var utf8Fn = Function("Utf8Text", IrType.String, [],
      new IrReturn(new IrStrConst("A\u0000\u00E9\u4E2D\U0001F600")));

    var module = new IrModule("EdgeCases", [
      Function("BigPoolAndLocals", IrType.I32, [], [.. stmts]),
      zeroLocalLoop,
      utf8Fn
    ]);

    var files = JvmEmitter.Emit(module, JvmProfile.Java21, new JvmEmitOptions { PackageFormat = JvmPackageFormat.Class });
    var output = RunHarness(files, """
      public class Harness {
        public static void main(String[] args) {
          int expected = 0;
          for (int i = 0; i < 260; i++) expected += 100000 + i;
          System.out.println(EdgeCases.BigPoolAndLocals() == expected);
          System.out.println(EdgeCases.CountDown(5));
          String s = EdgeCases.Utf8Text();
          System.out.println(s.length() + ":" + (int)s.charAt(0) + ":" + (int)s.charAt(1) + ":" + (int)s.charAt(2) + ":" + (int)s.charAt(3) + ":" + s.codePointAt(4));
        }
      }
      """);
    Assert.That(output, Is.EqualTo("true\n0\n6:65:0:233:20013:128512"));
  }

}
