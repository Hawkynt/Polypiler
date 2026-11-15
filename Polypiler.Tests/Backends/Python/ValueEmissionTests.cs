using NUnit.Framework;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The forms of the values of #446 and #448 one by one: the string operations, the list, map and set
/// operations and the loops over them, the steps and assignments inside expressions, the closures that
/// capture a variable of every pass, and the accessors of classes and interfaces.
/// </summary>
[TestFixture]
public sealed class ValueEmissionTests {

  private static string Python(string source, string file = "Values.cs") {
    var translated = PythonFixtures.Translate(source, file);
    Assert.That(translated.Source, Is.Not.Null, string.Join("; ", translated.Problems));
    return translated.Source!;
  }

  [Test]
  public void StringOperations_AreStrMethodsOrTheHelpersThatCheck() {
    var text = Python("""
      public static class Values {
        public static string Units(char[] chars) => new string(chars) + new string(chars, 1, 1);
        public static char[] Back(string s) => s.ToCharArray();
        public static string Tail(string s) => s.Substring(2);
        public static int From(string s) => s.IndexOf("b", 1, System.StringComparison.Ordinal);
        public static bool End(string s) => s.EndsWith("z", System.StringComparison.Ordinal);
        public static string Swap(string s, string a, string b) => s.Replace(a, b);
        public static string[] Parts(string s) => s.Split(',');
        public static string Joined(string[] pieces) => string.Join("-", pieces);
        public static string Trimmed(string s) => s.Trim() + s.TrimStart() + s.TrimEnd();
        public static string Padded(string s) => s.PadLeft(5, '*') + s.PadRight(5);
        public static bool Order(string a, string b) => string.CompareOrdinal(a, b) < 0;
      }
      """);
    Assert.That(text, Does.Contain("\"\".join(map(chr, chars))").And.Contain("_ply_from_units(chars, 1, 1)").And.Contain("list(map(ord, s))"));
    Assert.That(text, Does.Contain("_ply_substring_from(s, 2)").And.Contain("_ply_index_of(s, \"b\", 1)").And.Not.Contain("_ply_last_index_of"));
    Assert.That(text, Does.Contain("s.endswith(\"z\")").And.Contain("_ply_replace_all(s, a, b)").And.Contain("_ply_split(s, chr(44), -1)").Or.Contain("_ply_split(s, \",\", -1)"));
    Assert.That(text, Does.Contain("\"-\".join(pieces)").And.Contain("_ply_trim_net(s, True, True)").And.Contain("_ply_compare(a, b)"));
  }

  [Test]
  public void ListAndMapOperations_CheckWhatPythonWouldNot() {
    var text = Python("""
      using System.Collections.Generic;
      public static class Values {
        public struct Pair { public int A; public int B; }
        public static int Lists(int i) {
          var list = new List<int> { 1, 2 };
          list.Insert(0, 7); list.RemoveAt(1); list[0] = 3;
          var floats = new List<double> { double.NaN };
          return list[i] + list.IndexOf(3) + floats.IndexOf(double.NaN) + (floats.Contains(double.NaN) ? 1 : 0);
        }
        public static int Maps(string key) {
          var map = new Dictionary<string, int>();
          map.Add(key, 1);
          map.TryAdd(key, 2);
          map[key] = 3;
          var found = map.ContainsKey(key) ? map[key] : -1;
          map.Remove(key);
          return found + map.Count;
        }
        public static int Sets(int v) {
          var set = new HashSet<int> { 1, 2 };
          set.Add(v); set.Remove(1);
          var total = 0;
          foreach (var item in set) total = total * 10 + item;
          return total;
        }
        public static int Pairs() {
          var items = new List<Pair> { new Pair { A = 1 } };
          var copies = items.ToArray();
          var total = 0;
          foreach (var pair in items) { var p = pair; p.A = 5; total += p.A; }
          return total + copies[0].A;
        }
      }
      """);
    Assert.That(text, Does.Contain("_ply_list_insert(list_, 0, 7)").And.Contain("_ply_list_remove_at(list_, 1)").And.Contain("_ply_list_set(list_, 0, 3)"));
    Assert.That(text, Does.Contain("_ply_index_of_float(floats, math.nan)").And.Contain("_ply_float_key(").Or.Contain("_ply_index_of_float("));
    Assert.That(text, Does.Contain("_ply_map_add(map_, key, 1)").And.Contain("_ply_map_try_add(map_, key, 2)").And.Contain("_ply_map_remove(map_, key)"));
    Assert.That(text, Does.Contain("_PlySlots()").And.Contain("class _PlySlots(dict):"));
    Assert.That(text, Does.Contain("list(map(copy.copy, items))"));
  }

  [Test]
  public void LoopsThatChangeTheirCollection_TrapAsDotNetDoes() {
    var text = Python("""
      using System.Collections.Generic;
      public static class Values {
        public static int Grow() { var list = new List<int> { 1, 2 }; var n = 0; foreach (var x in list) { n++; list.Add(x); } return n; }
        public static int Keys() {
          var map = new Dictionary<int, int> { [1] = 1, [2] = 2 };
          var n = 0;
          foreach (var entry in map) { n += entry.Value; map.Remove(entry.Key); }
          return n;
        }
      }
      """);
    Assert.That(text, Does.Contain("_PlyList(").And.Contain("_ply_each_checked(list_)"));
    Assert.That(text, Does.Contain("_ply_each_key("));
  }

  [Test]
  public void JavaScriptSearches_FromAPosition_CheckIt() {
    var text = Python("""
      /** @param {string} s @returns {number} */
      export function last(s) {
        return s.lastIndexOf("b", 2) + s.indexOf("b", 1);
      }
      """, "last.js");
    Assert.That(text, Does.Contain("_ply_last_index_of(s, \"b\", ").And.Contain("_ply_index_of(s, \"b\", "));
  }

  [Test]
  public void JavaScriptLoops_VisitWhatIsAddedDuringTheWalk() {
    var text = Python("""
      /** @returns {number} */
      export function walk() {
        const map = new Map();
        map.set(1, 1);
        let n = 0;
        for (const [key, value] of map) {
          n = n + value;
          if (key < 3) map.set(key + 1, key + 1);
        }
        return n;
      }
      """, "walk.js");
    Assert.That(text, Does.Contain("_ply_each_live_key("));
  }

  [Test]
  public void StepsAndAssignments_InsideExpressions() {
    var text = Python("""
      public sealed class Counter { public int Count; }
      public static class Values {
        public static int Captured() {
          var n = 0;
          System.Func<int> f = () => n;
          var fs = new System.Func<int>[2];
          for (var i = 0; i < 2; i++) { var k = i; fs[i] = () => k++; }
          n++;
          return f() + fs[0]() + fs[1]();
        }
        public static bool Not(Counter o) => !(o != null) && !(o is null);
      }
      """);
    Assert.That(text, Does.Contain("_ply_put(k, ").And.Contain("o is None and o is not None"));
  }

  [Test]
  public void Accessors_AreProperties_AndStaticsClassAttributes() {
    var text = Python("""
      public interface IArea { double Area { get; } }
      public sealed class Square : IArea {
        private double _side;
        public static int Made;
        public Square(double side) { _side = side; Made++; }
        public double Area => _side * _side;
        public double Side { get => _side; set => _side = value; }

      }
      public static class Values {
        public static double Use(double s) {
          IArea area = new Square(s);
          var square = new Square(1);
          square.Side = s;
          System.Func<double, double> grow = v => v + Square.Made;
          return area.Area + square.Side + grow(1);
        }
      }
      """);
    Assert.That(text, Does.Contain("    @property\n    def area(self) -> float:").And.Contain("    @side.setter\n"));
    Assert.That(text, Does.Contain("Square.made = ").And.Contain("square.side = s"));
  }

  [Test]
  public void SeveralConstructors_AreChosenByTheNumberOfArguments_AndOfOneNumberRefused() {
    var text = Python("""
      public sealed class Span2 {
        public int Low; public int High;
        public Span2() : this(0, 0) { }
        public Span2(int both) : this(both, both) { }
        public Span2(int low, int high) { Low = low; High = high; }
      }
      public static class Values { public static int Width(int a, int b) => new Span2(a, b).High - new Span2(a).Low + new Span2().High; }
      """);
    Assert.That(text, Does.Contain("def __init__(self, *args: object) -> None:").And.Contain("self.__init3(").And.Contain("def __init1(self) -> None:"));
    var refused = PythonFixtures.Translate("""
      public sealed class Two { public int V; public Two(int a) { V = a; } public Two(double b) { V = 1; } }
      public static class Values { public static int F() => new Two(1).V + new Two(1.5).V; }
      """, "Two.cs");
    Assert.That(refused.Problems, Has.Some.Contains("'constructors of one arity' of the class 'Two'"));
  }

  [Test]
  public void NestedMutableStruct_IsCopiedWithTheStructsItHolds() {
    var text = Python("""
      public static class Values {
        public struct Inner { public int X; }
        public struct Outer { public Inner In; public int Y; }
        public static int Copy() { var a = new Outer(); var b = a; b.In.X = 5; b.Y = 1; return a.In.X * 10 + b.In.X; }
      }
      """);
    Assert.That(text, Does.Contain("def __copy__(self) -> ").And.Contain("copy.copy(self.in_)"));
  }

}
