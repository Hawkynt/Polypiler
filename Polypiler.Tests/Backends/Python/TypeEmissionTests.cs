using NUnit.Framework;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The forms of #448 in the generated text: classes with their constructors, methods, properties and
/// statics, structs as dataclasses - frozen where nothing writes a field, copied where the IR copies a
/// value - records with the IR's equality, enums as <c>enum.IntEnum</c>, optional values tested with
/// <c>is None</c>, exceptions as Python classes with <c>try</c>/<c>except</c>/<c>finally</c>, closures
/// as nested functions, and iterators as generators. CPython runs them in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class TypeEmissionTests {

  private static string Python(string source) {
    var translated = PythonFixtures.Translate(source, "Types.cs");
    Assert.That(translated.Source, Is.Not.Null, string.Join("; ", translated.Problems));
    return translated.Source!;
  }

  [Test]
  public void ReferenceRecordWithAnAccessor_IsAClassWithAMethod() {
    var text = Python("""
      public sealed class Plane {
        public int Width; public int Height; public int[] Pixels;
        public Plane(int width, int height, int[] pixels) { Width = width; Height = height; Pixels = pixels; }
        public int Get(int x, int y) => Pixels[y * Width + x];
      }
      public static class Types { public static int At(int x, int y) => new Plane(3, 2, new[] { 10, 20, 30, 40, 50, 60 }).Get(x, y); }
      """);
    Assert.That(text, Does.Contain("class Plane:\n    def __init__(self, width: int, height: int, pixels: list[int]) -> None:\n"));
    Assert.That(text, Does.Contain("    def get(self, x: int, y: int) -> int:\n").And.Contain("Plane(3, 2, [10, 20, 30, 40, 50, 60]).get(x, y)"));
  }

  [Test]
  public void StateClass_KeepsItsFieldsOnTheInstance() {
    var text = Python("""
      public sealed class Xorshift32 {
        private uint _state;
        public Xorshift32(uint seed) { _state = seed; }
        public uint Next() { var x = _state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _state = x; return x; }
      }
      public static class Types { public static uint Third() { var r = new Xorshift32(1); r.Next(); r.Next(); return r.Next(); } }
      """);
    Assert.That(text, Does.Contain("self._state = seed").And.Contain("    def next(self) -> int:\n").And.Contain("return r.next()"));
  }

  [Test]
  public void Struct_IsAFrozenDataclassUnlessAFieldIsWritten() {
    var frozen = Python("""
      public static class Types {
        public readonly record struct P(int X, int Y);
        public static bool Same(int v) => new P(v, 2) == new P(1, 2);
      }
      """);
    Assert.That(frozen, Does.Contain("@dataclass(eq=False, frozen=True)\n").And.Contain("def __eq__(self, other: object) -> bool:"));
    var mutable = Python("""
      public static class Types {
        public struct V { public double X; public double Y; public V(double x, double y) { X = x; Y = y; } }
        public static double Copy() { var p = new V(1, 2); var q = p; q.X = 5; return p.X * 10 + q.X; }
      }
      """);
    Assert.That(mutable, Does.Contain("@dataclass\n").And.Contain("copy.copy(p)").And.Contain("import copy"));
  }

  [Test]
  public void Enum_IsAnIntEnum_AndItsSwitchAMatchOnItsMembers() {
    var text = Python("""
      public static class Types {
        public enum Method { Yadif = 0, Bwdif = 1, GreedyHm = 2 }
        public static int Shader(int m) { switch ((Method)m) { case Method.Yadif: return 0; case Method.Bwdif: return 1; default: return 2; } }
        public static string Name(int m) => ((Method)m).ToString();
      }
      """);
    Assert.That(text, Does.Contain("(enum.IntEnum):\n    YADIF = 0\n    BWDIF = 1\n    GREEDY_HM = 2\n").And.Contain("import enum"));
    Assert.That(text, Does.Contain("_ply_enum_format("));
  }

  [Test]
  public void Optional_IsTestedWithIsNone() {
    var text = Python("public static class Types { public static int F(int? x) => x is null ? -1 : x.Value; public static int G(int? x) => x ?? 7; }");
    Assert.That(text, Does.Contain("def f(x: int | None) -> int:").And.Contain("x is None"));
    Assert.That(text, Does.Contain("x if x is not None else 7"));
  }

  [Test]
  public void Traps_AreRaisedAsTheirClasses_AndHandlersCatchThem() {
    var text = Python("""
      public static class Types {
        public static int F(int x, int y) {
          try {
            if (x < 0) throw new System.ArgumentException("negative");
            return x / y;
          } catch (System.DivideByZeroException) {
            return -1;
          } finally {
            x = 0;
          }
        }
      }
      """);
    Assert.That(text, Does.Contain("raise ValueError(\"negative\")").And.Contain("except ZeroDivisionError:").And.Contain("finally:"));
  }

  [Test]
  public void ExceptionClass_DerivesFromThePythonClassOfItsBase() {
    var text = Python("""
      public sealed class Bad : System.InvalidOperationException { public int Code; public Bad(string m, int code) : base(m) { Code = code; } }
      public static class Types {
        public static int F(int c) { var b = new Bad("x", c); try { throw b; } catch (Bad e) when (c > 2) { return e.Code; } catch (System.Exception e) { return e.Message.Length; } }
      }
      """);
    Assert.That(text, Does.Contain("class Bad(RuntimeError):").And.Contain("super().__init__(m)"));
    Assert.That(text, Does.Contain("except Exception as e:").And.Contain("isinstance(e, Bad)").And.Contain("_ply_message("));
  }

  [Test]
  public void Interface_AndVirtualDispatch_AreClassesWithMethods() {
    var text = Python("""
      public interface IShape { double Area(); }
      public sealed class Square : IShape { public double Side; public Square(double s) { Side = s; } public double Area() => Side * Side; }
      public class Animal { public virtual int Legs() => 0; public int Twice() => Legs() * 2; }
      public sealed class Dog : Animal { public override int Legs() => 4 + base.Legs(); }
      public static class Types {
        public static double Area(double s) { IShape shape = new Square(s); return shape.Area(); }
        public static int Legs() => new Dog().Twice();
        public static bool Is(int k) { Animal o = new Animal(); if (k > 0) o = new Dog(); return o is Dog; }
      }
      """);
    Assert.That(text, Does.Contain("class IShape:\n    def area(self) -> float:\n        raise NotImplementedError\n"));
    Assert.That(text, Does.Contain("class Square(IShape):").And.Contain("class Dog(Animal):").And.Contain("super().legs()"));
    Assert.That(text, Does.Contain("isinstance("));
  }

  [Test]
  public void Closures_AreNestedFunctions_ThatBindAVariableOfEveryPass() {
    var text = Python("""
      public static class Types {
        public static int Counter() { var n = 0; System.Func<int> next = () => ++n; next(); return next(); }
        public static int Passes() {
          var fs = new System.Func<int>[3];
          for (var t = 0; t < 3; t++) { var copy = t * 10; fs[t] = () => copy; }
          return fs[0]() + fs[1]() + fs[2]();
        }
        // A choice of two lambdas keeps the function value: one known lambda would be specialised away (O0463).
        public static int Twice(int x) { System.Func<int, int> f = x > 0 ? v => v * 2 : v => v * 3; return f(x); }
      }
      """);
    Assert.That(text, Does.Contain("nonlocal n"));
    Assert.That(text, Does.Contain("copy_ = [").And.Contain("(copy_=copy_) -> int:"));
    Assert.That(text, Does.Contain("def f(v: int) -> int:").Or.Contain("lambda v:"));
  }

  [Test]
  public void Iterator_IsAGenerator_RestartedOnEveryEnumeration() {
    var text = Python("""
      public static class Types {
        private static System.Collections.Generic.IEnumerable<int> Up(int n) { for (var i = 1; i <= n; i++) yield return i; }
        public static int Sum(int n) { var s = 0; foreach (var v in Up(n)) s += v; foreach (var v in Up(n)) s += v; return s; }
      }
      """);
    Assert.That(text, Does.Contain("yield i").And.Contain("return _PlySequence(_make, n)").And.Contain("class _PlySequence:"));
  }

  [Test]
  public void Collections_AreListsAndDicts() {
    var text = Python("""
      public static class Types {
        public static int Count(string[] words) {
          var counts = new System.Collections.Generic.Dictionary<string, int>();
          foreach (var w in words) { if (counts.ContainsKey(w)) counts[w] += 1; else counts[w] = 1; }
          var list = new System.Collections.Generic.List<int>();
          foreach (var pair in counts) list.Add(pair.Value);
          return list.Count;
        }
      }
      """);
    Assert.That(text, Does.Contain("counts: dict[str, int] = {}").And.Contain("w in counts").And.Contain(".append("));
  }

  [Test]
  public void Strings_AreStrOfCodeUnits() {
    var text = Python("""
      public static class Types {
        public static int Unit(string s, int i) => s[i];
        public static string Join(string a, string b) => a + "-" + b.Substring(1, 2) + b.Length;
        public static bool Has(string s) => s.Contains("x") && s.StartsWith("a", System.StringComparison.Ordinal) && s.IndexOf('b') >= 0;
      }
      """);
    Assert.That(text, Does.Contain("_ply_unit_at(s, i)").And.Contain("_ply_substring(b, 1, 2)"));
    Assert.That(text, Does.Contain("\"x\" in s").And.Contain("s.startswith(\"a\")").And.Contain("s.find(chr(98))").Or.Contain("s.find(\"b\")"));
  }

}
