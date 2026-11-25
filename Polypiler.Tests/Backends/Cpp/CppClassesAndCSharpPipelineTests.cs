using System;
using NUnit.Framework;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Cpp.CppTestHarness;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>
/// Live g++ tests for classes, inheritance, virtual/override/abstract/sealed methods, interfaces,
/// <c>using</c>/<c>IDisposable</c>, record classes, and end-to-end <see cref="Polypiler.Core.Frontends.CSharp.CSharpFrontend.Import"/>
/// pipelines across C++17, C++20, and C++23.
/// </summary>
[TestFixture]
public sealed class CppClassesAndCSharpPipelineTests {

  [Test]
  public void ClassesInheritanceInterfacesAndUsing_ParityAcrossAllStandards() {
    var source = """
      using System;

      public interface IShape {
        int Area();
      }

      public interface IColoredShape : IShape {
        int Color();
      }

      public abstract class ShapeBase : IShape {
        public int Scale;
        public static int CreatedCount = 0;

        protected ShapeBase(int scale) {
          this.Scale = scale;
          CreatedCount += 1;
        }

        public abstract int Area();

        public virtual int Perimeter() => this.Scale * 4;

        public ShapeBase Self() => this;
      }

      public class Rect : ShapeBase, IColoredShape, IDisposable {
        public int W;
        public int H;
        public bool Disposed;

        public int Width {
          get { var v = this.W; return v; }
          set { this.W = value; }
        }

        public Rect(int w, int h) : base(2) {
          var initW = w;
          this.W = initW;
          this.H = h;
        }

        public Rect(int side) : this(side, side) {
        }

        public sealed override int Area() => this.W * this.H * this.Scale;

        public override int Perimeter() => base.Perimeter() + 2 * (this.W + this.H);

        public int DoublePerimeter() => this.Perimeter() * 2;

        public int Color() => 7;

        public void Dispose() {
          this.Disposed = true;
        }

        public Rect SelfRect() => this;

        public static int Combine(int a, int b) => a + b;
      }

      public record class Pair(int First, double Second);
      public record class EmptyRec();

      public static class Runner {
        public static int RunAll() {
          var r = new Rect(3, 4);
          r.W = 4;
          r.Width = 3;
          var sq = new Rect(5);
          IShape s = r;
          IColoredShape cs = r;
          ShapeBase b = r;
          var isRect = b is Rect;
          var asRect = b as Rect;
          var castRect = (Rect)b;
          var sameRef = object.ReferenceEquals(r, r.SelfRect()) && object.ReferenceEquals(b, b.Self());
          var p1 = new Pair(1, 2.5);
          var p2 = new Pair(1, 2.5);
          var e1 = new EmptyRec();
          var e2 = new EmptyRec();
          var eqRec = (p1 == p2) && (e1 == e2);

          int disposedFlag = 0;
          Rect? holder = null;
          using (var res = new Rect(2, 3)) {
            holder = res;
          }
          if (holder != null && holder.Disposed)
            disposedFlag = 1;

          return s.Area() + sq.Area() + cs.Color() + r.DoublePerimeter() + r.Width + Rect.Combine(1, 2)
            + (isRect ? 10 : 0) + (asRect != null ? 20 : 0) + (castRect != null ? 30 : 0)
            + (sameRef ? 40 : 0) + (eqRec ? 50 : 0) + disposedFlag + ShapeBase.CreatedCount;
        }

        public static int BadCastThrows() {
          ShapeBase b = new Rect(1, 1);
          Rect? n = null;
          if (n != null)
            return n.Area();
          return ((Rect)b).Area();
        }
      }
      """;

    var mod = ImportCSharp(source);
    var expected = (int)Eval(mod, "RunAll")!;

    var driver = $$"""
      #include "runner.hpp"
      #include <cassert>
      #include <memory>
      using namespace runner;
      int main() {
        assert(run_all() == {{expected}});
        assert(bad_cast_throws() == 2);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void CSharpFrontendPipeline_Adler32AndAlgorithms_ParityAcrossAllStandards() {
    var source = """
      public static class Checksums {
        public static uint Adler32(byte[] data) {
          uint a = 1;
          uint b = 0;
          for (var i = 0; i < data.Length; ++i) {
            a = (a + data[i]) % 65521u;
            b = (b + a) % 65521u;
          }
          return (b << 16) | a;
        }

        public static int BinarySearch(int[] sorted, int target) {
          var lo = 0;
          var hi = sorted.Length - 1;
          while (lo <= hi) {
            var mid = lo + ((hi - lo) >> 1);
            if (sorted[mid] == target)
              return mid;
            if (sorted[mid] < target)
              lo = mid + 1;
            else
              hi = mid - 1;
          }
          return -1;
        }

        public static void BubbleSort(int[] values) {
          for (var i = 0; i < values.Length; ++i) {
            for (var j = 0; j + 1 < values.Length - i; ++j) {
              if (values[j] > values[j + 1]) {
                var t = values[j];
                values[j] = values[j + 1];
                values[j + 1] = t;
              }
            }
          }
        }
      }
      """;

    var mod = ImportCSharp(source);
    var bytes = new byte[] { (byte)'W', (byte)'i', (byte)'k', (byte)'i', (byte)'p', (byte)'e', (byte)'d', (byte)'i', (byte)'a' };
    var expectedAdler = (uint)Eval(mod, "Adler32", bytes)!;
    Assert.That(expectedAdler, Is.EqualTo(0x11E60398u));

    var driver = $$"""
      #include "checksums.hpp"
      #include <cassert>
      #include <cstdint>
      #include <vector>
      using namespace checksums;
      int main() {
        std::vector<std::uint8_t> data{'W','i','k','i','p','e','d','i','a'};
        assert(adler32(data) == {{expectedAdler}}u);
        std::vector<std::int32_t> arr{5, 1, 4, 2, 8};
        bubble_sort(arr);
        assert(arr == (std::vector<std::int32_t>{1, 2, 4, 5, 8}));
        assert(binary_search(arr, 4) == 2);
        assert(binary_search(arr, 9) == -1);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

}
