using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Tooling.Toolchains.ToolchainTesting;

namespace Polypiler.Tests.Tooling.Toolchains;

/// <summary>
/// The loader of <c>tooling/toolchains.lock</c>, and the repository lock itself: complete against
/// <c>backlog/README.md</c>, and in step with the project files and the float vectors' record of the
/// versions those really use.
/// </summary>
[TestFixture]
public sealed class ToolchainLockTests {

  /// <summary>The keys <c>backlog/README.md</c> lists under "Toolchains", read from its text and not from the loader.</summary>
  private static string[] ReadmeKeys() {
    var readme = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "backlog", "README.md"));
    var paragraph = Regex.Match(readme, @"Keys in use:(.*?)An issue that needs another", RegexOptions.Singleline).Groups[1].Value;
    return [.. Regex.Matches(paragraph, "`([a-z0-9-]+)`").Select(match => match.Groups[1].Value)];
  }

  [Test]
  public void RepositoryLock_IsValidAndHasEveryCoreKey() {
    var toolchains = ToolchainLock.Load(RepositoryLock);
    Assert.That(toolchains.MissingCoreKeys(), Is.Empty);
    Assert.That(toolchains.Entries.Select(entry => entry.Key), Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
  }

  [Test]
  public void CoversReadmeKeys() {
    var readme = ReadmeKeys();
    var toolchains = ToolchainLock.Load(RepositoryLock);
    Assert.That(readme, Has.Length.EqualTo(26));
    Assert.That(ToolchainLock.CoreKeys, Is.EquivalentTo(readme));
    Assert.That(readme.Where(key => !toolchains.TryGet(key, out _)), Is.Empty);
    Assert.That(readme, Is.SupersetOf(new[] { "dotnet-sdk", "node", "rust", "pb-compiler", "z3", "vice" }));
  }

  /// <summary>NuGet versions are restored from the project files; the lock records them and must say the same.</summary>
  [Test]
  public void NuGetPins_MatchTheProjectFiles() {
    var pins = Directory.EnumerateFiles(PrototypeFixture.RepositoryRoot, "*.*proj*", SearchOption.AllDirectories)
      .Concat(Directory.EnumerateFiles(PrototypeFixture.RepositoryRoot, "*.props", SearchOption.AllDirectories))
      .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
      .Where(path => path.EndsWith(".csproj", StringComparison.Ordinal) || path.EndsWith(".props", StringComparison.Ordinal))
      .Where(path => !path.Contains(Path.Combine("tests", "fixtures")))
      .SelectMany(path => XDocument.Load(path).Descendants().Where(element => element.Name.LocalName is "PackageReference" or "PackageDownload"))
      .SelectMany(element => ((string)element.Attribute("Version")!).Split(';').Select(version => ((string)element.Attribute("Include")!, version.Trim('[', ']'))))
      .ToHashSet();
    var nuget = ToolchainLock.Load(RepositoryLock).Entries.Where(entry => entry.Kind == ToolchainKind.NuGet).ToArray();

    Assert.That(nuget.Select(entry => (entry.Probe[0], entry.Version)).Where(pin => !pins.Contains(pin)), Is.Empty, "a NuGet entry of the lock that no project file restores");
    var downloads = pins.Where(pin => pin.Item1.Contains("ReferenceAssemblies", StringComparison.Ordinal) || pin.Item1.StartsWith("NETStandard.Library", StringComparison.Ordinal) || pin.Item1 == "Microsoft.NETCore.App.Ref");
    var toolchainPackages = new[] { "Acornima", "Microsoft.CodeAnalysis.CSharp" };
    Assert.That(downloads.Concat(pins.Where(pin => toolchainPackages.Contains(pin.Item1))).Where(pin => !nuget.Any(entry => entry.Probe[0] == pin.Item1 && entry.Version == pin.Item2)),
      Is.Empty, "a reference assembly, the parser or the compiler restored at a version the lock does not record");
  }

  /// <summary>The reference vectors record the gcc, MPFR and GMP they were computed with; the lock pins those.</summary>
  [Test]
  public void VectorToolchains_MatchTheVersionsTheVectorsRecord() {
    var recorded = File.ReadAllLines(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "vectors", "float", "generate", "versions.txt"))
      .Select(line => line.Split(": ", 2))
      .ToDictionary(parts => parts[0], parts => parts[1].Split(' ')[0]);
    var toolchains = ToolchainLock.Load(RepositoryLock);
    Assert.That(toolchains["gcc"].Version, Is.EqualTo(recorded["gcc"]));
    Assert.That(toolchains["mpfr"].Version, Is.EqualTo(recorded["mpfr"]));
    Assert.That(toolchains["gmp"].Version, Is.EqualTo(recorded["gmp"]));
  }

  [TestCase("no-version.lock", 3, 3, "missing property 'version' in 'node'")]
  [TestCase("extra-property.lock", 5, 5, "unknown property 'channel' in 'node'")]
  [TestCase("no-group.lock", 6, 21, "versionPattern of 'node' has no group 'version'")]
  [TestCase("unsorted.lock", 10, 3, "keys are not in ordinal order: 'gcc' after 'node'")]
  [TestCase("schema-2.lock", 1, 9, "unsupported schema 2")]
  [TestCase("duplicate.lock", 10, 3, "duplicate key 'node'")]
  [TestCase("invalid-key.lock", 3, 3, "invalid key 'Node'")]
  [TestCase("unknown-host.lock", 3, 3, "unknown toolchain key 'python'")]
  [TestCase("package-no-host.lock", 3, 3, "'mpmath' is a package but names no host toolchain")]
  public void MalformedFixture_IsRefusedWithItsReason(string fixture, int line, int column, string reason) {
    var exception = Assert.Throws<ToolchainLockException>(() => ToolchainLock.Load(Fixture(fixture)))!;
    Assert.That(exception.Diagnostics, Has.Count.EqualTo(1));
    var diagnostic = exception.Diagnostics[0];
    Assert.That((diagnostic.Code, diagnostic.Line, diagnostic.Column, diagnostic.Message), Is.EqualTo(("PLY0020", line, column, reason)));
    Assert.That(diagnostic.ToString(), Is.EqualTo($"{Fixture(fixture)}({line},{column}): error PLY0020: {reason}"));
  }

  [TestCase("empty.lock", 0, TestName = "A lock with zero toolchains is valid")]
  [TestCase("one.lock", 1, TestName = "A lock with exactly one toolchain is valid")]
  [TestCase("valid.lock", 2, TestName = "A lock with two toolchains is valid")]
  [TestCase("core-only.lock", 26, TestName = "A lock with only the core toolchains is valid")]
  [TestCase("core-plus-one.lock", 27, TestName = "A lock with one optional toolchain more is valid")]
  public void ValidFixture_Loads(string fixture, int count)
    => Assert.That(ToolchainLock.Load(Fixture(fixture)).Entries, Has.Count.EqualTo(count));

  [TestCase("core-only.lock", new string[0])]
  [TestCase("core-plus-one.lock", new string[0])]
  [TestCase("core-missing-z3.lock", new[] { "missing core toolchain 'z3'" })]
  public void MissingCoreKeys_AreReportedAtTheToolchainsProperty(string fixture, string[] reasons) {
    var missing = ToolchainLock.Load(Fixture(fixture)).MissingCoreKeys();
    Assert.That(missing.Select(diagnostic => diagnostic.Message), Is.EqualTo(reasons));
    Assert.That(missing.Select(diagnostic => (diagnostic.Code, diagnostic.Line, diagnostic.Column)), Is.All.EqualTo(("PLY0020", 2, 1)));
  }

  [Test]
  public void BinaryGarbage_IsNotAYamlMapping() {
    var exception = Assert.Throws<ToolchainLockException>(() => ToolchainLock.Load(Fixture("not-yaml.lock")))!;
    Assert.That(exception.Diagnostics.Single().Message, Does.StartWith("not a YAML mapping"));
  }

  [Test]
  public void MissingFile_IsAnEnvironmentError() {
    var path = Fixture("nowhere.lock");
    var exception = Assert.Throws<ToolchainLockException>(() => ToolchainLock.Load(path))!;
    var diagnostic = exception.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Line), Is.EqualTo(("PLY0004", 0)));
    Assert.That(diagnostic.ToString(), Does.StartWith($"{path}: error PLY0004: cannot read the toolchain lock: "));
    Assert.That(exception.Message, Is.EqualTo(diagnostic.ToString()));
  }

  [Test]
  public void Folder_IsAnEnvironmentError()
    => Assert.That(Assert.Throws<ToolchainLockException>(() => ToolchainLock.Load(FixtureFolder))!.Diagnostics.Single().Code, Is.EqualTo("PLY0004"));

  [TestCase("", 1, 1, "not a YAML mapping", TestName = "An empty file is not a mapping")]
  [TestCase("- a\n", 1, 1, "not a YAML mapping", TestName = "A list is not a mapping")]
  [TestCase("a: 1\n---\nb: 2\n", 2, 1, "not a YAML mapping: the lock holds more than one YAML document", TestName = "Two documents are refused")]
  [TestCase("a: [\n", 2, 1, null, TestName = "A syntax error is refused")]
  [TestCase("schema: 1\ntoolchains: {}\nextra: 1\n", 3, 1, "unknown property 'extra' in the lock", TestName = "An unknown top-level property is refused")]
  [TestCase("schema: 1\n", 1, 1, "missing property 'toolchains' in the lock", TestName = "The toolchains property is required")]
  [TestCase("toolchains: {}\n", 1, 1, "missing property 'schema' in the lock", TestName = "The schema property is required")]
  [TestCase("schema: 1\nschema: 1\ntoolchains: {}\n", 2, 1, "duplicate key 'schema'", TestName = "A top-level property given twice is refused")]
  [TestCase("schema: \"1\"\ntoolchains: {}\n", 1, 9, "unsupported schema 1", TestName = "A quoted schema is not the number 1")]
  [TestCase("schema: [1]\ntoolchains: {}\n", 1, 9, "unsupported schema ?", TestName = "A schema that is no scalar is refused")]
  [TestCase("schema: 1\ntoolchains: []\n", 2, 13, "property 'toolchains' must be a mapping", TestName = "The toolchains must be a mapping")]
  [TestCase("schema: 1\ntoolchains:\n  node: 1\n", 3, 9, "toolchain 'node' must be a mapping", TestName = "A toolchain must be a mapping")]
  [TestCase("schema: 1\ntoolchains:\n  ? [a]\n  : {}\n", 3, 5, "invalid key '?'", TestName = "A key that is no scalar is refused")]
  [TestCase("schema: 1\ntoolchains:\n  -node: {}\n", 3, 3, "invalid key '-node'", TestName = "A key starting with a hyphen is refused")]
  public void Text_IsRefusedWithItsReason(string text, int line, int column, string? reason) {
    var problems = Problems(text);
    Assert.That(problems, Has.Count.EqualTo(1));
    Assert.That((problems[0].Line, problems[0].Column), Is.EqualTo((line, column)));
    Assert.That(problems[0].Message, reason == null ? Does.StartWith("not a YAML mapping: ") : Is.EqualTo(reason));
  }

  [TestCase("version: [1]", "property 'version' of 'fake' must be a string", TestName = "A version that is a list is refused")]
  [TestCase("version: ~", "property 'version' of 'fake' must be a string", TestName = "A version that is a tilde null is refused")]
  [TestCase("version: null", "property 'version' of 'fake' must be a string", TestName = "A version that is null is refused")]
  [TestCase("version: \"\"", "property 'version' of 'fake' must be a string", TestName = "An empty version is refused")]
  [TestCase("probe: []", "property 'probe' of 'fake' must be a list of at least one string", TestName = "An empty probe is refused")]
  [TestCase("probe: fake", "property 'probe' of 'fake' must be a list of at least one string", TestName = "A probe that is no list is refused")]
  [TestCase("probe: [fake, [x]]", "property 'probe' of 'fake' must be a list of at least one string", TestName = "A probe with a nested list is refused")]
  [TestCase("platforms: [linux, solaris]", "unknown platform 'solaris' in 'fake'", TestName = "An unknown platform is refused")]
  [TestCase("platforms: [Linux]", "unknown platform 'Linux' in 'fake'", TestName = "Platforms are case-sensitive")]
  [TestCase("oracleOnly: yes", "property 'oracleOnly' of 'fake' must be true or false", TestName = "oracleOnly must be a truth value")]
  [TestCase("oracleOnly: \"true\"", "property 'oracleOnly' of 'fake' must be true or false", TestName = "A quoted truth value is a string")]
  [TestCase("kind: banana", "unknown kind 'banana' in 'fake'", TestName = "An unknown kind is refused")]
  [TestCase("home: [A]", "property 'home' of 'fake' must be a string", TestName = "home must be a string")]
  [TestCase("versionPattern: '(?<version>'", "versionPattern of 'fake' is not a regular expression: ", TestName = "A pattern that does not compile is refused")]
  [TestCase("licence: *anchor", "property 'licence' of 'fake' must be a string", TestName = "An alias is not a string")]
  public void Property_IsRefusedWithItsReason(string line, string reason) {
    var name = line[..line.IndexOf(':')];
    var text = Lock(Entry("fake"));
    var existing = new Regex($"(?m)^    {name}:.*\n");
    text = existing.IsMatch(text) ? existing.Replace(text, $"    {line}\n") : text + $"    {line}\n";
    var problems = Problems(text.Replace("version: \"1.2.3\"", "version: &anchor \"1.2.3\""));
    Assert.That(problems, Has.Count.EqualTo(1));
    Assert.That(problems[0].Message, Does.StartWith(reason));
  }

  [TestCase("version")]
  [TestCase("probe")]
  [TestCase("versionPattern")]
  [TestCase("platforms")]
  [TestCase("licence")]
  [TestCase("source")]
  public void RequiredProperty_IsMissing(string name)
    => Assert.That(Problems(Regex.Replace(Lock(Entry("fake")), $"(?m)^    {name}:.*\n", "")), Is.EqualTo(new[] { (3, 3, $"missing property '{name}' in 'fake'") }));

  [Test]
  public void PropertyGivenTwice_IsRefused()
    => Assert.That(Problems(Lock(Entry("fake")) + "    version: \"1\"\n").Single(), Is.EqualTo((10, 5, "duplicate key 'version'")));

  [Test]
  public void PackageOfAPackage_IsRefused() {
    var problems = Problems(Lock(
      Entry("a", extra: "kind: package\nrequires: host"),
      Entry("b", extra: "kind: nuget\nrequires: a"),
      Entry("host")));
    Assert.That(problems.Select(problem => problem.Message), Is.EqualTo(new[] { "'b' requires 'a', which is itself a package" }));
  }

  [Test]
  public void NuGetWithoutHost_IsRefused()
    => Assert.That(Problems(Lock(Entry("a", extra: "kind: nuget"))).Single().Message, Is.EqualTo("'a' is a package but names no host toolchain"));

  [Test]
  public void EveryProblem_IsReported() {
    var problems = Problems(Lock(Entry("b", extra: "kind: banana"), Entry("a", extra: "channel: x")));
    Assert.That(problems.Select(problem => problem.Message), Is.EqualTo(new[] {
      "unknown kind 'banana' in 'b'", "keys are not in ordinal order: 'a' after 'b'", "unknown property 'channel' in 'a'"
    }));
  }

  [Test]
  public void Entry_CarriesEveryProperty() {
    var toolchains = Parse(
      Entry("fake", "1.2.3", "[fake, --version, -v]", platforms: "[linux]", extra: "home: FAKE_HOME\noracleOnly: true\nkind: external\nrequires: host"),
      Entry("host", extra: "oracleOnly: false\nkind: tool"),
      Entry("plain"));
    var fake = toolchains["fake"];
    Assert.That(fake, Is.EqualTo(new ToolchainEntry("fake", "1.2.3", fake.Probe, @"^fake (?<version>\S+)$", fake.Platforms, "MIT", "https://example.org/fake") {
      Home = "FAKE_HOME", OracleOnly = true, Kind = ToolchainKind.External, Requires = "host", Line = 3, Column = 3
    }));
    Assert.That(fake.Probe, Is.EqualTo(new[] { "fake", "--version", "-v" }));
    Assert.That(fake.Platforms, Is.EqualTo(new[] { "linux" }));
    Assert.That((fake.Licence, fake.Source), Is.EqualTo(("MIT", "https://example.org/fake")));
    Assert.That((toolchains["host"].OracleOnly, toolchains["host"].Kind), Is.EqualTo((false, ToolchainKind.Tool)));
    Assert.That((toolchains["plain"].Home, toolchains["plain"].Requires, toolchains["plain"].Kind), Is.EqualTo(((string?)null, (string?)null, ToolchainKind.Tool)));
    Assert.That((toolchains.File, toolchains.Line, toolchains.Column), Is.EqualTo(("test.lock", 2, 1)));
  }

  [Test]
  public void UnknownKey_IsAnError() {
    var toolchains = Parse(Entry("fake"));
    Assert.That(toolchains.TryGet("nope", out _), Is.False);
    var exception = Assert.Throws<ToolchainLockException>(() => _ = toolchains["nope"])!;
    Assert.That(exception.Diagnostics.Single().ToString(), Is.EqualTo("test.lock: error PLY0020: unknown toolchain key 'nope'"));
  }

  [Test]
  public void Kinds_AreReadByName() {
    var toolchains = Parse(
      Entry("a", extra: "kind: package\nrequires: d"),
      Entry("b", extra: "kind: nuget\nrequires: d"),
      Entry("c", extra: "kind: external"),
      Entry("d", extra: "kind: tool"));
    Assert.That(toolchains.Entries.Select(entry => entry.Kind), Is.EqualTo(new[] { ToolchainKind.Package, ToolchainKind.NuGet, ToolchainKind.External, ToolchainKind.Tool }));
  }

}
