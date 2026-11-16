using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Lists, maps and sets as Rust writes them: a list a <c>Vec</c>, a map or a set of .NET a vector of
/// slots in the order its keys are enumerated, the helpers that keep the order of freed slots, loops
/// over them, collections passed borrowed, and the forms that stay refused.
/// </summary>
[TestFixture]
public sealed class CollectionEmitTests {

  private static string Emitted(string methods) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", "using System.Collections.Generic; " + PrototypeFixture.Wrap(methods))));

  private static string[] Problems(string source)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", "using System.Collections.Generic; " + source)).Select(problem => problem.Message)];

  [Test]
  public void List_IsAVec() {
    var text = Emitted("public static int F(int i) { var list = new List<int>(); list.Add(3); list.Insert(0, 7); list[1] = 4; list.RemoveAt(0); list.Add(2); "
      + "return list[i] + list.Count + list.IndexOf(2) + (list.Contains(4) ? 1 : 0); }");
    Assert.That(text, Does.Contain("let mut list: Vec<i32> = Vec::new();"));
    Assert.That(text, Does.Contain("list.push(3);"));
    Assert.That(text, Does.Contain("list.insert(ply_list_insert_index(0, list.len()), 7);"));
    Assert.That(text, Does.Contain("let index = ply_list_index(1, list.len());\n    list[index] = 4;"));
    Assert.That(text, Does.Contain("list.remove(ply_list_index(0, list.len()));"));
    Assert.That(text, Does.Contain("list[ply_list_index(i, list.len())]"));
    Assert.That(text, Does.Contain("list.len() as i32"));
    Assert.That(text, Does.Contain("list.iter()\n                .position(|element| element == &2)\n                .map_or(-1, |at| at as i32)"));
    Assert.That(text, Does.Contain("list.contains(&4)"));
    Assert.That(text, Does.Contain("fn ply_list_index(index: i32, count: usize) -> usize {\n    if index < 0 || index as usize >= count {\n        panic!(\"ArgumentOutOfRange\");\n    }\n    index as usize\n}"));
  }

  [Test]
  public void ListOfStringsAndFloats_KeepsTheirEquality() {
    var text = Emitted("public static int F(double v, string s) { var a = new List<double> { double.NaN }; var b = new List<string> { \"x\" }; b.Add(s); "
      + "return a.IndexOf(v) + (a.Contains(v) ? 1 : 0) + b[1].Length + b.IndexOf(s); }");
    Assert.That(text, Does.Contain("a.iter()\n        .position(|element| ply_same_value_f64(*element, v))"));
    Assert.That(text, Does.Contain("a.iter().any(|element| ply_same_value_f64(*element, v))"));
    Assert.That(text, Does.Contain("b.push(s.to_vec());"));
    Assert.That(text, Does.Contain("b[ply_list_index(1, b.len())].clone().len()"));
  }

  [Test]
  public void ListFromAndToAnArray_CopiesTheElements() {
    var text = Emitted("public static int[] F(int[] a) { var list = new List<int>(a); list.Add(1); return list.ToArray(); }");
    Assert.That(text, Does.Contain("let mut list: Vec<i32> = a.to_vec();"));
    Assert.That(text, Does.Contain("list.to_vec()"));
  }

  [Test]
  public void Map_IsSlotsInTheOrderDotNetEnumerates() {
    var text = Emitted("public static int F(int k) { var map = new Dictionary<int, int>(); map[1] = 2; map.Add(3, 4); var added = map.TryAdd(5, 6); map.Remove(1); "
      + "map[k] = map[3] + 1; var sum = 0; foreach (var entry in map) sum = sum * 10 + entry.Key + entry.Value; "
      + "return sum + map.Count + (added && map.ContainsKey(k) ? 1 : 0); }");
    Assert.That(text, Does.Contain("let mut map: Vec<Result<(i32, i32), u32>> = Vec::new();"));
    Assert.That(text, Does.Contain("ply_map_put(&mut map, 1, 2);"));
    Assert.That(text, Does.Contain("ply_map_add(&mut map, 3, 4);"));
    Assert.That(text, Does.Contain("ply_map_try_add(&mut map, 5, 6)"));
    Assert.That(text, Does.Contain("ply_map_remove(&mut map, &1);"));
    Assert.That(text, Does.Contain("let value = ply_map_get(&map, &3).expect(\"KeyNotFound\").wrapping_add(1);\n    ply_map_put(&mut map, k, value);"));
    Assert.That(text, Does.Contain("for entry in map.iter().flatten() {"));
    Assert.That(text, Does.Contain("map.iter().flatten().count() as i32"));
    Assert.That(text, Does.Contain("ply_map_find(&map, &k).is_some()"));
    Assert.That(text, Does.Contain("fn ply_slot_freed<T>(slots: &[Result<T, u32>]) -> Option<usize> {"));
  }

  [Test]
  public void MapOfStrings_LoopsOverCopiesOfItsEntries() {
    var text = Emitted("public static string F(string w) { var map = new Dictionary<string, string>(); map[w] = w; var text = \"\"; "
      + "foreach (var entry in map) text += entry.Key + entry.Value; foreach (var value in map.Values) text += value; return text; }");
    Assert.That(text, Does.Contain("let mut map: Vec<Result<(Vec<u16>, Vec<u16>), u32>> = Vec::new();"));
    Assert.That(text, Does.Contain("ply_map_put(&mut map, w.to_vec(), w.to_vec());"));
    Assert.That(text, Does.Contain(".0.clone();"));
    Assert.That(text, Does.Contain(".1.clone();"));
  }

  [Test]
  public void Set_IsSlotsInTheOrderDotNetEnumerates() {
    var text = Emitted("public static int F(int v) { var set = new HashSet<int> { 1, 2 }; set.Remove(1); var added = set.Add(v); set.Clear(); set.Add(3); "
      + "var sum = 0; foreach (var item in set) sum += item; return sum + set.Count + (added && set.Contains(v) ? 1 : 0); }");
    Assert.That(text, Does.Contain("let mut set: Vec<Result<i32, u32>> = Vec::new();"));
    Assert.That(text, Does.Contain("ply_set_add(&mut set, 1);"));
    Assert.That(text, Does.Contain("ply_set_remove(&mut set, &1);"));
    Assert.That(text, Does.Contain("set.clear();"));
    Assert.That(text, Does.Contain("ply_set_find(&set, &v).is_some()"));
    Assert.That(text, Does.Contain("let item: i32 = *entry;").Or.Contain("let item = *entry;"));
  }

  [Test]
  public void ListLoop_CopiesItsElement() {
    var text = Emitted("public static int F() { var list = new List<int> { 1, 2 }; var words = new List<string> { \"a\" }; var sum = 0; "
      + "foreach (var item in list) sum += item; foreach (var word in words) sum += word.Length; return sum; }");
    Assert.That(text, Does.Contain("for entry in list.iter() {\n        let item = *entry;"));
    Assert.That(text, Does.Contain("let word = entry1.clone();"));
  }

  [Test]
  public void CollectionPassed_IsBorrowed_SharedOrExclusive() {
    var text = Emitted("static int Sum(List<int> list) { var s = 0; foreach (var v in list) s += v; return s; } static void Fill(List<int> list) => list.Add(1); "
      + "static int Size(Dictionary<int, int> map) => map.Count; public static int F() { var list = new List<int>(); Fill(list); var map = new Dictionary<int, int>(); return Sum(list) + Size(map); }");
    Assert.That(text, Does.Contain("fn sum(list: &[i32]) -> i32 {"));
    Assert.That(text, Does.Contain("fn fill(list: &mut Vec<i32>) {"));
    Assert.That(text, Does.Contain("fn size(map: &[Result<(i32, i32), u32>]) -> i32 {"));
    Assert.That(text, Does.Contain("fill(&mut list);"));
    Assert.That(text, Does.Contain("sum(&list).wrapping_add(size(&map))"));
  }

  [Test]
  public void LoopThatAlwaysLeavesOnItsFirstPass_IsAnIf() {
    var text = Emitted("public static bool F(int[] a) { var i = 0; while (i < a.Length) { i++; return true; } return false; }");
    Assert.That(text, Does.Contain("if i < a.len() as i32 {"));
    Assert.That(text, Does.Not.Contain("while"));
  }

  [TestCase("public sealed class Box { public int V; } public static class S { public static int F() { var list = new List<Box>(); return list.Count; } }", "'a list of Box'")]
  [TestCase("public static class S { public static int F() { var map = new Dictionary<double, int>(); return map.Count; } }", "'the collection map<f64, i32, slotreuse>'")]
  [TestCase("public static class S { public static int F() { var list = new List<int> { 1 }; foreach (var v in list) list.Add(v); return list.Count; } }", "'a collection changed in a loop over it'")]
  [TestCase("public static class S { public static int F(bool c) { var a = new List<int>(); var d = new List<int>(); var b = c ? a : d; b.Add(1); return a.Count; } }", "'a collection held by two variables'")]
  [TestCase("public static class S { static int Size(List<int> l) => l.Count; public static int F() { var a = new List<int>(); List<int> b; b = a; b.Add(1); return Size(a) + b.Count; } }", "'a collection held by two variables'")]
  [TestCase("public static class S { static List<int> Make() => new List<int>(); static int Size(List<int> l) => l.Count; public static int F() => Size(Make()); }", "'a collection passed on that no variable holds'")]
  public void CollectionOutsideTheForm_IsRefused(string source, string construct)
    => Assert.That(Problems(source), Has.Some.Contain(construct));

  [Test]
  public void CollectionInTheOrderOfInsertion_IsRefused() {
    var imported = Polypiler.Core.Frontends.JavaScript.JavaScriptFrontend.Import("map.mjs", "export function f(k) { const m = new Map(); m.set(k, 1); return m.size; }");
    Assert.That(RustCrates.Check(imported.Module!).Select(problem => problem.Message), Has.Some.Contain("in the order of insertion"));
  }

}
