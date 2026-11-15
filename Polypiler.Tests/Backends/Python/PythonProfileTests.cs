using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Python;

namespace Polypiler.Tests.Backends.Python;

/// <summary>The profiles py3.8 to py3.14, the capability table that says what each has, and the naming rules of PEP 8.</summary>
[TestFixture]
public sealed class PythonProfileTests {

  [Test]
  public void Profiles_AreEveryMinorVersionFrom8To14_TheNewestByDefault() {
    Assert.That(PythonProfile.Names, Is.EqualTo(new[] { "py3.8", "py3.9", "py3.10", "py3.11", "py3.12", "py3.13", "py3.14" }));
    Assert.That(PythonProfile.Default.Name, Is.EqualTo("py3.14"));
    Assert.That(PythonProfile.Default.Version, Is.EqualTo("3.14"));
    Assert.That(PythonProfile.Default.ToString(), Is.EqualTo("py3.14"));
  }

  [TestCase("py3.8", 8)]
  [TestCase("3.10", 10)]
  [TestCase(null, 14)]
  public void Parse_ReadsANameOrAVersion(string? name, int minor) => Assert.That(PythonProfile.Parse(name).Minor, Is.EqualTo(minor));

  [TestCase("py3.7")]
  [TestCase("py3.15")]
  [TestCase("py2.7")]
  [TestCase("python3")]
  [TestCase("")]
  public void Parse_RefusesWhatIsNoProfile(string name) {
    Assert.That(PythonProfile.TryParse(name, out var profile, out var error), Is.False);
    Assert.That((profile, error), Is.EqualTo(((PythonProfile?)null, $"unknown Python profile '{name}'; the profiles are py3.8, py3.9, py3.10, py3.11, py3.12, py3.13, py3.14")));
    Assert.That(() => PythonProfile.Parse(name), Throws.ArgumentException);
  }

  [TestCase(PythonCapabilities.Match, "py3.9", false)]
  [TestCase(PythonCapabilities.Match, "py3.10", true)]
  [TestCase(PythonCapabilities.BitCount, "py3.9", false)]
  [TestCase(PythonCapabilities.BitCount, "py3.10", true)]
  [TestCase(PythonCapabilities.BuiltinGenerics, "py3.8", false)]
  [TestCase(PythonCapabilities.BuiltinGenerics, "py3.9", true)]
  [TestCase(PythonCapabilities.AssignmentExpression, "py3.8", true)]
  [TestCase(PythonCapabilities.Struct, "py3.8", true)]
  public void Capability_IsThereFromItsVersionOn(string capability, string profile, bool has)
    => Assert.That(PythonCapabilities.Standard.Has(capability, PythonProfile.Parse(profile)), Is.EqualTo(has));

  [Test]
  public void EveryCapability_ThatAProfileLacks_HasAReplacement() {
    foreach (var profile in PythonProfile.All)
      Assert.That(PythonCapabilities.Standard.Missing(profile).Select(name => PythonCapabilities.Standard[name].Replacement), Has.None.Null, profile.Name);

    Assert.That(PythonCapabilities.Standard.Missing(PythonProfile.Parse("py3.8")), Is.EqualTo(new[] { "list[int]", "match", "int.bit_count" }));
    Assert.That(PythonCapabilities.Standard.Missing(PythonProfile.Default), Is.Empty);
  }

  [Test]
  public void Table_RefusesAnUnknownNameAndADuplicate() {
    Assert.That(() => PythonCapabilities.Standard["walrus"], Throws.ArgumentException);
    Assert.That(() => new PythonCapabilities([new("a", 8), new("a", 9)]), Throws.ArgumentException);
    Assert.That(() => PythonCapabilities.Standard.Has(PythonCapabilities.Match, null!), Throws.ArgumentNullException);
  }

  [TestCase("computeChecksum", "compute_checksum")]
  [TestCase("ComputeChecksum", "compute_checksum")]
  [TestCase("compute_checksum", "compute_checksum")]
  [TestCase("XMLParser", "xml_parser")]
  [TestCase("Adler32", "adler32")]
  [TestCase("ToInt32", "to_int32")]
  [TestCase("Fnv1a64", "fnv1a64")]
  [TestCase("IsPow2", "is_pow2")]
  [TestCase("a$b", "a_b")]
  [TestCase("café", "caf_")]
  [TestCase("", "")]
  public void Snake_WritesSnakeCase(string name, string snake) => Assert.That(PythonNaming.Snake(name), Is.EqualTo(snake));

  [TestCase("class", "class_")]
  [TestCase("match", "match_")]
  [TestCase("type", "type_")]
  [TestCase("len", "len_")]
  [TestCase("1st", "n_1st")]
  [TestCase("value", "value")]
  public void Usable_AvoidsKeywordsBuiltInsAndLeadingDigits(string name, string usable) => Assert.That(PythonNaming.Usable(name), Is.EqualTo(usable));

  [Test]
  public void Names_OfEachKindOfDeclaration() {
    Assert.That(PythonNaming.Function("ComputeChecksum", true), Is.EqualTo("compute_checksum"));
    Assert.That(PythonNaming.Function("Helper", false), Is.EqualTo("_helper"));
    Assert.That(PythonNaming.Function("Len", true), Is.EqualTo("len_"));
    Assert.That(PythonNaming.Function("$", true), Is.Empty);
    Assert.That(PythonNaming.Constant("crcTable"), Is.EqualTo("_CRC_TABLE"));
    Assert.That(PythonNaming.Constant("$"), Is.EqualTo("_CONSTANT"));
    Assert.That(PythonNaming.Global("callCount"), Is.EqualTo("_call_count"));
    Assert.That(PythonNaming.Global("_"), Is.EqualTo("_state"));
    Assert.That(PythonNaming.Local("sum"), Is.EqualTo("sum_"));
    Assert.That(PythonNaming.Local(""), Is.EqualTo("value"));
    Assert.That(PythonNaming.Module("Hawkynt.Checksums.Adler32"), Is.EqualTo("adler32"));
    Assert.That(PythonNaming.Module("_"), Is.EqualTo("module"));
  }

  [Test]
  public void Free_TakesTheFirstFreeNumber() {
    Assert.That(PythonNaming.Free("x", _ => false), Is.EqualTo("x"));
    Assert.That(PythonNaming.Free("x", name => name is "x" or "x_2"), Is.EqualTo("x_3"));
    Assert.That(() => PythonNaming.Free("x", null!), Throws.ArgumentNullException);
  }

  [TestCase("a1", true)]
  [TestCase("_a", true)]
  [TestCase("1a", false)]
  [TestCase("a-b", false)]
  [TestCase("def", false)]
  [TestCase("", false)]
  public void IsIdentifier_AcceptsTheAsciiSubset(string name, bool identifier) => Assert.That(PythonNaming.IsIdentifier(name), Is.EqualTo(identifier));

}
