using NUnit.Framework;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The rarer forms of the object model, the closures and the handlers: base calls from inside a
/// closure, properties without a getter, classes without a constructor, writes into the field of an
/// object an expression gives, structs made mutable by what they hold, cells for parameters, catch
/// variables, loop elements and keys a closure captures, and the radix of an unsigned value.
/// </summary>
[TestFixture]
public sealed class ObjectEmissionTests {

  private static string Python(string source, string file = "Objects.cs") {
    var translated = PythonFixtures.Translate(source, file);
    Assert.That(translated.Source, Is.Not.Null, string.Join("; ", translated.Problems));
    return translated.Source!;
  }

  [Test]
  public void BaseMembers_AreReachedThroughSuper() {
    var text = Python("""
      public class Animal { public virtual int Legs() => 2; }
      public sealed class Dog : Animal { public override int Legs() => base.Legs() + 2; }
      public static class Objects { public static int Count() => new Dog().Legs(); }
      """);
    Assert.That(text, Does.Contain("super().legs()"));
  }

  [Test]
  public void PropertyWithoutAGetter_StillHasOne() {
    var text = Python("""
      public sealed class Sink { public int Total; public int Add { set { Total += value; } } }
      public static class Objects { public static int Fill() { var s = new Sink(); s.Add = 3; s.Add = 4; return s.Total; } }
      """);
    Assert.That(text, Does.Contain("raise AttributeError").And.Contain("@add.setter"));
  }

  [Test]
  public void JavaScriptClassWithoutAConstructor_HasNoneOfItsOwn() {
    var text = Python("""
      class Base { /** @returns {number} */ size() { return 1; } }
      class Derived extends Base { /** @returns {number} */ size() { return 2; } }
      /** @returns {number} */
      export function total() { return new Base().size() + new Derived().size(); }
      """, "classes.js");
    Assert.That(text, Does.Contain("class Derived(Base):"));
  }

  [Test]
  public void FieldWrite_IntoAComputedObject_ComputesTheObjectFirst() {
    var text = Python("""
      public sealed class Box { public int Value; }
      public static class Objects {
        private static int Pick() => 0;
        private static int Compute() => 5;
        public static int Write() { var boxes = new System.Collections.Generic.List<Box> { new Box() }; boxes[Pick()].Value = Compute(); return boxes[0].Value; }
      }
      """);
    Assert.That(text, Does.Contain("_t1 = _ply_list_get(boxes, _pick())\n    _t1.value = _compute()"));
  }

  [Test]
  public void Structs_HoldingArraysOrMutableStructs_AreMutable() {
    var text = Python("""
      public static class Objects {
        public struct Buffer { public int[] Data; public Buffer(int[] data) { Data = data; } }
        public struct Inner { public int X; }
        public struct Holder { public Inner In; }
        public static int Use() {
          var b = new Buffer(new int[2]);
          var c = b;
          var i = new Inner(); i.X = 3;
          var h = new Holder { In = i };
          var k = h;
          return c.Data.Length + k.In.X;
        }
      }
      """);
    Assert.That(text, Does.Not.Contain("frozen=True)\nclass Buffer").And.Not.Contain("frozen=True)\nclass Holder"));
  }

  [Test]
  public void AddressOfAParameter_AndAnUninitializedLocal_AreCells() {
    var text = Python("""
      public static unsafe class Objects {
        public static int Bump(int x) { int* p = &x; *p += 1; return x; }
        public static int Late() { int y; y = 4; int* p = &y; *p += 1; return y; }
      }
      """);
    Assert.That(text, Does.Contain("    x = [x]\n").And.Contain("_PlyPointer(y, 0, 1, \"i\", 0)"));
  }

  [Test]
  public void CapturedLoopElements_AreCellsMadeAnewEveryPass() {
    var text = Python("""
      using System.Collections.Generic;
      public static class Objects {
        private static IEnumerable<int> Up(int n) { for (var i = 0; i < n; i++) yield return i; }
        public static int Sequence() {
          var fs = new List<System.Func<int>>();
          foreach (var v in Up(3)) fs.Add(() => v);
          var s = 0;
          foreach (var f in fs) s = s * 10 + f();
          return s;
        }
        public static int Keys() {
          var map = new Dictionary<int, int> { [1] = 10, [2] = 20 };
          var fs = new List<System.Func<int>>();
          foreach (var pair in map) fs.Add(() => pair.Key + pair.Value);
          return fs[0]() + fs[1]();
        }
      }
      """);
    Assert.That(text, Does.Contain("v = [_item]").And.Contain(" = [_key]"));
  }

  [Test]
  public void CaughtExceptions_BindTheirVariables_InFiltersToo() {
    var text = Python("""
      public static class Objects {
        public static int Pick(int x) {
          try {
            if (x > 0) throw new System.InvalidOperationException("p");
            throw new System.FormatException("f");
          } catch (System.InvalidOperationException first) when (x > 5) {
            return first.Message.Length;
          } catch (System.Exception second) when (x < 0) {
            return second.Message.Length + 10;
          } catch (System.Exception) {
            return -1;
          }
        }
      }
      """);
    Assert.That(text, Does.Contain("except Exception as first:").And.Contain("(second := first) is not None and x < 0"));
  }

  [Test]
  public void ArgumentException_IsAValueErrorThatIsNoFormatError() {
    var text = Python("""
      public static class Objects {
        public static int Catch(int x) {
          try {
            if (x > 0) throw new System.ArgumentException("a");
            throw new System.FormatException("f");
          } catch (System.ArgumentException) {
            return 1;
          } catch (System.FormatException) {
            return 2;
          }
        }
      }
      """);
    Assert.That(text, Does.Contain("isinstance(_error, ValueError) and not isinstance(_error, FormatError)"));
  }

  [Test]
  public void HexadecimalOfAnUnsignedValue_IsFormat() {
    var text = Python("public static class Objects { public static string Hex(uint v) => v.ToString(\"x\"); public static string Wide(int v) => v.ToString(\"X8\"); }");
    Assert.That(text, Does.Contain("format(v, \"x\")").And.Contain("_ply_radix(v, 32, 16, 8, True, True)"));
  }

}
