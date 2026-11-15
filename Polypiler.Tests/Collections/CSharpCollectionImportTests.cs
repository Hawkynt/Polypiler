using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Collections;

/// <summary>
/// The C# frontend on lists, dictionaries and sets (#1328): what it imports, run by the interpreter
/// against the values .NET computes for the same methods (the fixture tests compare with the build
/// itself), and every refusal by name.
/// </summary>
[TestFixture]
public sealed class CSharpCollectionImportTests {

  private static IrModule Imported(string method) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    Assert.That(result.Module, Is.Not.Null, string.Join("\n", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static IReadOnlyList<string> Problems(string method) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    return result.Module is null ? [.. result.Diagnostics.Select(diagnostic => diagnostic.Message)] : [];
  }

  private const string Dictionary = "System.Collections.Generic.Dictionary";

  /// <summary>What .NET 10 computes for each method, run by the interpreter.</summary>
  [TestCase("public static int F() { var d = new " + Dictionary + "<int, int> { [1] = 5 }; if (!d.TryGetValue(2, out var v)) return v - 1; else return v; }", "-1")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<int, int> { [1] = 5 }; int v = 9; if (d.TryGetValue(1, out v)) { } return v; }", "5")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<int, int> { [1] = 5 }; if (d.TryGetValue(2, out var v)) return 1; return v; }", "0")]
  [TestCase("public static string F() { var d = new " + Dictionary + "<int, string> { [1] = \"a\" }; if (d.TryGetValue(1, out var v)) return v; return \"-\"; }", "a")]
  [TestCase("public static double F() { var d = new " + Dictionary + "<int, double> { [1] = 0.5 }; if (!d.TryGetValue(1, out var v)) return -1; return v; }", "f:3fe0000000000000")]
  [TestCase("public static long F() { var d = new " + Dictionary + "<int, int> { [1] = 2, [3] = 4 }; long s = 0; foreach (long v in d.Values) s = s * 10 + v; return s; }", "24")]
  [TestCase("public static long F() { var d = new " + Dictionary + "<int, int> { [1] = 2 }; long s = 0; foreach (var (k, v) in d) s = k * 10 + v; return s; }", "12")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<string, int>(); d[\"a\"] = 1; d[\"a\"] += 2; return d[\"a\"] + d.Count * 10; }", "13")]
  [TestCase("public static int F() { var s = new System.Collections.Generic.HashSet<string> { \"a\", \"a\" }; int n = 0; foreach (var w in s) n += w.Length; return n * 10 + s.Count; }", "11")]
  [TestCase("public static int F() { var l = new System.Collections.Generic.List<long> { 1 }; int n = 0; foreach (int x in l) n += x; return n; }", "1")]
  public void Method_ComputesWhatDotNetComputes(string method, string outcome)
    => Assert.That(PrototypeFixture.InterpretedOutcome(Imported(method), new PrototypeCall("F", [])), Is.EqualTo(outcome));

  /// <summary>Dictionaries and sets of C# enumerate in .NET's order of slots.</summary>
  [Test]
  public void DictionaryAndSet_AreOfTheSlotOrder() {
    var module = Imported("public static int F() { var d = new " + Dictionary + "<int, bool>(); var s = new System.Collections.Generic.HashSet<int>(); return d.Count + s.Count; }");
    var locals = module.Functions.Single().Locals;
    Assert.That(locals.Single(local => local.Name == "d").Type, Is.EqualTo(IrType.Map(IrType.I32, IrType.Bool, MapOrder.SlotReuse)));
    Assert.That(locals.Single(local => local.Name == "s").Type, Is.EqualTo(IrType.Set(IrType.I32, MapOrder.SlotReuse)));
  }

  /// <summary>Test design 11: every refused type and member, by name.</summary>
  [TestCase("public static int F() { var d = new System.Collections.Generic.SortedDictionary<int, int>(); d[1] = 1; return d.Count; }", "SortedDictionary<int, int>")]
  [TestCase("public static int F() { var q = new System.Collections.Generic.Queue<int>(); return q.Count; }", "Queue<int>")]
  [TestCase("public static int F() { var l = new System.Collections.Generic.List<int>(4); return l.Count; }", "new List<int>(int)")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<string, int>(System.StringComparer.Ordinal); return d.Count; }", "new Dictionary<string, int>(System.StringComparer)")]
  [TestCase("public static int F() { var l = new System.Collections.Generic.List<int>(); l.Sort(); return l.Count; }", "List<int>.Sort")]
  [TestCase("public static int F() { var l = new System.Collections.Generic.List<int>(); return l.Capacity; }", "List<int>.Capacity")]
  [TestCase("public static bool F() { var d = new " + Dictionary + "<int, int>(); return d.TryGetValue(1, out var v); }", "Dictionary<int, int>.TryGetValue")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<int, int>(); foreach (var e in d) { var x = e; } return 0; }", "KeyValuePair<int, int>")]
  [TestCase("public static string F() { var d = new " + Dictionary + "<int, string>(); if (!d.TryGetValue(1, out var v)) return v; return \"\"; }", "TryGetValue of a string that is read where the key is missing")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<int, int>(); int[] a = new int[1]; if (d.TryGetValue(1, out a[0])) return 1; return 0; }", "an out argument of TryGetValue other than a variable of the value type")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<int, int>(); foreach ((int k, int v) in d) { } return 0; }", "a foreach over a dictionary that deconstructs its entry into anything but two new variables")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<P, int>(); return d.Count; } public struct P { public int X; }", "Dictionary<Snippet.P, int>")]
  [TestCase("public static int F() { var l = new System.Collections.Generic.List<B>(); return l.ToArray().Length; } public sealed class B { }", "B[]")]
  [TestCase("public static int F() { return Make(new System.Collections.Generic.List<int> { 1 }); } static int Make(System.Collections.Generic.List<int> l) => l.Count;", "a collection initializer other than the value of a variable or a return")]
  [TestCase("public static int F() { System.Collections.Generic.List<int> l = [.. new int[1]]; return l.Count; }", "a spread in a collection expression")]
  [TestCase("public static int F() { var d = new " + Dictionary + "<int, int> { { 1, 2, 3 } }; return d.Count; }", "")]
  [TestCase("public static int F() { var l = new System.Collections.Generic.List<int> { 1 }; return l[0]++; }", "a step of a list element whose value is used")]
  public void Construct_IsRefusedByName(string method, string named) {
    var problems = Problems(method);
    Assert.That(problems, Is.Not.Empty);
    Assert.That(problems, Has.Some.Contains(named));
  }

}
