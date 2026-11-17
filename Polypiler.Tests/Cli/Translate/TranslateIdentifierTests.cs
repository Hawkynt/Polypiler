using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Cli.Translate;

/// <summary>
/// The identifiers of <c>translate</c> (#180, test design 12, 13): an unknown language or profile is
/// <c>PLY0006</c>, a known language the command has no component for is <c>PLY0014</c>, and neither
/// writes anything; <c>--profile</c> is the former spelling of <c>--target-profile</c>.
/// </summary>
[TestFixture]
public sealed class TranslateIdentifierTests {

  private const string Sum32 = TranslateWorkspace.Fixtures + "/sum32.cs";

  private TranslateWorkspace _workspace = null!;

  [SetUp]
  public void CreateWorkspace() => this._workspace = new();

  [TearDown]
  public void DeleteWorkspace() => this._workspace.Dispose();

  private (int ExitCode, string Output, string Error) Translate(params string[] options)
    => this._workspace.Run(["translate", Sum32, .. options, "--output", "out"]);

  [TestCase("--from", "klingon", "--to", "javascript", "unknown language 'klingon'")]
  [TestCase("--from", "csharp", "--to", "klingon", "unknown language 'klingon'")]
  [TestCase("--from", "CSharp", "--to", "javascript", "unknown language 'CSharp'")]
  [TestCase("--from", "cs", "--to", "javascript", "unknown language 'cs'")]
  public void AnUnknownLanguage_IsPLY0006(string from, string source, string to, string target, string message) {
    var (exitCode, output, error) = this.Translate(from, source, to, target);
    Assert.That((exitCode, output, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.UsageError, "", $"polypiler: error PLY0006: {message}")));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [TestCase("--target-profile", "es1", "javascript")]
  [TestCase("--profile", "es1", "javascript")]
  [TestCase("--source-profile", "net1.0", "csharp")]
  [TestCase("--source-profile", "es2022", "csharp")]
  [TestCase("--target-profile", "py2.7", "python")]
  [TestCase("--target-profile", "rust2015", "rust")]
  public void AnUnknownProfile_IsPLY0006(string option, string profile, string language) {
    var (exitCode, _, error) = this.Translate("--from", "csharp", "--to", option == "--source-profile" ? "javascript" : language, option, profile);
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.UsageError, $"polypiler: error PLY0006: unknown profile '{profile}' for language '{language}'")));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [Test]
  public void AKnownLanguageWithoutABackend_IsPLY0014() {
    var (exitCode, _, error) = this.Translate("--from", "csharp", "--to", "ruby");
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.UnsupportedConstruct, "polypiler: error PLY0014: no backend for 'ruby' with profile 'default'")));
    Assert.That(Directory.Exists(this._workspace.Resolve("out")), Is.False);
  }

  [Test]
  public void AKnownLanguageWithoutAFrontend_IsPLY0014WithItsProfileAsTheRequiredCapability() {
    var (exitCode, _, error) = this._workspace.Run("--diagnostics", "json", "translate", Sum32, "--from", "go", "--source-profile", "go1.22", "--to", "javascript", "--output", "out");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
    Assert.That(error, Does.Contain("\"code\":\"PLY0014\"").And.Contain("no frontend for 'go' with profile 'go1.22'").And.Contain("\"requiredCapability\":\"go go1.22\""));
  }

  [Test]
  public void TheFormerProfileOption_NamesTheTargetProfile() {
    var (exitCode, _, error) = this.Translate("--from", "csharp", "--to", "javascript", "--profile", "es5");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(this._workspace.Manifest("out").Target.Profile, Does.StartWith("es5"));
  }

  [Test]
  public void BothSpellingsOfTheTargetProfile_AreExclusive() {
    var (exitCode, _, error) = this.Translate("--from", "csharp", "--to", "javascript", "--profile", "es5", "--target-profile", "es5");
    Assert.That((exitCode, error.Split('\n')[0]), Is.EqualTo(((int)ExitCode.UsageError, "polypiler: error PLY0011: options '--profile' and '--target-profile' cannot be combined")));
  }

  [Test]
  public void ASourceProfile_IsAcceptedForEverySourceLanguage() {
    var (exitCode, _, error) = this.Translate("--from", "csharp", "--source-profile", "net8.0", "--to", "javascript");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
  }

  [Test]
  public void TheLanguageTable_HoldsEveryFolderOfTheSpecification() {
    var folders = new[] { "languages", "targets" }
      .SelectMany(family => Directory.GetDirectories(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", family)))
      .Select(Path.GetFileName).Distinct();
    Assert.That(TranslateLanguages.All.Select(language => language.Id), Is.SupersetOf(folders));
  }

  [Test]
  public void TheProfilesOfTheSpecification_AreProfilesOfTheirLanguage() {
    foreach (var language in new[] { "csharp", "javascript" }) {
      var profiles = Directory.GetDirectories(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "targets", language)).Select(Path.GetFileName);
      Assert.That(TranslateLanguages.Find(language)!.Profiles, Is.SupersetOf(profiles), language);
    }
  }

  [TestCase("csharp", null, "net10.0")]
  [TestCase("javascript", null, "es2022")]
  [TestCase("javascript", "es5", "es5")]
  [TestCase("javascript", "es1", null)]
  [TestCase("rust", null, "rust2024")]
  [TestCase("ruby", null, null)]
  public void AProfile_ResolvesToItselfOrTheDefault(string id, string? typed, string? expected)
    => Assert.That(TranslateLanguages.Find(id)!.Resolve(typed), Is.EqualTo(expected));

  [Test]
  public void AnIdentifier_IsMatchedExactly()
    => Assert.That((TranslateLanguages.Find("python")?.Id, TranslateLanguages.Find("Python")), Is.EqualTo(("python", (TranslateLanguage?)null)));

}
