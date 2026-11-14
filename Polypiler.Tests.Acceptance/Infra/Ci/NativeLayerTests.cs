using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Ci;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The parts of the native layer that need no compiler: the sanitizer table and the flags built from
/// it, and the layer file the audit reads. <c>tests/features/infra/infra-ci-native.feature</c> compiles
/// and runs the fixtures.
/// </summary>
[TestFixture]
[Layer("native")]
public sealed class NativeLayerTests {

  /// <summary>Test design 2 and 3: each platform and compiler of the layer, with and without sanitizers.</summary>
  [TestCase("linux", "gcc", "address,undefined")]
  [TestCase("linux", "clang", "address,undefined")]
  [TestCase("macos", "clang", "address,undefined")]
  [TestCase("windows", "clang", null)]
  public void TheTable_SaysWhichCombinationHasWhichSanitizers(string platform, string compiler, string? groups) {
    Assert.That(NativeFlags.SanitizersOf(compiler, platform), Is.EqualTo(groups));
    var flags = NativeFlags.Sanitized(NativeLanguage.C, compiler, platform);
    var expected = new[] { "-std=c17", "-Wall", "-Wextra", "-Werror", "-O1", "-g", "-fno-omit-frame-pointer" };
    Assert.That(flags, Is.EqualTo(groups is null ? expected : [.. expected, $"-fsanitize={groups}", "-fno-sanitize-recover=all"]));
  }

  [Test]
  public void TheTable_HasExactlyTheFourCombinationsOfTheLayer()
    => Assert.That(NativeFlags.Sanitizers.Keys.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "linux/clang", "linux/gcc", "macos/clang", "windows/clang" }));

  /// <summary>A combination the table does not name never builds without sanitizers silently.</summary>
  [Test]
  public void ACombinationOutsideTheTable_IsAnError()
    => Assert.That(() => NativeFlags.SanitizersOf("gcc", "macos"), Throws.InvalidOperationException.With.Message.Contains("no row 'macos/gcc'"));

  [Test]
  public void CPlusPlus_IsBuiltAsCPlusPlus20()
    => Assert.That(NativeFlags.Sanitized(NativeLanguage.Cpp, "clang", "linux")[0], Is.EqualTo("-std=c++20"));

  [Test]
  public void TheCompileStep_DrivesCPlusPlusAsCPlusPlus() {
    var clang = NativeFlags.Compile("clang", NativeLanguage.Cpp, "a.cpp", "program", "-DEMPTY");
    var gcc = NativeFlags.Compile("gcc", NativeLanguage.Cpp, "a.cpp", "program");
    var c = NativeFlags.Compile("gcc", NativeLanguage.C, "a.c", "program");
    Assert.That((clang.Toolchain, clang.Arguments[0], clang.Arguments.TakeLast(4)), Is.EqualTo(("clang", "--driver-mode=g++", new[] { "-DEMPTY", "a.cpp", "-o", "program" })));
    Assert.That((gcc.Toolchain, gcc.Arguments[0], gcc.Arguments[^1]), Is.EqualTo(("gcc", "-std=c++20", "-lstdc++")));
    Assert.That(c.Arguments.TakeLast(3), Is.EqualTo(new[] { "a.c", "-o", "program" }));
  }

  [Test]
  public void AProgram_HasTheExtensionOfThePlatform()
    => Assert.That(NativeFlags.Program("p"), Is.EqualTo(OperatingSystem.IsWindows() ? "p.exe" : "p"));

  [TestCase("schema: 2\nsanitizers: {}\n")]
  [TestCase("schema: 1\n")]
  [TestCase("schema: 1\nsanitizers: [a]\n")]
  public void AMalformedTable_IsRefused(string text)
    => Assert.That(() => NativeFlags.ReadTable(text), Throws.TypeOf<InvalidDataException>().With.Message.Contains("schema: 1"));

  [TestCase("schema: 1\nsanitizers:\n  linux/gcc: [address]\n")]
  [TestCase("schema: 1\nsanitizers:\n  linux/gcc: ''\n")]
  public void ARowThatIsNoText_IsRefused(string text)
    => Assert.That(() => NativeFlags.ReadTable(text), Throws.TypeOf<InvalidDataException>().With.Message.Contains("'linux/gcc' is not a list of sanitizers"));

  /// <summary>The layer file is valid, and the sanitizer table beside it is no layer file of its own.</summary>
  [Test]
  public void TheLayerFile_IsValidAndTheTableIsNotALayer() {
    var (layers, problems) = CiLayer.LoadAll(PrototypeFixture.RepositoryRoot);
    Assert.That(problems, Is.Empty);
    var native = layers.Single(layer => layer.Name == "native");
    Assert.That((native.Toolchains, native.Os, native.Always), Is.EqualTo((new[] { "dotnet-sdk", "gcc", "clang", "bubblewrap" }, new[] { "ubuntu-latest", "macos-latest", "windows-latest" }, false)));
    Assert.That(native.Selects("tests/fixtures/infra/ci/native/adler32.c"), Is.True);
    Assert.That(native.Selects("Polypiler.Core.Backends.C/Emitter.cs"), Is.True);
    Assert.That(native.Selects("README.md"), Is.False);
  }

}
