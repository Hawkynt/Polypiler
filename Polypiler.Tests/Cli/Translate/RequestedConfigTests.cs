using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;

namespace Polypiler.Tests.Cli.Translate;

/// <summary>
/// The canonical command line of <c>translate</c> (#180, ADR 0004 "Recorded command line"; test design
/// 3, 19 to 24): one string for every spelling of one request, defaults written out, the output and
/// the options that only change how the run reports left out.
/// </summary>
[TestFixture]
public sealed class RequestedConfigTests {

  private const string Sum32 = TranslateWorkspace.Fixtures + "/sum32.cs";

  private TranslateWorkspace _workspace = null!;

  [SetUp]
  public void CreateWorkspace() => this._workspace = new();

  [TearDown]
  public void DeleteWorkspace() => this._workspace.Dispose();

  private string Recorded(string output, params string[] arguments) {
    var (exitCode, _, error) = this._workspace.Run(arguments);
    Assert.That(exitCode, Is.Zero, error);
    return this._workspace.Manifest(output).RequestedConfig!;
  }

  [Test]
  public void TheSpellingsOfOneRequest_RecordOneLine() {
    var lines = new[] {
      this.Recorded("a", "translate", Sum32, "--from", "csharp", "--to", "javascript", "--export", "Add", "--output", "a"),
      this.Recorded("b", "--quiet", "translate", "--to=javascript", "--export", "Add", "--export", "Add", "--opt", "default", "--output", "b", "--from", "csharp", Sum32),
      this.Recorded("c", "translate", Sum32, "--diagnostics", "json", "--target-profile", "es2022", "--source-profile", "net10.0", "--from", "csharp", "--to", "javascript", "--export", "Add", "--output", "c"),
      this.Recorded("d", "translate", Sum32, "--from", "csharp", "--to", "javascript", "--export", "Add", "--profile", "es2022", "--output", "d", "--explain-opt")
    };
    Assert.That(lines.Distinct().ToArray(), Is.EqualTo(new[] {
      $"translate {Sum32} --export Add --from csharp --source-profile net10.0 --target-profile es2022 --to javascript --opt default"
    }));
  }

  [Test]
  public void ARequestWithNothingOptional_RecordsTheOwnOptionsInTheirOrder()
    => Assert.That(this.Recorded("out", "translate", Sum32, "--to", "javascript", "--from", "csharp", "--output", "out"),
      Is.EqualTo($"translate {Sum32} --from csharp --source-profile net10.0 --target-profile es2022 --to javascript --opt default"));

  [Test]
  public void TheOptionsOfALanguage_AreRecordedByName_FlagsWithoutValue()
    => Assert.That(this.Recorded("out", "translate", Sum32, "--from", "csharp", "--to", "javascript", "--typed-arrays", "--profile", "es5", "--module", "cjs", "--disable-opt", "O0500,O0006,O0500", "--output", "out"),
      Is.EqualTo($"translate {Sum32} --from csharp --module cjs --source-profile net10.0 --target-profile es5 --to javascript --typed-arrays --opt default --disable-opt O0006,O0500"));

  [Test]
  public void ThePythonProfile_IsRecordedInItsNormalSpelling()
    => Assert.That(this.Recorded("out", "translate", Sum32, "--from", "csharp", "--to", "python", "--target-profile", "3.11", "--output", "out"),
      Does.Contain(" --target-profile py3.11 "));

  [Test]
  public void TheInputPath_IsRecordedAsTyped_WithForwardSlashes() {
    var plain = this.Recorded("a", "translate", Sum32, "--from", "csharp", "--to", "javascript", "--output", "a");
    var dotted = this.Recorded("b", "translate", "./" + Sum32, "--from", "csharp", "--to", "javascript", "--output", "b");
    Assert.That((plain.Split(' ')[1], dotted.Split(' ')[1]), Is.EqualTo((Sum32, "./" + Sum32)));
  }

  [Test]
  public void AnInputThatNeedsQuoting_IsQuoted() {
    var spaced = this._workspace.Write("my file.cs", "public static class S { public static int F(int a) => a; }");
    Assert.That(this.Recorded("out", "translate", spaced, "--from", "csharp", "--to", "javascript", "--output", "out"), Does.StartWith("translate \"my file.cs\" --from"));
  }

  [Test]
  public void ABackslash_BecomesASlash()
    => Assert.That(RequestedConfig.Build("translate", ["dir\\sum32.cs"], new Dictionary<string, IReadOnlyList<string>>(), new HashSet<string>()), Is.EqualTo("translate dir/sum32.cs"));

  [Test]
  public void AList_IsSortedOnceWithoutDuplicates_AndKeepsTheCommasInsideBrackets() {
    var options = new Dictionary<string, IReadOnlyList<string>> {
      ["--instantiate"] = ["Max[int,long]", "Min[int],Max[int,long]"],
      ["--export"] = ["B", "A,B"]
    };
    Assert.That(RequestedConfig.Build("translate", ["a.cs"], options, new HashSet<string> { "--export", "--instantiate" }),
      Is.EqualTo("translate a.cs --export A,B --instantiate \"Max[int,long],Min[int]\""));
  }

  [Test]
  public void ThePipelineOptions_FollowTheOwnOptionsInTheirFixedOrder() {
    var options = new Dictionary<string, IReadOnlyList<string>> {
      ["--strict"] = [],
      ["--disable-opt"] = ["O0002"],
      ["--zeta"] = ["z"],
      ["--opt"] = ["speed"],
      ["--assume"] = ["x"]
    };
    Assert.That(RequestedConfig.Build("translate", ["a.cs"], options, new HashSet<string> { "--disable-opt" }),
      Is.EqualTo("translate a.cs --zeta z --opt speed --disable-opt O0002 --assume x --strict"));
  }

  [TestCase("", "\"\"")]
  [TestCase("a$b", "\"a\\$b\"")]
  [TestCase("a_b.c/d:e=f,g+h@i%j-k", "a_b.c/d:e=f,g+h@i%j-k")]
  public void AValue_IsQuotedByTheRuleOfTheAdr(string value, string recorded)
    => Assert.That(RequestedConfig.Build("translate", [], new Dictionary<string, IReadOnlyList<string>> { ["--namespace"] = [value] }, new HashSet<string>()),
      Is.EqualTo($"translate --namespace {recorded}"));

  [TestCase("a", new[] { "a" })]
  [TestCase("a,b", new[] { "a", "b" })]
  [TestCase("F[int,long],G", new[] { "F[int,long]", "G" })]
  [TestCase("F[A[int,int],long]", new[] { "F[A[int,int],long]" })]
  [TestCase("", new[] { "" })]
  public void Items_SplitAtTheCommasOutsideBrackets(string value, string[] items)
    => Assert.That(RequestedConfig.Items(value), Is.EqualTo(items));

  [Test]
  public void NullArguments_AreRefused() {
    var none = new Dictionary<string, IReadOnlyList<string>>();
    var empty = new HashSet<string>();
    Assert.That(() => RequestedConfig.Build(null!, [], none, empty), Throws.ArgumentNullException);
    Assert.That(() => RequestedConfig.Build("t", null!, none, empty), Throws.ArgumentNullException);
    Assert.That(() => RequestedConfig.Build("t", [], null!, empty), Throws.ArgumentNullException);
    Assert.That(() => RequestedConfig.Build("t", [], none, null!), Throws.ArgumentNullException);
    Assert.That(() => RequestedConfig.Items(null!).ToList(), Throws.ArgumentNullException);
  }

}
