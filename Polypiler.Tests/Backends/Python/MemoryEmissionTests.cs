using NUnit.Framework;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The forms of #446 in the generated text: arrays as lists with the index checked where Python would
/// count from the end, element loops as <c>for x in xs</c>, endian accesses as <c>int.from_bytes</c>,
/// <c>int.to_bytes</c> and <c>struct</c> after a range check that a slice does not make, overlap-safe
/// copies through a slice, views as <c>_PlyView</c> and pointers as a region and an offset, a local
/// whose address is taken as a cell. CPython runs them in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class MemoryEmissionTests {

  private static string Python(string source) {
    var translated = PythonFixtures.Translate(source, "Memory.cs");
    Assert.That(translated.Source, Is.Not.Null, string.Join("; ", translated.Problems));
    return translated.Source!;
  }

  [Test]
  public void ElementLoop_IsAForOverTheList() {
    var text = Python("public static class Memory { public static int Total(int[] xs) { var s = 0; foreach (var x in xs) s += x; return s; } }");
    Assert.That(text, Does.Contain("def total(xs: list[int]) -> int:").And.Contain("in range(len(xs)):").Or.Contain("for x in xs:"));
  }

  [Test]
  public void PossiblyNegativeIndex_IsCheckedFirst() {
    var text = Python("public static class Memory { public static int At(int[] xs, int i) => xs[i]; public static int First(int[] xs) => xs[0]; }");
    Assert.That(text, Does.Contain("return xs[_ply_index(i)]").And.Contain("return xs[0]"));
  }

  [Test]
  public void EndianLoadsAndStores_AreIntFromBytesAndStruct() {
    var text = Python("""
      public static class Memory {
        public static uint Little(byte[] b) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b);
        public static int Big(byte[] b) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b);
        public static double Real(byte[] b) => System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(b);
        public static void Put(byte[] b, ushort v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(b, v);
      }
      """);
    Assert.That(text, Does.Contain("int.from_bytes(_ply_bytes(").And.Contain("\"little\")").And.Contain("signed=True"));
    Assert.That(text, Does.Contain("struct.unpack(\"<d\", _ply_bytes(").And.Contain(".to_bytes(2, \"big\")"));
  }

  [Test]
  public void Copy_GoesThroughASlice_AndFillAndClearStoreEveryElement() {
    var text = Python("""
      public static class Memory {
        public static int[] Move(int[] xs) { System.Array.Copy(xs, 0, xs, 1, 4); return xs; }
        public static int[] Filled(int n) { var a = new int[n]; System.Array.Fill(a, 7); return a; }
        public static int[] Cleared(int[] a) { System.Array.Clear(a); return a; }
      }
      """);
    Assert.That(text, Does.Contain("target[target_index:target_index + length] = source[source_index:source_index + length]"));
    Assert.That(text, Does.Contain("_ply_fill(a, 7)").And.Contain("_ply_fill(a, 0)"));
  }

  [Test]
  public void View_IsAPlyView_AndAPointerARegionAndAnOffset() {
    var text = Python("""
      public static unsafe class Memory {
        public static int Sum(byte[] d, int start, int length) { System.ReadOnlySpan<byte> s = System.MemoryExtensions.AsSpan(d, start, length); var t = 0; foreach (var b in s) t += b; return t; }
        public static uint Hash(byte[] data) { uint h = 0; fixed (byte* p = data) { for (var q = p; q < p + data.Length; q++) h = h * 31 + *q; } return h; }
        public static int Local() { int x = 6; int* p = &x; *p += 1; return x; }
      }
      """);
    Assert.That(text, Does.Contain("class _PlyView:").And.Contain("_ply_view_of(d, start, length)"));
    Assert.That(text, Does.Contain("_ply_pin(data, \"B\")").And.Contain("_ply_pointer_load("));
    Assert.That(text, Does.Contain("x = [6]").And.Contain("_PlyPointer(x, 0, 1, \"i\", 0)").And.Contain("return x[0]"));
  }

}
