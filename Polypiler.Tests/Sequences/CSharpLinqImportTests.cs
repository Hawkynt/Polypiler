using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// LINQ imported by the C# frontend (#1329): what each member becomes, what is refused by name, and that
/// the fixtures are written by every profile of both backends.
/// </summary>
[TestFixture]
internal sealed class CSharpLinqImportTests {

  private const string Usings = "using System; using System.Collections.Generic; using System.Linq;\n";

  private static ImportResult Import(string methods) => CSharpFrontend.Import("Snippet.cs", Usings + PrototypeFixture.Wrap(methods));

  private static IrModule Imported(string methods) {
    var result = Import(methods);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.Empty);
    return result.Module!;
  }

  private static IReadOnlyList<IrSequenceOp> Operations(IrModule module, string function = "F")
    => [.. IrTree.Nodes(module.Functions.Single(candidate => candidate.Name == function).Body).OfType<IrSequence>().Select(node => node.Op)];

  /// <summary>Test design 15: a member outside the table, a culture-sensitive or foreign comparer, a parallel or queryable sequence - each refused naming it, nothing imported.</summary>
  [TestCase("public static string F(string[] a) => a.OrderBy(s => s).First();", "culture-sensitive comparison")]
  [TestCase("public static string F(string[] a) => a.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).First();", "a comparer other than StringComparer.Ordinal")]
  [TestCase("public static int F(int[] a) => a.OrderBy(s => s, Comparer<int>.Default).First();", "a comparer other than StringComparer.Ordinal")]
  [TestCase("public static int F(int[] a) { var o = a.OrderBy(x => x); return o.ThenBy(x => -x).First(); }", "of an ordered sequence held in a variable")]
  [TestCase("public static int F(int[] a) => a.Zip(a.Select(x => x), (x, y) => x + y).Sum();", "Zip with a second sequence that is no array and no list")]
  [TestCase("public static int F(int[] a) => a.Zip(a).Count();", "'(int First, int Second)'")]
  [TestCase("public static int F(int[] a) => a.AsParallel().Sum();", "ParallelEnumerable.Sum, a parallel or a queryable sequence")]
  [TestCase("public static int F(int[] a) => a.AsQueryable().Count();", "Queryable.Count, a parallel or a queryable sequence")]
  [TestCase("public static int F(int[] a) => a.Distinct().Count();", "Enumerable.Distinct")]
  [TestCase("public static int F(int[] a) => a.GroupBy(x => x).Count();", "IGrouping<int, int>")]
  [TestCase("public static int F(int[] a) => a.GroupBy(x => x).Select(g => 1).Count();", "IGrouping<int, int>")]
  [TestCase("public static int F(int[] a) => a.Last();", "Enumerable.Last")]
  [TestCase("public static int F(int[] a) => a.Take(1..2).Count();", "Enumerable.Take")]
  [TestCase("public static int F(int[] a) => a.Select((x, i) => x).SelectMany((x, i) => new[] { x }).Count();", "Enumerable.SelectMany")]
  [TestCase("public static int F(int[] a) => a.First(predicate: x => x > 1);", "Enumerable.First with a named argument")]
  [TestCase("public static int F(int[] a) => a?.Count() ?? 0;", "a null-conditional access")]
  [TestCase("public static int F(string s) => s.Count();", "LINQ over a string")]
  [TestCase("public static decimal F(decimal[] a) => a.Sum();", "decimal")]
  [TestCase("public static int F(int[] a) => a.Sum(x => (int?)x) ?? 0;", "Enumerable.Sum")]
  [TestCase("static readonly int[] t = { 1, 2 }; public static int F() => t.Count();", "the static readonly array 't' used as a value")]
  [TestCase("static readonly int[] t = { 1, 2 }; public static bool F() => t.Contains(1);", "the static readonly array 't' used as a value")]
  [TestCase("static readonly int[] t = { 1, 2 }; public static int F(int[] a) => a.Zip(t, (x, y) => x).Count();", "the static readonly array 't' used as a value")]
  [TestCase("public static int F(int[] a) => Array.IndexOf((Array)a, 1);", "Array.IndexOf")]
  [TestCase("public static int F(int[] a) => Array.FindIndex(a, 1, x => x > 0);", "Array.FindIndex")]
  [TestCase("public static int F(int[] a) => Array.Find(match: x => x > 0, array: a);", "Array.Find")]
  [TestCase("public static int F(int[] a) => (from x in a join y in a on x equals y select x).Count();", "of a query")]
  [TestCase("public static int F(int[] a) => (from x in a group x by x).Count();", "IGrouping<int, int>")]
  [TestCase("public static int F(int[] a) => (from x in a select x into y select y).Count();", "into of a query")]
  [TestCase("public static int F(int[] a) => (from int x in a select x).Count();", "a typed range variable of a query")]
  [TestCase("public static int F(int[] a) => (from x in a from int y in a select y).Count();", "a typed range variable of a query")]
  [TestCase("public static int F(int[] a) => (from x in a let y = x orderby y select y).Count();", "an orderby after a let or a second from")]
  [TestCase("public static int F(string[] a) => (from x in a orderby x select x).Count();", "orderby of a string key, a culture-sensitive comparison")]
  [TestCase("public static int F(int[] a) => (from x in a group x by x into g select g.Key).Count();", "into of a query")]
  [TestCase("public static int F() => Enumerable.Empty<int>().Count();", "Enumerable.Empty")]
  [TestCase("public sealed class Box { public Box Select(Func<int, int> f) => this; } public static int F(Box b) { var q = from x in b select x + 1; return 0; }", "a query over Box")]
  [TestCase("public static bool F(int[] a) { ReadOnlySpan<int> s = a; return s.Contains(1); }", "MemoryExtensions.Contains")]
  public void Construct_IsRefusedByName(string methods, string what) {
    var result = Import(methods);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(CSharpFrontend.Unsupported));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(what));
  }

  /// <summary>Test design 2: every member of the table is the operation of the same name.</summary>
  [TestCase("public static int F(int[] a) => a.Select(x => x).Where(x => x > 0).Sum();", new[] { IrSequenceOp.Sum, IrSequenceOp.Where, IrSequenceOp.Select, IrSequenceOp.FromArray })]
  [TestCase("public static int F(int[] a) => Enumerable.Count(Enumerable.Where(a, (x, i) => i > 0));", new[] { IrSequenceOp.Count, IrSequenceOp.WhereIndexed, IrSequenceOp.FromArray })]
  [TestCase("public static int F(int[] a) => a.AsEnumerable().Skip(1).Take(2).Max(x => x * 2);", new[] { IrSequenceOp.Max, IrSequenceOp.Select, IrSequenceOp.Take, IrSequenceOp.Skip, IrSequenceOp.FromArray })]
  [TestCase("public static int F(int[] a) => a.Min(x => -x);", new[] { IrSequenceOp.Min, IrSequenceOp.Select, IrSequenceOp.FromArray })]
  [TestCase("public static int F(int[] a) => a.Aggregate(1, (s, x) => s * x, s => s + 1);", new[] { IrSequenceOp.AggregateSeed, IrSequenceOp.FromArray })]
  [TestCase("public static bool F(int[] a) => a.Any(x => x > 1) && a.All(x => x > 0);", new[] { IrSequenceOp.AnyWhere, IrSequenceOp.FromArray, IrSequenceOp.All, IrSequenceOp.FromArray })]
  [TestCase("public static int F(List<int> a) => a.OrderByDescending(x => x).ThenByDescending(x => -x).FirstOrDefault(x => x > 9);",
    new[] { IrSequenceOp.FirstOrDefaultWhere, IrSequenceOp.ThenByDescending, IrSequenceOp.OrderByDescending, IrSequenceOp.FromList })]
  [TestCase("public static List<int> F(IEnumerable<int> a) => a.ToList();", new[] { IrSequenceOp.ToList })]
  [TestCase("public static bool F(int[] a) => Array.Exists(a, x => x < 0) || Array.TrueForAll(a, x => x > 0);", new[] { IrSequenceOp.AnyWhere, IrSequenceOp.FromArray, IrSequenceOp.All, IrSequenceOp.FromArray })]
  [TestCase("public static int F(int[] a) => (from x in a select x).Count();", new[] { IrSequenceOp.Count, IrSequenceOp.FromArray })]
  [TestCase("public static bool F(IEnumerable<int> s) => s.Contains(2);", new[] { IrSequenceOp.Contains })]
  [TestCase("public static int F(int[] a) => (from x in a orderby x % 2, x descending select x).First();", new[] { IrSequenceOp.First, IrSequenceOp.ThenByDescending, IrSequenceOp.OrderBy, IrSequenceOp.FromArray })]
  [TestCase("public static int F(int[] a) => (from x in a orderby x descending, x select x).First();", new[] { IrSequenceOp.First, IrSequenceOp.ThenBy, IrSequenceOp.OrderByDescending, IrSequenceOp.FromArray })]
  public void Member_IsTheOperationOfItsName(string methods, IrSequenceOp[] expected)
    => Assert.That(Operations(Imported(methods)), Is.EqualTo(expected));

  /// <summary>Test design 6: two Takes in one chain, either of which ends it; a Take of nothing anywhere pulls nothing.</summary>
  [TestCase(3, 1, 2)]
  [TestCase(0, 1, 0)]
  [TestCase(3, 0, 0)]
  public void TwoTakes_EndTheChain(int first, int second, int expected) {
    var module = Imported("public static int F(int[] a, int m, int n) { var calls = 0; var r = a.Select(x => { ++calls; return x; }).Take(m).Skip(1).Take(n).Sum(); return calls * 100 + r; }");
    var fused = SequenceFusion.Run(module);
    var calls = first == 0 || second == 0 ? 0 : System.Math.Min(first, 2);
    Assert.That(IrInterpreter.Call(module, "F", [new[] { 1, 2, 3, 4 }, first, second]), Is.EqualTo(calls * 100 + expected));
    Assert.That(IrInterpreter.Call(fused, "F", [new[] { 1, 2, 3, 4 }, first, second]), Is.EqualTo(calls * 100 + expected));
  }

  /// <summary>Aggregate of structs and of function values, whose fold starts from the default of each.</summary>
  [Test]
  public void Aggregate_OfStructsAndFunctions() {
    var module = Imported("public record struct P(int X); public static int F(int[] a) { var ps = a.Select(x => new P(x)).ToArray(); Func<int, int>[] fs = { x => x + 1, x => x * 3 }; return ps.Aggregate((p, q) => new P(p.X * 10 + q.X)).X + fs.Aggregate((f, g) => x => g(f(x)))(1); }");
    var fused = SequenceFusion.Run(module);
    Assert.That(IrVerifier.Verify(fused), Is.Empty);
    Assert.That(IrInterpreter.Call(fused, "F", [new[] { 1, 2 }]), Is.EqualTo(18));
    Assert.That(IrInterpreter.Call(module, "F", [new[] { 1, 2 }]), Is.EqualTo(18));
  }

  /// <summary>A delegate type the class declares names the collections and optional values of its signature after their elements.</summary>
  [Test]
  public void DeclaredDelegate_NamesCollectionsAfterTheirElements() {
    var module = Imported("public static int F() { Func<HashSet<int>, Dictionary<int, long>, int?, List<double>, int, int> f = (s, d, o, l, n) => s.Count + d.Count + (o ?? 0) + l.Count + n; return f(new HashSet<int>(), new Dictionary<int, long>(), 2, new List<double>(), 3); }");
    var profile = CSharpProfile.Parse("net3.5");
    var text = CSharpEmitter.Emit(CSharpEmitter.Legalize(module, () => Polypiler.Cli.PrecomputedImplementations.Module, profile, FloatMode.Accurate), profile)[0].Content;
    Assert.That(text, Does.Contain("Int32SetInt32Int64MapInt32OptionalDoubleListInt32ToInt32"), text);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
  }

  /// <summary>Test design 5: a sequence enumerated twice runs its lambda twice; a captured variable is read when it is enumerated.</summary>
  [Test]
  public void ReEnumeration_AndLateCapture_AreKept() {
    var module = LinqFixtures.Imported("Pipelines");
    Assert.That(IrInterpreter.Call(module, "Recount", []), Is.EqualTo(850));
    Assert.That(IrInterpreter.Call(module, "Late", []), Is.EqualTo(new[] { 4 }));
  }

  /// <summary>The scenario values of the feature, as .NET measures them.</summary>
  [TestCase("Deferred", 312124), TestCase("IndexOfNaN", 0), TestCase("ContainsNaN", 1), TestCase("LazyTrap", 5)]
  public void Fixture_HasTheScenarioValues(string method, int expected)
    => Assert.That(LinqFixtures.Compiled("Pipelines", method).Invoke(null, []), Is.EqualTo(expected));

  [Test]
  public void Table_ListsWhatIsImportedAndWhatIsRefused() {
    Assert.That(CSharpSequenceApi.Supported.Count() + CSharpSequenceApi.Refused.Count(), Is.EqualTo(CSharpSequenceApi.Members.Count));
    Assert.That(CSharpSequenceApi.Supported.Select(member => member.Ir), Has.All.Not.Empty);
    Assert.That(CSharpSequenceApi.Refused.Select(member => member.Refusal), Has.Some.EqualTo("culture-sensitive comparison"));
  }

  /// <summary>Test design 1, 18 and 19: every C# profile writes the fixtures, which compile there; no profile lacks an operation.</summary>
  [TestCase("Pipelines"), TestCase("ByteMedian")]
  [Category("Slow")]
  public void EveryCSharpProfile_WritesTheFixture(string fixture) {
    var module = LinqFixtures.Imported(fixture);
    foreach (var profile in CSharpProfile.All) {
      var legalized = CSharpEmitter.Legalize(module, () => Polypiler.Cli.PrecomputedImplementations.Module, profile, FloatMode.Accurate);
      Assert.That(CSharpEmitter.Check(legalized, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(legalized, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1, 18 and 19: every JavaScript level writes the fixtures at that level.</summary>
  [TestCase("Pipelines"), TestCase("ByteMedian")]
  public void EveryJavaScriptLevel_WritesTheFixture(string fixture) {
    var module = LinqFixtures.Imported(fixture);
    foreach (var level in JavaScriptProfile.LevelNames) {
      var profile = JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm");
      var legalized = JavaScriptEmitter.Legalize(module, () => Polypiler.Cli.PrecomputedImplementations.Module, profile, FloatMode.Accurate);
      Assert.That(JavaScriptEmitter.Check(legalized, profile), Is.Empty, level);
      var text = JavaScriptEmitter.Emit(legalized, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{level}\n{text}");
    }
  }

}
