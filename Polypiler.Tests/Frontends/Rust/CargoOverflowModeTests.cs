using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.Frontends.Rust.Cargo;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The overflow mode of a Rust source without <c>--overflow-checks</c>: the profile rules of The Cargo
/// Book ("Profiles": the defaults of <c>dev</c>, <c>release</c>, <c>test</c> and <c>bench</c>,
/// <c>inherits</c>, the profiles of the workspace root only) over the manifest found above the
/// source, and the TOML reader that reads it (TOML 1.0.0). The acceptance tests hold the result to
/// what <c>cargo build</c> does with the same manifest.
/// </summary>
[TestFixture]
public sealed class CargoOverflowModeTests {

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-cargo-").FullName;

  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  private string Write(string relative, string text) {
    var path = Path.Combine(this._directory, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return path;
  }

  // The profiles.

  [TestCase("dev", OverflowChecks.Panic)]
  [TestCase("test", OverflowChecks.Panic)]
  [TestCase("release", OverflowChecks.Wrap)]
  [TestCase("bench", OverflowChecks.Wrap)]
  public void BuiltinProfile_HasCargosDefault(string profile, OverflowChecks expected) {
    Assert.That(CargoOverflowMode.FromManifest("[package]\nname = \"p\"\n", profile), Is.EqualTo(expected));
    Assert.That(CargoOverflowMode.Builtin(profile), Is.EqualTo(expected));
  }

  [Test]
  public void CustomProfile_IsNotBuiltIn() => Assert.That(CargoOverflowMode.Builtin("fast"), Is.Null);

  [TestCase("[profile.dev]\noverflow-checks = false", "dev", OverflowChecks.Wrap, TestName = "Own setting: dev off")]
  [TestCase("[profile.release]\noverflow-checks = true", "release", OverflowChecks.Panic, TestName = "Own setting: release on")]
  [TestCase("[profile.dev]\noverflow-checks = false", "test", OverflowChecks.Wrap, TestName = "Inherited: test from dev")]
  [TestCase("[profile.release]\noverflow-checks = true", "bench", OverflowChecks.Panic, TestName = "Inherited: bench from release")]
  [TestCase("[profile.test]\noverflow-checks = false\n[profile.dev]\noverflow-checks = true", "test", OverflowChecks.Wrap, TestName = "Own setting wins over the inherited one")]
  [TestCase("[profile.fast]\ninherits = \"release\"", "fast", OverflowChecks.Wrap, TestName = "Custom: inherits release")]
  [TestCase("[profile.fast]\ninherits = \"release\"\noverflow-checks = true", "fast", OverflowChecks.Panic, TestName = "Custom: own setting")]
  [TestCase("[profile.a]\ninherits = \"b\"\n[profile.b]\ninherits = \"dev\"\n[profile.dev]\noverflow-checks = false", "a", OverflowChecks.Wrap, TestName = "Custom: a chain of inherits")]
  [TestCase("profile.release.overflow-checks = true", "release", OverflowChecks.Panic, TestName = "Form: dotted keys at the root")]
  [TestCase("[profile]\nrelease = { overflow-checks = true, opt-level = 3 }", "release", OverflowChecks.Panic, TestName = "Form: an inline table")]
  [TestCase("[profile.\"release\"]\n'overflow-checks' = true", "release", OverflowChecks.Panic, TestName = "Form: quoted keys")]
  [TestCase("[profile.release]\nopt-level = \"s\"", "release", OverflowChecks.Wrap, TestName = "Form: a profile table without the key")]
  public void Manifest_DecidesTheMode(string manifest, string profile, OverflowChecks expected)
    => Assert.That(CargoOverflowMode.FromManifest(manifest, profile), Is.EqualTo(expected));

  [TestCase("[package]\nname = \"p\"", "fast", "Cargo profile 'fast' is not defined in Cargo.toml", TestName = "Refused: an undefined custom profile")]
  [TestCase("[profile.fast]\nopt-level = 1", "fast", "Cargo profile 'fast' of Cargo.toml has no 'inherits', which a custom profile needs", TestName = "Refused: no inherits")]
  [TestCase("[profile.a]\ninherits = \"b\"\n[profile.b]\ninherits = \"a\"", "a", "Cargo profile 'a' of Cargo.toml inherits in a circle: a -> b -> a", TestName = "Refused: a circle")]
  [TestCase("[profile.dev]\noverflow-checks = \"yes\"", "dev", "'overflow-checks' of [profile.dev] in Cargo.toml is no boolean: \"yes\"", TestName = "Refused: a string")]
  [TestCase("[profile.dev]\noverflow-checks = 1", "dev", "'overflow-checks' of [profile.dev] in Cargo.toml is no boolean: 1", TestName = "Refused: a number")]
  [TestCase("[profile.dev]\noverflow-checks = [true]", "dev", "'overflow-checks' of [profile.dev] in Cargo.toml is no boolean: a table or an array", TestName = "Refused: an array")]
  [TestCase("[profile.dev\n", "dev", "Cargo.toml is no TOML: line 1: a table header ends with ']'", TestName = "Refused: no TOML")]
  public void Unreadable_IsRefusedWithItsReason(string manifest, string profile, string message)
    => Assert.That(() => CargoOverflowMode.FromManifest(manifest, profile), Throws.TypeOf<CargoProfileException>().With.Message.EqualTo(message));

  // Finding the manifest.

  [Test]
  public void NoManifest_KeepsTheDefaultOfTheProfile() {
    var source = this.Write("src/lib.rs", "");
    // A temporary folder can lie below a Cargo.toml only on a machine that put one at the root of its temporary files.
    Assume.That(File.Exists(Path.Combine(Path.GetTempPath(), "Cargo.toml")), Is.False);
    Assert.That(CargoOverflowMode.ForSource(source), Is.EqualTo(new OverflowModeChoice(OverflowChecks.Panic, "the default of Cargo profile 'dev', no Cargo.toml above the source")));
    Assert.That(CargoOverflowMode.ForSource(source, "release").Mode, Is.EqualTo(OverflowChecks.Wrap));
    Assert.That(() => CargoOverflowMode.ForSource(source, "fast"), Throws.TypeOf<CargoProfileException>()
      .With.Message.EqualTo("Cargo profile 'fast' is not built in, and there is no Cargo.toml above the source to define it"));
  }

  [Test]
  public void ManifestBesideOrAbove_IsFound() {
    this.Write("Cargo.toml", "[package]\nname = \"p\"\n[profile.dev]\noverflow-checks = false\n");
    Assert.That(CargoOverflowMode.ForSource(this.Write("lib.rs", "")), Is.EqualTo(new OverflowModeChoice(OverflowChecks.Wrap, "Cargo profile 'dev' of Cargo.toml")));
    Assert.That(CargoOverflowMode.ForSource(this.Write("src/deep/lib.rs", "")), Is.EqualTo(new OverflowModeChoice(OverflowChecks.Wrap, "Cargo profile 'dev' of ../../Cargo.toml")));
  }

  [Test]
  public void WorkspaceRoot_OverridesTheProfilesOfAMember() {
    this.Write("Cargo.toml", "[workspace]\nmembers = [\"member\"]\n[profile.dev]\noverflow-checks = false\n");
    this.Write("member/Cargo.toml", "[package]\nname = \"m\"\n[profile.dev]\noverflow-checks = true\n");
    Assert.That(CargoOverflowMode.ForSource(this.Write("member/src/lib.rs", "")), Is.EqualTo(new OverflowModeChoice(OverflowChecks.Wrap, "Cargo profile 'dev' of ../../Cargo.toml")));
  }

  [Test]
  public void ManifestWithWorkspace_IsItsOwnRoot() {
    this.Write("Cargo.toml", "[workspace]\n[profile.dev]\noverflow-checks = true\n");
    this.Write("inner/Cargo.toml", "[package]\nname = \"i\"\n[workspace]\n[profile.dev]\noverflow-checks = false\n");
    Assert.That(CargoOverflowMode.ForSource(this.Write("inner/lib.rs", "")).Mode, Is.EqualTo(OverflowChecks.Wrap));
  }

  [Test]
  public void PackageWorkspace_NamesTheRoot() {
    this.Write("root/Cargo.toml", "[workspace]\n[profile.dev]\noverflow-checks = false\n");
    this.Write("Cargo.toml", "[workspace]\n[profile.dev]\noverflow-checks = true\n");
    this.Write("crates/m/Cargo.toml", "[package]\nname = \"m\"\nworkspace = \"../../root\"\n");
    Assert.That(CargoOverflowMode.ForSource(this.Write("crates/m/lib.rs", "")), Is.EqualTo(new OverflowModeChoice(OverflowChecks.Wrap, "Cargo profile 'dev' of ../../root/Cargo.toml")));
  }

  [Test]
  public void PackageWorkspaceWithoutManifest_IsRefused() {
    this.Write("m/Cargo.toml", "[package]\nname = \"m\"\nworkspace = \"../none\"\n");
    Assert.That(() => CargoOverflowMode.ForSource(this.Write("m/lib.rs", "")), Throws.TypeOf<CargoProfileException>().With.Message.Contains("the workspace '../none' of").And.Message.EndsWith("has no Cargo.toml"));
  }

  [Test]
  public void BrokenManifest_IsRefusedWithItsPath() {
    var manifest = this.Write("Cargo.toml", "[package]\nname = 'p\n");
    Assert.That(() => CargoOverflowMode.ForSource(this.Write("lib.rs", "")), Throws.TypeOf<CargoProfileException>()
      .With.Message.EqualTo($"{manifest} is no TOML: line 2: a string is not closed on its line"));
  }

  [Test]
  public void Arguments_AreRequired() {
    Assert.That(() => CargoOverflowMode.ForSource(null!), Throws.ArgumentNullException);
    Assert.That(() => CargoOverflowMode.ForSource("a.rs", null!), Throws.ArgumentNullException);
    Assert.That(() => CargoOverflowMode.FromManifest(null!, "dev"), Throws.ArgumentNullException);
    Assert.That(() => CargoOverflowMode.FromManifest("", null!), Throws.ArgumentNullException);
    Assert.That(() => TomlReader.Read(null!), Throws.ArgumentNullException);
  }

  // The TOML reader.

  private static object? At(Dictionary<string, object> table, params string[] keys) {
    object? current = table;
    foreach (var key in keys)
      current = ((Dictionary<string, object>)current!)[key];
    return current;
  }

  [Test]
  public void Toml_ReadsEveryFormOfACargoManifest() {
    var document = TomlReader.Read(""""
      # A comment, then a table.
      [package]   # after a header
      name = "a \"b\" \\ \t\u00e9\U0001F600\b\f\n\r"
      literal = 'C:\path'
      empty = ""
      authors = [ "x", 'y', ]   # a trailing comma
      version.major = 1
      "quoted key" = true
      nested = [[1, 2], [], { a = false }]
      multi = [
        1, # a comment inside
        2
      ]
      inline = {}
      when = 1979-05-27 07:32:00Z
      description = '''
      first
      'second''''
      text = """
      one \
         two ""quoted""\t"""

      [[bin]]
      name = "one"
      [[bin]]
      name = "two"
      [bin.extra]
      flag = false

      [dependencies.serde]
      version = "1"
      [dependencies]
      rand = "0.8"
      """".Replace("\r\n", "\n", StringComparison.Ordinal));
    Assert.That(At(document, "package", "name"), Is.EqualTo("a \"b\" \\ \t\u00e9\U0001F600\b\f\n\r"));
    Assert.That(At(document, "package", "literal"), Is.EqualTo("C:\\path"));
    Assert.That(At(document, "package", "empty"), Is.EqualTo(""));
    Assert.That(At(document, "package", "authors"), Is.EqualTo(new object[] { "x", "y" }));
    Assert.That(At(document, "package", "version", "major"), Is.EqualTo(new TomlRaw("1")));
    Assert.That(At(document, "package", "quoted key"), Is.EqualTo(true));
    var nested = (List<object>)At(document, "package", "nested")!;
    Assert.That(nested[0], Is.EqualTo(new object[] { new TomlRaw("1"), new TomlRaw("2") }));
    Assert.That(nested[1], Is.Empty);
    Assert.That(((Dictionary<string, object>)nested[2])["a"], Is.EqualTo(false));
    Assert.That(At(document, "package", "multi"), Is.EqualTo(new object[] { new TomlRaw("1"), new TomlRaw("2") }));
    Assert.That(At(document, "package", "inline"), Is.Empty);
    Assert.That(At(document, "package", "when"), Is.EqualTo(new TomlRaw("1979-05-27 07:32:00Z")));
    Assert.That(At(document, "package", "description"), Is.EqualTo("first\n'second'"));
    Assert.That(At(document, "package", "text"), Is.EqualTo("one two \"\"quoted\"\"\t"));
    var bins = (List<object>)document["bin"];
    Assert.That(bins, Has.Count.EqualTo(2));
    Assert.That(((Dictionary<string, object>)bins[1])["name"], Is.EqualTo("two"));
    Assert.That(At((Dictionary<string, object>)bins[1], "extra", "flag"), Is.EqualTo(false));
    Assert.That(At(document, "dependencies", "serde", "version"), Is.EqualTo("1"));
    Assert.That(At(document, "dependencies", "rand"), Is.EqualTo("0.8"));
  }

  [Test]
  public void Toml_ReadsWindowsLineEndingsAndAnEmptyDocument() {
    Assert.That(TomlReader.Read("a = '''\r\nx'''\r\nb = \"\"\"\r\ny \\\r\n z\"\"\"\r\n# end\r\n"), Is.EqualTo(new Dictionary<string, object> { ["a"] = "x", ["b"] = "y z" }));
    Assert.That(TomlReader.Read(""), Is.Empty);
    Assert.That(TomlReader.Read("a = 1"), Is.EqualTo(new Dictionary<string, object> { ["a"] = new TomlRaw("1") }));
  }

  [TestCase("a = 1\na = 2", 2, "key 'a' is defined twice")]
  [TestCase("[t]\n[t]", 2, "table 't' is defined twice")]
  [TestCase("t = { a = 1 }\n[t]", 2, "table 't' is defined twice")]
  [TestCase("t = 1\n[t.u]", 2, "'t' is no table")]
  [TestCase("t = { a = 1 }\n[t.u]", 2, "'t' is no table")]
  [TestCase("t = 1\n[[t]]", 2, "'t' is no array of tables")]
  [TestCase("t = [ {} ]\n[[t]]", 2, "'t' is no array of tables")]
  [TestCase("t = 1\nt.u = 2", 2, "'t' is no table")]
  [TestCase("[t", 1, "a table header ends with ']'")]
  [TestCase("[[t]", 1, "an array-of-tables header ends with ']]'")]
  [TestCase("a 1", 1, "a key is followed by '='")]
  [TestCase("= 1", 1, "unexpected '=' where a key belongs")]
  [TestCase("a.", 1, "a key is missing at the end")]
  [TestCase("a =", 1, "a value is missing")]
  [TestCase("a = 1 b", 1, "unexpected 'b' after a value")]
  [TestCase("a = [1 2]", 1, "the values of an array are separated by ','")]
  [TestCase("a = { b = 1\n}", 1, "the pairs of an inline table are separated by ',' on one line")]
  [TestCase("a = \"x", 1, "a string is not closed on its line")]
  [TestCase("a = \"x\n\"", 1, "a string is not closed on its line")]
  [TestCase("a = 'x\n'", 1, "a string is not closed on its line")]
  [TestCase("a = '''x", 1, "a multi-line string is not closed")]
  [TestCase("a = \"\"\"x", 1, "a multi-line string is not closed")]
  [TestCase("a = \"\\q\"", 1, "unknown escape '\\q'")]
  [TestCase("a = \"\\u12\"", 1, "an escape takes 4 hexadecimal digits of a Unicode scalar value")]
  [TestCase("a = \"\\uD800\"", 1, "an escape takes 4 hexadecimal digits of a Unicode scalar value")]
  [TestCase("a = \"\\U00110000\"", 1, "an escape takes 8 hexadecimal digits of a Unicode scalar value")]
  [TestCase("a = \"\\uzzzz\"", 1, "an escape takes 4 hexadecimal digits of a Unicode scalar value")]
  [TestCase("a = \"\\u1", 1, "an escape takes 4 hexadecimal digits of a Unicode scalar value")]
  public void Toml_RefusesWhatIsNotWellFormed(string text, int line, string message)
    => Assert.That(() => TomlReader.Read(text), Throws.TypeOf<TomlException>().With.Message.EqualTo($"line {line}: {message}").And.Property(nameof(TomlException.Line)).EqualTo(line));

}
