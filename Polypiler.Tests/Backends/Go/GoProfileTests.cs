using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Go;
using Polypiler.Core.Backends.Go.Passes;
using Polypiler.Core.Backends.Go.Printing;
using Polypiler.Core.Backends.Go.Syntax;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Go;

/// <summary>The profiles of the Go backend, its capability table and its names.</summary>
[TestFixture]
public sealed class GoProfileTests {

  [TestCase(null, "go1.27"), TestCase("go/1", "go1.27"), TestCase("go1.18", "go1.18"), TestCase("1.22", "go1.22"), TestCase("go1.25", "go1.25")]
  public void Profile_IsReadFromItsName(string? name, string expected) {
    Assert.That(GoProfile.Parse(name).Name, Is.EqualTo(expected));
    Assert.That(GoProfile.Parse(name).ToString(), Is.EqualTo(expected));
    Assert.That(GoProfile.Parse(name).IntWidth, Is.EqualTo(64));
  }

  [TestCase("go1.17"), TestCase("go1.28"), TestCase("go2"), TestCase("")]
  public void Profile_OutsideTheVersions_IsRefused(string name) {
    Assert.That(GoProfile.TryParse(name, out var profile, out var error), Is.False);
    Assert.That((profile, error), Is.EqualTo(((GoProfile?)null, $"unknown Go profile '{name}'; the profiles are go1.18 to go1.27 and go/1")));
    Assert.That(() => GoProfile.Parse(name), Throws.ArgumentException);
  }

  [Test]
  public void Profiles_ListTheVersionsThatBroughtAConstruct() {
    Assert.That(GoProfile.Names, Is.EqualTo(new[] { "go1.18", "go1.21", "go1.22", "go1.23", "go1.27" }));
    Assert.That(GoProfile.All, Has.Count.EqualTo(10));
    Assert.That(GoProfile.Default.Version, Is.EqualTo("1.27"));
  }

  [Test]
  public void Capabilities_SayWhatEachVersionHas() {
    var table = GoCapabilities.Standard;
    Assert.That(table.Missing(GoProfile.Parse("go1.18")), Is.EqualTo(new[] { GoCapabilities.MinMax, GoCapabilities.Slices, GoCapabilities.Clear, GoCapabilities.RangeOverInt, GoCapabilities.PerIterationLoopVariables, GoCapabilities.RangeOverFunc }));
    Assert.That(table.Missing(GoProfile.Parse("go1.22")), Is.EqualTo(new[] { GoCapabilities.RangeOverFunc }));
    Assert.That(table.Missing(GoProfile.Default), Is.Empty);
    Assert.That(table[GoCapabilities.MinMax].Replacement, Is.EqualTo("a helper per type"));
    Assert.That(() => table["goto"], Throws.ArgumentException);
    Assert.That(() => new GoCapabilities([new(GoCapabilities.Generics, 18), new(GoCapabilities.Generics, 19)]), Throws.ArgumentException);
  }

  [TestCase("fnv1a_32", true, "Fnv1a32")]
  [TestCase("crc32_update", true, "CRC32Update")]
  [TestCase("crc32_update", false, "crc32Update")]
  [TestCase("get_id", true, "GetID")]
  [TestCase("HTTPServer", false, "httpServer")]
  [TestCase("func", false, "func_")]
  [TestCase("len", false, "len_")]
  [TestCase("1st", false, "_1st")]
  [TestCase("1st", true, "X1st")]
  [TestCase("$", true, "X")]
  [TestCase("$", false, "value")]
  [TestCase("Def<bool>", true, "DefOfBool")]
  public void Names_AreMixedCaps(string name, bool exported, string expected)
    => Assert.That(GoNaming.Function(name, exported), Is.EqualTo(expected));

  [TestCase("example.test/polychecksums", "polychecksums")]
  [TestCase("example.test/polychecksums/v2", "polychecksums")]
  [TestCase("Checksums.Adler32", "adler32")]
  [TestCase("my_pkg", "mypkg")]
  [TestCase("2fast", "p2fast")]
  [TestCase("", "p")]
  [TestCase("map", "map_")]
  public void Package_IsTheLastElementOfThePath(string path, string expected) => Assert.That(GoNaming.Package(path), Is.EqualTo(expected));

  [TestCase("Statements", "statements.go")]
  [TestCase("Linux", "linux.go")]
  [TestCase("MathLinux", "math_linux_go.go")]
  [TestCase("ParserTest", "parser_test_go.go")]
  [TestCase("$", "module.go")]
  public void File_IsSnakeCase_AndNeverABuildConstraint(string name, string expected) => Assert.That(GoNaming.File(name), Is.EqualTo(expected));

  [TestCase("example.test/x", true), TestCase("x", true), TestCase("Example Test/x", false), TestCase("", false), TestCase(null, false), TestCase("a/../b", false), TestCase("/a", false), TestCase("a/", false)]
  public void ModulePath_IsCheckedAsTheGoCommandChecksIt(string? path, bool valid) => Assert.That(GoNaming.IsModulePath(path), Is.EqualTo(valid));

  [Test]
  public void Free_TakesTheNextNumber() {
    Assert.That(GoNaming.Free("x", name => name == "x"), Is.EqualTo("x2"));
    Assert.That(GoNaming.Free("len_", name => name == "len_"), Is.EqualTo("len2"));
    Assert.That(GoNaming.Usable("ok"), Is.EqualTo("ok"));
  }

  [Test]
  public void UnusedHelpers_AreRemoved_AndReportedAsTheyGo() {
    var helper = new GoFuncDecl("plyUnused", new GoFuncType([], []), new GoBlock([new GoExprStmt(new GoCall(new GoIdent("plyAlsoUnused"), []))])) { IsHelper = true };
    var other = new GoFuncDecl("plyAlsoUnused", new GoFuncType([], []), new GoBlock([])) { IsHelper = true };
    var used = new GoFuncDecl("plyUsed", new GoFuncType([], []), new GoBlock([])) { IsHelper = true };
    var own = new GoFuncDecl("F", new GoFuncType([], []), new GoBlock([new GoExprStmt(new GoCall(new GoIdent("plyUsed"), []))]));
    var file = new GoFile([], "p", [], [own, helper, other, used]);
    var applied = new System.Collections.Generic.List<string>();
    var result = new GoUnusedDeclarations().Run(file, GoProfile.Default, applied.Add);
    Assert.That(result.Declarations.OfType<GoFuncDecl>().Select(function => function.Name), Is.EqualTo(new[] { "F", "plyUsed" }));
    Assert.That(applied, Has.Count.EqualTo(2));
    Assert.That(new GoUnusedDeclarations().Run(result, GoProfile.Default, applied.Add), Is.SameAs(result));
    Assert.That(GoTargetPasses.Pipeline.Ids, Is.EqualTo(new[] { "O0501" }));
  }

}

/// <summary>
/// Every prototype source, C# and JavaScript, translated to Go in the oldest and the newest profile:
/// the reader reads the printed text back into a tree of the same shape, which prints the same text;
/// a second translation prints the same; no helper is left that nothing names. A source the backend
/// refuses is refused with a code of its own.
/// </summary>
[TestFixture]
[Category("Slow")]
public sealed class GoParseBackTests {

  private static readonly string _sources = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype");

  private static System.Collections.Generic.IEnumerable<TestCaseData> Cases()
    => from source in GoldenOutputs.All
       from profile in new[] { "go1.18", "go1.27" }
       select new TestCaseData(source, profile).SetName($"GoParseBack({source}, {profile})");

  [TestCaseSource(nameof(Cases))]
  public void EverySource_ParsesBackToTheTreeItWasPrintedFrom(string source, string profile) {
    var path = Path.Combine(_sources, source.Replace('/', Path.DirectorySeparatorChar));
    var text = File.ReadAllText(path);
    var module = source.EndsWith(".cs", System.StringComparison.Ordinal)
      ? CSharpFrontend.Import(Path.GetFileName(path), text).Module
      : JavaScriptFrontend.Import(Path.GetFileName(path), text).Module;
    if (module is null) {
      Assert.Pass("the frontend refuses the source");
      return;
    }

    var parsed = GoProfile.Parse(profile);
    var lowered = GoEmitter.Legalize(module, () => Polypiler.Cli.PrecomputedImplementations.Module, parsed, FloatMode.Accurate);
    var problems = GoEmitter.Check(lowered, parsed);
    if (problems.Count > 0) {
      Assert.That(problems.Select(problem => problem.Code), Is.All.Match("^PLY21[5-7][0-9]$|^PLY0[0-9]{3}$"));
      return;
    }

    var built = GoEmitter.Build(lowered, parsed, FloatMode.Accurate, null, TargetPassOptions.Default).Tree;
    var (sourceFile, runtime) = GoEmitter.Split(built);
    foreach (var file in new[] { sourceFile, runtime }.OfType<GoFile>()) {
      var printed = GoPrinter.Print(file);
      var read = GoSyntaxReader.ReadFile(printed);
      Assert.That(GoSyntaxRewriter.Shape(read), Is.EqualTo(GoSyntaxRewriter.Shape(file)), printed);
      Assert.That(GoPrinter.Print(read), Is.EqualTo(printed));
    }

    var names = built.Declarations.SelectMany(GoSyntaxRewriter.Names).ToList();
    Assert.That(built.Declarations.OfType<GoFuncDecl>().Where(function => function.IsHelper && !names.Contains(function.Name)), Is.Empty, "helpers nothing names");
    var again = GoEmitter.Emit(lowered, parsed).Select(file => file.Content).ToList();
    Assert.That(GoEmitter.Emit(lowered, parsed).Select(file => file.Content), Is.EqualTo(again));
  }

}
