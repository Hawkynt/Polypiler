using System;
using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Perl.PerlIr;

namespace Polypiler.Tests.Backends.Perl;

/// <summary>Tests for <see cref="PerlProfile"/>, <see cref="PerlEmitOptions"/>, <see cref="PerlNaming"/> and name-collision diagnostics.</summary>
[TestFixture]
public sealed class PerlProfileAndNamingTests {

  [Test]
  public void Profiles_HaveExpectedDefaultsAndParsing() {
    Assert.That(PerlProfile.Names, Is.EqualTo(new[] { "perl/5.6", "perl/5.8", "perl/5" }));
    Assert.That(PerlProfile.ByteStorageNames, Is.EqualTo(new[] { "array", "string" }));
    Assert.That(PerlProfile.ObjectModelNames, Is.EqualTo(new[] { "blessed", "moose", "inside-out" }));
    Assert.That(PerlProfile.Default.Name, Is.EqualTo("perl/5"));
    Assert.That(PerlProfile.Default.Dialect, Is.EqualTo(PerlDialect.Perl5));
    Assert.That(PerlProfile.Default.ByteStorage, Is.EqualTo(PerlByteStorage.Array));
    Assert.That(PerlProfile.Default.ObjectModel, Is.EqualTo(PerlObjectModel.Blessed));
    Assert.That(PerlProfile.Default.ToString(), Is.EqualTo("perl/5"));
    Assert.That(PerlProfile.Parse(null), Is.EqualTo(PerlProfile.Default));
    Assert.That(PerlProfile.Parse("perl/5"), Is.EqualTo(PerlProfile.Default));
    Assert.That(PerlProfile.Parse("perl/5.6"), Is.EqualTo(PerlProfile.Perl56));
    Assert.That(PerlProfile.Parse("perl/5.8"), Is.EqualTo(PerlProfile.Perl58));

    Assert.That(PerlProfile.Perl56.HasDefinedOr, Is.False);
    Assert.That(PerlProfile.Perl56.HasConstantHashRef, Is.False);
    Assert.That(PerlProfile.Perl56.HasUtf8Downgrade, Is.False);
    Assert.That(PerlProfile.Perl56.HasScalarUtil, Is.False);
    Assert.That(PerlProfile.Perl56.SupportsMoose, Is.False);
    Assert.That(PerlProfile.Perl56.VersionPragma, Is.EqualTo("use 5.006;"));

    Assert.That(PerlProfile.Perl58.HasDefinedOr, Is.False);
    Assert.That(PerlProfile.Perl58.HasConstantHashRef, Is.True);
    Assert.That(PerlProfile.Perl58.HasUtf8Downgrade, Is.True);
    Assert.That(PerlProfile.Perl58.HasScalarUtil, Is.True);
    Assert.That(PerlProfile.Perl58.SupportsMoose, Is.True);
    Assert.That(PerlProfile.Perl58.HasFieldHash, Is.False);
    Assert.That(PerlProfile.Perl58.VersionPragma, Is.EqualTo("use 5.008;"));

    Assert.That(PerlProfile.TryParse("perl/5", "string", "inside-out", out var ioProf, out var err), Is.True);
    Assert.That(err, Is.Null);
    Assert.That(ioProf!.ByteStorage, Is.EqualTo(PerlByteStorage.String));
    Assert.That(ioProf.ObjectModel, Is.EqualTo(PerlObjectModel.InsideOut));
    Assert.That(ioProf.ToString(), Is.EqualTo("perl/5;byteStorage=string;objects=inside-out"));

    Assert.That(PerlProfile.TryParse("perl/5.8", "array", "moose", out var mooseProf, out err), Is.True);
    Assert.That(err, Is.Null);
    Assert.That(mooseProf!.ObjectModel, Is.EqualTo(PerlObjectModel.Moose));
    Assert.That(mooseProf.ToString(), Is.EqualTo("perl/5.8;objects=moose"));

    Assert.That(PerlProfile.TryParse(null, "array", out var arrProf, out _), Is.True);
    Assert.That(arrProf!.ByteStorage, Is.EqualTo(PerlByteStorage.Array));
  }

  [TestCase("perl/6", null, null, "unknown Perl profile 'perl/6'; the profiles are perl/5.6, perl/5.8 and perl/5")]
  [TestCase("perl5", null, null, "unknown Perl profile 'perl5'; the profiles are perl/5.6, perl/5.8 and perl/5")]
  [TestCase("perl/5", "vec", null, "unknown Perl byte storage 'vec'; the modes are array and string")]
  [TestCase("perl/5", null, "corinna", "unknown Perl object model 'corinna'; the modes are blessed, moose and inside-out")]
  [TestCase("perl/5.6", null, "moose", "the Moose object model requires Perl 5.8 or later; use profile 'perl/5.8' or 'perl/5', or choose 'blessed' or 'inside-out'")]
  public void Profiles_RejectUnknownNamesStorageAndIncompatibleObjectModels(string name, string? storage, string? objectModel, string expected) {
    Assert.That(PerlProfile.TryParse(name, storage, objectModel, out var profile, out var error), Is.False);
    Assert.That((profile, error), Is.EqualTo(((PerlProfile?)null, expected)));
    if (storage is null && objectModel is null)
      Assert.That(() => PerlProfile.Parse(name), Throws.ArgumentException);
  }

  [TestCase("computeChecksum", "compute_checksum")]
  [TestCase("ComputeChecksum", "compute_checksum")]
  [TestCase("XMLParser", "xml_parser")]
  [TestCase("Adler32", "adler32")]
  [TestCase("a-b", "a_b")]
  [TestCase("", "")]
  public void Snake_ConvertsIdentifiers(string input, string expected)
    => Assert.That(PerlNaming.Snake(input), Is.EqualTo(expected));

  [TestCase("sub", "sub_")]
  [TestCase("my", "my_")]
  [TestCase("a", "a_")]
  [TestCase("b", "b_")]
  [TestCase("_", "__")]
  [TestCase("1st", "n_1st")]
  [TestCase("value", "value")]
  public void Usable_EscapesReservedAndLeadingDigits(string input, string expected)
    => Assert.That(PerlNaming.Usable(input), Is.EqualTo(expected));

  [Test]
  public void Naming_CoversFunctionsLocalsConstantsPackagesPathsHashKeysStringsAndFree() {
    Assert.That(PerlNaming.Function("ComputeChecksum", true), Is.EqualTo("compute_checksum"));
    Assert.That(PerlNaming.Function("Helper", false), Is.EqualTo("_helper"));
    Assert.That(PerlNaming.Function("Map<i32,i64>", true), Is.EqualTo("map_of_i32_and_i64"));
    Assert.That(PerlNaming.Function("$$$", true), Is.Empty);

    Assert.That(PerlNaming.Local("a"), Is.EqualTo("a_"));
    Assert.That(PerlNaming.Local("b"), Is.EqualTo("b_"));
    Assert.That(PerlNaming.Local("_"), Is.EqualTo("v"));
    Assert.That(PerlNaming.Local("sum"), Is.EqualTo("sum"));

    Assert.That(PerlNaming.Constant("crcTable"), Is.EqualTo("CRC_TABLE"));
    Assert.That(PerlNaming.Constant("$$"), Is.EqualTo("CONST"));
    Assert.That(PerlNaming.Constant("1st"), Is.EqualTo("N_1ST"));
    Assert.That(PerlNaming.Constant("BEGIN"), Is.EqualTo("BEGIN_"));

    Assert.That(PerlNaming.Package("Hawkynt.Checksums.Adler32"), Is.EqualTo("Hawkynt::Checksums::Adler32"));
    Assert.That(PerlNaming.Package("foo_bar::baz-1"), Is.EqualTo("FooBar::Baz1"));
    Assert.That(PerlNaming.Package("123test"), Is.EqualTo("M123test"));
    Assert.That(PerlNaming.Package("..."), Is.EqualTo("Module"));

    Assert.That(PerlNaming.PackagePath("Hawkynt::Checksums::Adler32"), Is.EqualTo("lib/Hawkynt/Checksums/Adler32.pm"));
    Assert.That(PerlNaming.TestPath("Hawkynt::Checksums::Adler32"), Is.EqualTo("t/adler32.t"));
    Assert.That(PerlNaming.TestPath("Simple"), Is.EqualTo("t/simple.t"));
    Assert.That(PerlNaming.TestPath("___"), Is.EqualTo("t/module.t"));

    Assert.That(PerlNaming.HashKey("Width"), Is.EqualTo("width"));
    Assert.That(PerlNaming.HashKey("sub"), Is.EqualTo("'sub'"));
    Assert.That(PerlNaming.HashKey("a'b\\c"), Is.EqualTo("a_b_c"));
    Assert.That(PerlNaming.HashKey("$$"), Is.EqualTo("'$$'"));

    Assert.That(PerlNaming.StringLiteral("hi \"a\" $b @c \\ \n\r\t\0é"),
      Is.EqualTo("\"hi \\\"a\\\" \\$b \\@c \\\\ \\n\\r\\t\\x{00}\\x{e9}\""));

    Assert.That(PerlNaming.Free("x", _ => false), Is.EqualTo("x"));
    Assert.That(PerlNaming.Free("x", n => n is "x" or "x_2"), Is.EqualTo("x_3"));
    Assert.That(() => PerlNaming.Free("x", null!), Throws.ArgumentNullException);
  }

  [Test]
  public void FileName_AndFunctionNames_WorkAndDetectCollisions() {
    var mod = new IrModule("Hawkynt.Checksums.Adler32", [
      Function("Compute", IrType.I32, [L("x", IrType.I32)], new IrReturn(R(L("x", IrType.I32)))),
      Function("Helper", false, IrType.I32, [L("x", IrType.I32)], new IrReturn(R(L("x", IrType.I32))))
    ]);
    Assert.That(PerlEmitter.FileName(mod), Is.EqualTo("lib/Hawkynt/Checksums/Adler32.pm"));
    Assert.That(PerlEmitter.FileName(mod, new PerlEmitOptions { PackageName = "Custom::Pkg" }), Is.EqualTo("lib/Custom/Pkg.pm"));
    var names = PerlEmitter.FunctionNames(mod);
    Assert.That(names["Compute"], Is.EqualTo("compute"));
    Assert.That(names["Helper"], Is.EqualTo("_helper"));

    var colliding = new IrModule("M", [
      Function("FooBar", IrType.I32, [], new IrReturn(I(1))),
      Function("foo_bar", IrType.I32, [], new IrReturn(I(2)))
    ]);
    var problems = Problems(colliding);
    Assert.That(problems, Has.Some.Contains("PLY1774"));
    Assert.That(() => Emit(colliding), Throws.ArgumentException);

    var emptyName = new IrModule("M", [
      Function("$$$", IrType.I32, [], new IrReturn(I(1)))
    ]);
    Assert.That(Problems(emptyName), Has.Some.Contains("PLY1774"));

    var privateCollision = new IrModule("M", [
      Function("FooBar", false, IrType.I32, [], new IrReturn(I(1))),
      Function("foo_bar", false, IrType.I32, [], new IrReturn(I(2)))
    ]);
    var privNames = PerlEmitter.FunctionNames(privateCollision);
    Assert.That(privNames["FooBar"], Is.EqualTo("_foo_bar"));
    Assert.That(privNames["foo_bar"], Is.EqualTo("_foo_bar_2"));
  }

}
