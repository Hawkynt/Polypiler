using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Kotlin;

namespace Polypiler.Tests.Backends.Kotlin;

/// <summary>The Kotlin profiles, their names and compiler options, the capability table and the names of the emitted code.</summary>
[TestFixture]
public sealed class KotlinProfileTests {

  [TestCase(null, "kotlin2.4", "1.8")]
  [TestCase("kotlin/jvm", "kotlin2.4", "1.8")]
  [TestCase("kotlin2.0", "kotlin2.0", "1.8")]
  [TestCase("2.3", "kotlin2.3", "1.8")]
  [TestCase("kotlin2.2-jvm17", "kotlin2.2", "17")]
  [TestCase("kotlin2.1-jvm8", "kotlin2.1", "1.8")]
  [TestCase("kotlin2.4-jvm1.8", "kotlin2.4", "1.8")]
  [TestCase("kotlin2.4-jvm21", "kotlin2.4", "21")]
  public void TryParse_ReadsTheVersionAndTheJvmTarget(string? name, string expected, string jvm) {
    Assert.That(KotlinProfile.TryParse(name, out var profile, out var error), Is.True);
    Assert.Multiple(() => {
      Assert.That(error, Is.Null);
      Assert.That(profile!.Name, Is.EqualTo(expected));
      Assert.That(profile.JvmTarget, Is.EqualTo(jvm));
    });
  }

  [TestCase("kotlin1.9")]
  [TestCase("kotlin2.5")]
  [TestCase("kotlin2.4-jvm9")]
  [TestCase("java")]
  [TestCase("")]
  public void TryParse_RefusesAnythingElse(string name) {
    Assert.That(KotlinProfile.TryParse(name, out var profile, out var error), Is.False);
    Assert.Multiple(() => {
      Assert.That(profile, Is.Null);
      Assert.That(error, Does.StartWith($"unknown Kotlin profile '{name}'").And.Contain("kotlin2.0, kotlin2.1"));
      Assert.Throws<ArgumentException>(() => KotlinProfile.Parse(name));
    });
  }

  [Test]
  public void Profiles_CoverEveryVersionOnEveryTarget() {
    Assert.Multiple(() => {
      Assert.That(KotlinProfile.Names, Is.EqualTo(new[] { "kotlin2.0", "kotlin2.1", "kotlin2.2", "kotlin2.3", "kotlin2.4" }));
      Assert.That(KotlinProfile.Everything, Has.Count.EqualTo(20));
      Assert.That(KotlinProfile.Default.ToString(), Is.EqualTo("kotlin2.4"));
      Assert.That(KotlinProfile.Parse("kotlin2.2-jvm17").ToString(), Is.EqualTo("kotlin2.2-jvm17"));
      Assert.That(KotlinProfile.Parse("kotlin2.2-jvm17").Jvm, Is.EqualTo(17));
      Assert.That(KotlinProfile.Default.Jvm, Is.EqualTo(8));
      Assert.That(KotlinProfile.Parse("kotlin2.0").CompilerOptions, Is.EqualTo(new[] { "-language-version", "2.0", "-api-version", "2.0", "-jvm-target", "1.8", "-Xsuppress-version-warnings" }));
      Assert.Throws<ArgumentException>(() => KotlinProfile.Default.WithJvmTarget("9"));
    });
  }

  [Test]
  public void Capabilities_FollowTheJvmTarget() {
    var table = KotlinCapabilities.Standard;
    Assert.Multiple(() => {
      Assert.That(table.Has(KotlinCapabilities.Fma, KotlinProfile.Default), Is.False);
      Assert.That(table.Has(KotlinCapabilities.Fma, KotlinProfile.Parse("kotlin2.4-jvm11")), Is.True);
      Assert.That(table.Has(KotlinCapabilities.UnsignedMultiplyHigh, KotlinProfile.Parse("kotlin2.4-jvm17")), Is.False);
      Assert.That(table.Has(KotlinCapabilities.CompressExpand, KotlinProfile.Parse("kotlin2.0-jvm21")), Is.True);
      Assert.That(table.Missing(KotlinProfile.Default), Is.EqualTo(new[] { KotlinCapabilities.Fma, KotlinCapabilities.MultiplyHigh, KotlinCapabilities.UnsignedMultiplyHigh, KotlinCapabilities.CompressExpand }));
      Assert.That(table.Missing(KotlinProfile.Parse("kotlin2.4-jvm21")), Is.Empty);
      Assert.That(table[KotlinCapabilities.Fma].Replacement, Does.Contain("portable"));
      Assert.Throws<ArgumentException>(() => _ = table["nothing"]);
      Assert.Throws<ArgumentException>(() => new KotlinCapabilities([new("a", 0, 8, "x"), new("a", 1, 9, "y")]));
      Assert.That(new KotlinCapabilities([new("late", 3, 8, "none")]).Has("late", KotlinProfile.Parse("kotlin2.2")), Is.False);
    });
  }

  [TestCase("Adler32", "adler32")]
  [TestCase("HTTPServer", "httpServer")]
  [TestCase("ABC", "abc")]
  [TestCase("max_value", "maxValue")]
  [TestCase("_private", "private")]
  [TestCase("fun", "fun_")]
  [TestCase("println", "println_")]
  [TestCase("value", "value_")]
  [TestCase("9lives", "_9lives")]
  [TestCase("a-b", "aB")]
  [TestCase("___", "")]
  [TestCase("lower", "lower")]
  public void Camel_FollowsTheConventions(string name, string expected) => Assert.That(KotlinNaming.Camel(name), Is.EqualTo(expected));

  [TestCase("point", "Point")]
  [TestCase("my_type", "MyType")]
  [TestCase("9x", "T9x")]
  [TestCase("Int", "Int_")]
  [TestCase("__", "")]
  public void Type_IsUpperCamelCase(string name, string expected) => Assert.That(KotlinNaming.Type(name), Is.EqualTo(expected));

  [TestCase("MaxValue", "MAX_VALUE")]
  [TestCase("_table", "TABLE")]
  [TestCase("crc32Table", "CRC32_TABLE")]
  [TestCase("__", "")]
  public void Constant_IsScreamingSnakeCase(string name, string expected) => Assert.That(KotlinNaming.Constant(name), Is.EqualTo(expected));

  [TestCase("Adler32", "adler32")]
  [TestCase("My Module", "mymodule")]
  [TestCase("9", "generated9")]
  [TestCase("", "generated")]
  [TestCase("Object", "object_")]
  public void Package_IsLowerCase(string module, string expected) => Assert.That(KotlinNaming.Package(module), Is.EqualTo(expected));

  [Test]
  public void Free_TakesTheFirstNameNotTaken() {
    Assert.Multiple(() => {
      Assert.That(KotlinNaming.Free("a", _ => false), Is.EqualTo("a"));
      Assert.That(KotlinNaming.Free("a", name => name is "a" or "a2"), Is.EqualTo("a3"));
      Assert.Throws<ArgumentNullException>(() => KotlinNaming.Free(null!, _ => false));
    });
  }

  [Test]
  public void Names_RejectNull() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => KotlinNaming.Camel(null!));
      Assert.Throws<ArgumentNullException>(() => KotlinNaming.Type(null!));
      Assert.Throws<ArgumentNullException>(() => KotlinNaming.Constant(null!));
      Assert.Throws<ArgumentNullException>(() => KotlinNaming.Package(null!));
      Assert.Throws<ArgumentNullException>(() => KotlinNaming.Free("a", null!));
      Assert.Throws<ArgumentNullException>(() => KotlinCapabilities.Standard.Has(KotlinCapabilities.Fma, null!));
      Assert.Throws<ArgumentNullException>(() => new KotlinCapabilities(null!));
    });
  }

  [Test]
  public void Keywords_AndReservedNames_AreDisjointFromOrdinaryNames() {
    Assert.That(KotlinNaming.Keywords.Intersect(KotlinNaming.Reserved), Is.Empty);
    Assert.That(KotlinNaming.Keywords, Has.Member("when").And.Member("typealias"));
  }

}
