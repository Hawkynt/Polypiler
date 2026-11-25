using System;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Jvm;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Jvm;

[TestFixture]
public sealed class JvmArraysExceptionsAndJarTests {

  [Test]
  public void PrimitiveAndStringArrays_NullChecksAndLengths_WorkAndEmitPython() {
    var bytes = JvmTestHarness.CompileClass("ArrayOps", """
      public class ArrayOps {
        public static int allPrimitives(int n) {
          boolean[] zb = new boolean[n];
          byte[] bb = new byte[n];
          short[] sb = new short[n];
          char[] cb = new char[n];
          int[] ib = new int[n];
          long[] lb = new long[n];
          float[] fb = new float[n];
          double[] db = new double[n];
          zb[0] = true;
          bb[0] = -5;
          sb[0] = 300;
          cb[0] = 'A';
          ib[0] = 1000;
          lb[0] = 2000L;
          fb[0] = 3.0f;
          db[0] = 4.0;
          return (zb[0] ? 1 : 0) + bb[0] + sb[0] + cb[0] + ib[0] + (int)lb[0] + (int)fb[0] + (int)db[0] + ib.length;
        }
        public static int stringArray() {
          String[] arr = new String[] { "hello", "world" };
          return arr[0].length() + arr[1].length() + (arr[0].equals("hello") ? 1 : 0) + (arr[1].isEmpty() ? 10 : 0) + arr[0].charAt(0);
        }
        public static int nullChecks(int[] a, String s) {
          int r = 0;
          if (a == null) r += 1;
          if (s != null) r += 2;
          String empty = null;
          if (empty == s) r += 4;
          if (s != empty) r += 8;
          return r;
        }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("ArrayOps.class", bytes);
    Assert.That(IrInterpreter.Call(mod, "allPrimitives", [3]), Is.EqualTo(1 - 5 + 300 + 65 + 1000 + 2000 + 3 + 4 + 3));
    Assert.That(IrInterpreter.Call(mod, "stringArray", []), Is.EqualTo(5 + 5 + 1 + 0 + 'h'));
    Assert.That(IrInterpreter.Call(mod, "nullChecks", [null!, "abc"]), Is.EqualTo(1 + 2 + 8));
    Assert.That(IrInterpreter.Call(mod, "nullChecks", [new int[1], null!]), Is.EqualTo(4));

    JvmTestHarness.EmitPython(mod);
  }

  [Test]
  public void StaticFieldsAndClinit_PreserveStateAndEmitPython() {
    var bytes = JvmTestHarness.CompileClass("StaticState", """
      public class StaticState {
        public static final boolean CONST_BOOL = true;
        public static final int CONST_INT = 42;
        public static final long CONST_LONG = 10000000000L;
        public static final float CONST_FLOAT = 2.5f;
        public static final double CONST_DOUBLE = 3.5;
        public static final String CONST_STR = "polypiler";
        private static String mutableStr = "init";
        private static int counter = 10;
        private static int[] table = new int[] { 2, 4, 6, 8 };
        private static long[] longs = new long[] { 0L, 1L, 10000000000L };
        private static float[] floats = new float[] { 0.0f, 1.0f, 2.5f };
        private static double[] doubles = new double[] { 0.0, 1.0, 3.5 };
        private static byte[] bytesArr = new byte[4];
        private static int[] emptyArr;
        private static String[] emptyStrArr;
        private static String[] names = new String[] { "a", "bc" };
        private static int initHelper(int x) { return x + 5; }
        static {
          counter = initHelper(counter);
          mutableStr = "ready";
        }
        public static int nextCount() {
          counter++;
          return counter;
        }
        public static int readConsts() {
          return (CONST_BOOL ? 1 : 0) + CONST_INT + (int)(CONST_LONG / 1000000000L) + (int)(CONST_FLOAT + CONST_DOUBLE) + CONST_STR.length() + mutableStr.length();
        }
        public static int tableSum() {
          int s = 0;
          for (int i = 0; i < table.length; i++) s += table[i];
          return s + (int)longs[1] + (int)floats[2] + (int)doubles[2] + bytesArr.length + names[1].length() + emptyArr.length + emptyStrArr.length;
        }
        public static int overloaded(int x) { return x + 1; }
        public static long overloaded(long x) { return x + 2L; }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("StaticState.class", bytes);
    var inst = IrInterpreter.Instantiate(mod);
    Assert.That(inst.Call("nextCount"), Is.EqualTo(16));
    Assert.That(inst.Call("nextCount"), Is.EqualTo(17));
    Assert.That(inst.Call("readConsts"), Is.EqualTo(1 + 42 + 10 + 6 + 9 + 5));
    Assert.That(inst.Call("tableSum"), Is.EqualTo(20 + 1 + 2 + 3 + 4 + 2));
    Assert.That(inst.Call("overloaded_i32", 10), Is.EqualTo(11));
    Assert.That(inst.Call("overloaded_i64", 10L), Is.EqualTo(12L));

    JvmTestHarness.EmitPython(mod);
  }

  [Test]
  public void TryCatchFinallyAndThrow_WorkAndEmitPython() {
    var bytes = JvmTestHarness.CompileClass("ExOps", """
      public class ExOps {
        public static int safeDiv(int a, int b) {
          try {
            return a / b;
          } catch (ArithmeticException e) {
            return -999;
          }
        }
        public static int checkPositive(int x) {
          if (x < 0) throw new IllegalArgumentException("negative");
          return x * 2;
        }
        public static String catchMessage(int x) {
          try {
            if (x < 0) throw new IllegalArgumentException("bad-input");
            return "ok";
          } catch (IllegalArgumentException e) {
            return e.getMessage();
          }
        }
        public static int multiCatch(int[] a, int i, int d) {
          int r = 0;
          try {
            r = a[i] / d;
          } catch (ArithmeticException e) {
            r = -1;
          } catch (ArrayIndexOutOfBoundsException e) {
            r = -2;
          } finally {
            r += 100;
          }
          return r;
        }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("ExOps.class", bytes);
    Assert.That(IrInterpreter.Call(mod, "safeDiv", [10, 2]), Is.EqualTo(5));
    Assert.That(IrInterpreter.Call(mod, "safeDiv", [10, 0]), Is.EqualTo(-999));
    Assert.That(IrInterpreter.Call(mod, "checkPositive", [7]), Is.EqualTo(14));
    Assert.That(() => IrInterpreter.Call(mod, "checkPositive", [-1]), Throws.InstanceOf<IrException>());
    Assert.That(IrInterpreter.Call(mod, "catchMessage", [-5]), Is.EqualTo("bad-input"));
    Assert.That(IrInterpreter.Call(mod, "catchMessage", [5]), Is.EqualTo("ok"));
    Assert.That(IrInterpreter.Call(mod, "multiCatch", [new[] { 20, 40 }, 1, 4]), Is.EqualTo(110));
    Assert.That(IrInterpreter.Call(mod, "multiCatch", [new[] { 20, 40 }, 1, 0]), Is.EqualTo(99));
    Assert.That(IrInterpreter.Call(mod, "multiCatch", [new[] { 20, 40 }, 9, 2]), Is.EqualTo(98));

    JvmTestHarness.EmitPython(mod);
  }

  [Test]
  public void JdkIntrinsics_MatchJavaSemantics_AndEmitPython() {
    var bytes = JvmTestHarness.CompileClass("JdkIntrinsics", """
      import java.util.Arrays;
      public class JdkIntrinsics {
        public static int mathInt(int a, int b) {
          return Math.abs(a) + Math.min(a, b) + Math.max(a, b) + Math.floorDiv(a, b) + Math.floorMod(a, b);
        }
        public static long mathLong(long a, long b, int c) {
          return Math.abs(a) + Math.min(a, b) + Math.max(a, b) + Math.floorDiv(a, b) + Math.floorMod(a, b)
            + Math.floorDiv(a, c) + (long)Math.floorMod(a, c);
        }
        public static float mathFloat(float a, float b) {
          return Math.abs(a) + Math.min(a, b) + Math.max(a, b) + Math.copySign(a, b);
        }
        public static double mathDouble(double a, double b) {
          return Math.abs(a) + Math.min(a, b) + Math.max(a, b) + Math.sqrt(Math.abs(a))
            + Math.floor(a) + Math.ceil(a) + Math.rint(a) + Math.copySign(a, b);
        }
        public static int integerBits(int x, int y, int d) {
          return Integer.rotateLeft(x, d) ^ Integer.rotateRight(x, d)
            ^ Integer.numberOfLeadingZeros(x) ^ Integer.numberOfTrailingZeros(x)
            ^ Integer.bitCount(x) ^ Integer.divideUnsigned(x, y)
            ^ Integer.remainderUnsigned(x, y) ^ Integer.compareUnsigned(x, y);
        }
        public static long longBits(long x, long y, int d) {
          return Long.rotateLeft(x, d) ^ Long.rotateRight(x, d)
            ^ (long)Long.numberOfLeadingZeros(x) ^ (long)Long.numberOfTrailingZeros(x)
            ^ (long)Long.bitCount(x) ^ Long.divideUnsigned(x, y)
            ^ Long.remainderUnsigned(x, y) ^ (long)Long.compareUnsigned(x, y);
        }
        public static int floatDoubleClassify(double d, float f) {
          int r = 0;
          if (Double.isNaN(d)) r |= 1;
          if (Double.isInfinite(d)) r |= 2;
          if (Double.isFinite(d)) r |= 4;
          if (Float.isNaN(f)) r |= 8;
          if (Float.isInfinite(f)) r |= 16;
          if (Float.isFinite(f)) r |= 32;
          return r;
        }
        public static double bitsRoundTripD(double d) {
          long raw = Double.doubleToRawLongBits(d);
          long canon = Double.doubleToLongBits(d);
          return Double.longBitsToDouble(raw) + Double.longBitsToDouble(canon);
        }
        public static float bitsRoundTripF(float f) {
          int raw = Float.floatToRawIntBits(f);
          int canon = Float.floatToIntBits(f);
          return Float.intBitsToFloat(raw) + Float.intBitsToFloat(canon);
        }
        public static int arraysOps() {
          int[] a = new int[5];
          Arrays.fill(a, 7);
          Arrays.fill(a, 1, 4, 3);
          int[] b = Arrays.copyOf(a, 7);
          System.arraycopy(b, 0, b, 1, 4);
          return b[0] + b[1] + b[2] + b[3] + b[4] + b[5] + b[6];
        }
        public static int arrayCopyStrings() {
          String[] src = new String[] { "a", "bb", "ccc" };
          String[] dst = new String[] { "", "", "" };
          System.arraycopy(src, 0, dst, 0, 3);
          return dst[0].length() + dst[1].length() + dst[2].length();
        }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("JdkIntrinsics.class", bytes);
    Assert.That(IrInterpreter.Call(mod, "mathInt", [-7, 3]), Is.EqualTo(7 + (-7) + 3 + (-3) + 2));
    Assert.That(IrInterpreter.Call(mod, "mathInt", [int.MinValue, -1]), Is.EqualTo(unchecked(int.MinValue + int.MinValue + (-1) + int.MinValue + 0)));
    Assert.That(IrInterpreter.Call(mod, "mathLong", [-7L, 3L, 3]), Is.EqualTo(7L + (-7L) + 3L + (-3L) + 2L + (-3L) + 2L));
    Assert.That((float)IrInterpreter.Call(mod, "mathFloat", [-4.0f, 2.0f])!, Is.EqualTo(4.0f + (-4.0f) + 2.0f + 4.0f).Within(1e-5f));
    Assert.That((double)IrInterpreter.Call(mod, "mathDouble", [-4.5, 2.0])!, Is.EqualTo(4.5 + (-4.5) + 2.0 + Math.Sqrt(4.5) + (-5.0) + (-4.0) + (-4.0) + 4.5).Within(1e-12));
    Assert.That(IrInterpreter.Call(mod, "integerBits", [0, 3, 5]), Is.EqualTo(0 ^ 0 ^ 32 ^ 32 ^ 0 ^ 0 ^ 0 ^ (-1)));
    Assert.That(IrInterpreter.Call(mod, "integerBits", [-1, 3, 4]), Is.EqualTo(
      unchecked((int)System.Numerics.BitOperations.RotateLeft(0xFFFFFFFFu, 4))
      ^ unchecked((int)System.Numerics.BitOperations.RotateRight(0xFFFFFFFFu, 4))
      ^ 0 ^ 0 ^ 32 ^ unchecked((int)(0xFFFFFFFFu / 3u)) ^ unchecked((int)(0xFFFFFFFFu % 3u)) ^ 1));
    Assert.That(IrInterpreter.Call(mod, "longBits", [0L, 3L, 5]), Is.EqualTo(0L ^ 0L ^ 64L ^ 64L ^ 0L ^ 0L ^ 0L ^ (-1L)));
    Assert.That(IrInterpreter.Call(mod, "longBits", [-1L, 3L, 4]), Is.EqualTo(
      unchecked((long)System.Numerics.BitOperations.RotateLeft(ulong.MaxValue, 4))
      ^ unchecked((long)System.Numerics.BitOperations.RotateRight(ulong.MaxValue, 4))
      ^ 0L ^ 0L ^ 64L ^ unchecked((long)(ulong.MaxValue / 3UL)) ^ unchecked((long)(ulong.MaxValue % 3UL)) ^ 1L));
    Assert.That(IrInterpreter.Call(mod, "floatDoubleClassify", [double.NaN, float.PositiveInfinity]), Is.EqualTo(1 | 16));
    Assert.That(IrInterpreter.Call(mod, "floatDoubleClassify", [double.NegativeInfinity, 1.5f]), Is.EqualTo(2 | 32));
    Assert.That(IrInterpreter.Call(mod, "floatDoubleClassify", [2.5, float.NaN]), Is.EqualTo(4 | 8));
    Assert.That((double)IrInterpreter.Call(mod, "bitsRoundTripD", [3.25])!, Is.EqualTo(6.5).Within(1e-12));
    Assert.That((float)IrInterpreter.Call(mod, "bitsRoundTripF", [1.5f])!, Is.EqualTo(3.0f).Within(1e-6f));
    // a = [7,3,3,3,7], b = [7,3,3,3,7,0,0], arraycopy(b,0,b,1,4) -> [7,7,3,3,3,0,0] -> sum = 23
    Assert.That(IrInterpreter.Call(mod, "arraysOps", []), Is.EqualTo(23));
    Assert.That(IrInterpreter.Call(mod, "arrayCopyStrings", []), Is.EqualTo(6));

    JvmTestHarness.EmitPython(mod);
  }

  [Test]
  public void MultiClassJar_WithCrossCallsClinitOverloadsAndExportFilter_WorksAndEmitsPython() {
    var compiled = JvmTestHarness.CompileClasses(
      ("pkg/Helper.java", """
        package pkg;
        public class Helper {
          public static int BASE = 10;
          static { BASE += 2; }
          private static int secret(int x) { return x * 2; }
          public static int twice(int x) { return secret(x) + BASE; }
          public static int calc(int x) { return x + 1; }
        }
        """),
      ("pkg/Main.java", """
        package pkg;
        public class Main {
          public static int EXTRA = 3;
          static { EXTRA *= 2; }
          public static int calc(int x) { return Helper.twice(x) + EXTRA; }
          public static int run(int x) { return calc(x) + Helper.calc(x); }
        }
        """));

    var jarBytes = JvmTestHarness.PackJar([
      ("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\n")),
      ("module-info.class", [0xCA, 0xFE, 0xBA, 0xBE]),
      .. compiled
    ]);

    var modAll = JvmTestHarness.ImportAndVerify("app.jar", jarBytes);
    Assert.That(IrInterpreter.Call(modAll, "run", [5]), Is.EqualTo((5 * 2 + 12 + 6) + (5 + 1)));
    var pyAll = JvmTestHarness.EmitPython(modAll);
    Assert.That(pyAll, Does.Contain("def run("));

    // Export filter selecting only pkg.Main.run
    var modFiltered = JvmTestHarness.ImportAndVerify("app.jar", jarBytes, new JvmImportOptions { Exports = ["pkg.Main.run"] });
    Assert.That(modFiltered.Functions.Where(f => f.IsPublic).Select(f => f.Name), Is.EqualTo(new[] { "run" }));
    Assert.That(IrInterpreter.Call(modFiltered, "run", [5]), Is.EqualTo(34));
    JvmTestHarness.EmitPython(modFiltered);
  }

  [Test]
  public void TypedStaticArraysConstantValuesAndSwitchLoopDispatcher_LiftAndVerify() {
    var bytes = JvmTestHarness.CompileClass("TypedStatics", """
      public class TypedStatics {
        public static final boolean FLAG_T = true;
        public static final boolean FLAG_F = false;
        public static final long BIG = 1234567890123L;
        public static final float PI_F = 3.5f;
        public static final double PI_D = 6.25;
        public static final String GREET = "hi";
        public static long[] LONGS = new long[] { 0L, 1L, 100000L };
        public static float[] FLOATS = new float[] { 0.0f, 1.0f, 2.5f };
        public static double[] DOUBLES = new double[] { 0.0, 1.0, 2.5 };
        public static String[] NAMES = new String[] { "a", "bc" };
        public static int[] EMPTY_NUM = new int[0];
        public static String[] EMPTY_STR = new String[0];
        public static int[] PARTIAL = new int[3];
        public static String[] PARTIAL_STR = new String[2];
        static {
          PARTIAL[0] = 7;
          PARTIAL_STR[0] = "ok";
        }
        public static long readAll() {
          long s = (FLAG_T && !FLAG_F ? 1L : 0L) + BIG + LONGS[2] + (long)FLOATS[2] + (long)DOUBLES[2]
            + NAMES[1].length() + GREET.length() + EMPTY_NUM.length + EMPTY_STR.length
            + PARTIAL[0] + PARTIAL_STR[0].length() + (long)PI_F + (long)PI_D;
          return s;
        }
        public static int switchLoopExit(int n) {
          int acc = 0;
          int i = 0;
          outer:
          while (i < n) {
            switch (i) {
              case 0: acc += 1; break;
              case 1: acc += 10; break;
              case 2: acc += 20; break outer;
              case 3: acc += 30; break;
              case 4: acc += 40; break;
              case 9: throw new IllegalArgumentException();
              default: acc += 2; break;
            }
            i++;
          }
          return acc;
        }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("TypedStatics.class", bytes);
    Assert.That(IrInterpreter.Call(mod, "readAll", []), Is.EqualTo(1L + 1234567890123L + 100000L + 2L + 2L + 2L + 2L + 7L + 2L + 3L + 6L));
    Assert.That(IrInterpreter.Call(mod, "switchLoopExit", [5]), Is.EqualTo(1 + 10 + 20));
    Assert.That(IrInterpreter.Call(mod, "switchLoopExit", [2]), Is.EqualTo(1 + 10));
    JvmTestHarness.EmitPython(mod);

    // Additional branch coverage: instance field skip, name collision, ref/string ==, break/continue/while(true), ldc array length, and unsupported external field/invokespecial
    var extraBytes = JvmTestHarness.CompileClass("ExtraCov", """
      public class ExtraCov {
        public int ignoredInstanceField;
        public static int same = 5;
        public static int[] BIG_ARR = new int[40000];
        public static int[] DYN_ARR = new int[same];
        public static int same(int n, int[] a, int[] b, String s1, String s2) {
          int acc = 0;
          int i = 0;
          while (true) {
            if (i >= n) break;
            i++;
            if (i == 2) continue;
            acc += i;
          }
          if (a == b) acc += 100;
          if (s1 == s2) acc += 200;
          if (n > 0) acc += 1;
          switch (n) {
            case 0:
            case 1:
            default:
              acc += 3;
              break;
            case 2:
              acc += 4;
              break;
          }
          return acc + BIG_ARR.length + DYN_ARR.length;
        }
      }
      """);
    var extraMod = JvmTestHarness.ImportAndVerify(".class", extraBytes);
    var fnName = extraMod.Functions.Single(f => f.IsPublic).Name;
    var arr = new int[] { 1 };
    Assert.That(IrInterpreter.Call(extraMod, fnName, [3, arr, arr, "x", "x"]), Is.EqualTo((1 + 3) + 100 + 200 + 1 + 3 + 40000));

    // Unresolved external getstatic and unsupported invokespecial
    var badStatic = JvmTestHarness.CompileClass("BadStatic", "public class BadStatic { public static int f() { return Integer.MAX_VALUE + System.out.hashCode(); } }");
    Assert.That(JvmFrontend.Import("BadStatic.class", badStatic).Diagnostics, Is.Not.Empty);
    var badNew = JvmTestHarness.CompileClass("BadNew", "public class BadNew { public static int f() { new Object(); return 1; } }");
    Assert.That(JvmFrontend.Import("BadNew.class", badNew).Diagnostics, Is.Not.Empty);
  }

}
