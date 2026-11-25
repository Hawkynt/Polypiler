using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Jvm;

[TestFixture]
public sealed class JvmScalarAndControlTests {

  [Test]
  public void ArithmeticShiftsAndBitwise_MatchJavaSemantics_AndEmitPython() {
    var bytes = JvmTestHarness.CompileClass("ScalarOps", """
      public class ScalarOps {
        public static int addWrap(int a, int b) { return a + b; }
        public static int subMulNeg(int a, int b) { return -(a - b) * 3; }
        public static int divRem(int a, int b) { return (a / b) * 100 + (a % b); }
        public static long longArith(long a, long b) {
          long s = (a + b) - (a * 3L);
          return (s / b) + (s % b) - (-a);
        }
        public static int shifts32(int x, int n) {
          return (x << n) ^ (x >> n) ^ (x >>> n);
        }
        public static long shifts64(long x, int n) {
          return ((x << n) | (x >> n)) ^ (x >>> n);
        }
        public static int bitwise32(int a, int b) {
          return (a & b) | ((a ^ b) & 0xFF);
        }
        public static long bitwise64(long a, long b) {
          return (a & b) | (a ^ b);
        }
        public static float floatOps(float a, float b) {
          return -((a + b) * (a - b) / b) + (a % b);
        }
        public static double doubleOps(double a, double b) {
          return -((a + b) * (a - b) / b) + (a % b);
        }
        public static int largeConsts(int x) {
          return x + 100000 - 30000 + 7;
        }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("ScalarOps.class", bytes);
    Assert.That(IrInterpreter.Call(mod, "addWrap", [int.MaxValue, 1]), Is.EqualTo(int.MinValue));
    Assert.That(IrInterpreter.Call(mod, "subMulNeg", [10, 4]), Is.EqualTo(-18));
    Assert.That(IrInterpreter.Call(mod, "divRem", [int.MinValue, -1]), Is.EqualTo(unchecked(int.MinValue * 100)));
    Assert.That(IrInterpreter.Call(mod, "divRem", [-7, 2]), Is.EqualTo(-301));
    Assert.That(IrInterpreter.Call(mod, "longArith", [10L, 3L]), Is.EqualTo(3L));
    Assert.That(IrInterpreter.Call(mod, "shifts32", [-16, 33]), Is.EqualTo((-16 << 1) ^ (-16 >> 1) ^ (-16 >>> 1)));
    Assert.That(IrInterpreter.Call(mod, "shifts64", [-16L, 65]), Is.EqualTo(((-16L << 1) | (-16L >> 1)) ^ (-16L >>> 1)));
    Assert.That(IrInterpreter.Call(mod, "bitwise32", [0x1234, 0x0F0F]), Is.EqualTo((0x1234 & 0x0F0F) | ((0x1234 ^ 0x0F0F) & 0xFF)));
    Assert.That(IrInterpreter.Call(mod, "bitwise64", [0x1234_5678L, 0x0F0F_0F0FL]), Is.EqualTo((0x1234_5678L & 0x0F0F_0F0FL) | (0x1234_5678L ^ 0x0F0F_0F0FL)));
    Assert.That((float)IrInterpreter.Call(mod, "floatOps", [5.5f, 2.0f])!, Is.EqualTo(-((5.5f + 2.0f) * (5.5f - 2.0f) / 2.0f) + (5.5f % 2.0f)).Within(1e-5f));
    Assert.That((double)IrInterpreter.Call(mod, "doubleOps", [5.5, 2.0])!, Is.EqualTo(-((5.5 + 2.0) * (5.5 - 2.0) / 2.0) + (5.5 % 2.0)).Within(1e-12));
    Assert.That(IrInterpreter.Call(mod, "largeConsts", [5]), Is.EqualTo(70012));

    var py = JvmTestHarness.EmitPython(mod);
    Assert.That(py, Does.Contain("def add_wrap("));
  }

  [Test]
  public void ConversionsAndComparisons_PreserveJavaTruncationSaturationAndNaN() {
    var bytes = JvmTestHarness.CompileClass("ConvCmp", """
      public class ConvCmp {
        public static int narrow(int x) {
          byte b = (byte)x;
          short s = (short)x;
          char c = (char)x;
          return b + s + c;
        }
        public static long widen(int x, float f, double d) {
          return (long)x + (long)f + (long)d;
        }
        public static int fromFloatDouble(float f, double d) {
          return (int)f + (int)d;
        }
        public static double toFloatDouble(int i, long l, float f, double d) {
          float f1 = (float)i + (float)l + (float)d;
          double d1 = (double)i + (double)l + (double)f;
          return d1 + (double)f1;
        }
        public static int l2iVal(long x) { return (int)x; }
        public static int cmpLong(long a, long b) {
          if (a < b) return -1;
          if (a > b) return 1;
          return 0;
        }
        public static int cmpFloatLt(float a, float b) { return a < b ? 1 : 0; }
        public static int cmpFloatGt(float a, float b) { return a > b ? 1 : 0; }
        public static int cmpDoubleLt(double a, double b) { return a < b ? 1 : 0; }
        public static int cmpDoubleGt(double a, double b) { return a > b ? 1 : 0; }
        public static boolean narrowParams(boolean z, byte b, short s, char c) {
          return z && (b + s + c > 0);
        }
        public static byte retByte(int x) { return (byte)x; }
        public static short retShort(int x) { return (short)x; }
        public static char retChar(int x) { return (char)x; }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("ConvCmp.class", bytes);
    Assert.That(IrInterpreter.Call(mod, "narrow", [0x12345680]), Is.EqualTo(unchecked((sbyte)0x80) + (short)0x5680 + (ushort)0x5680));
    Assert.That(IrInterpreter.Call(mod, "widen", [10, 2.9f, double.NaN]), Is.EqualTo(12L));
    Assert.That(IrInterpreter.Call(mod, "fromFloatDouble", [1e20f, double.NaN]), Is.EqualTo(int.MaxValue));
    Assert.That(IrInterpreter.Call(mod, "fromFloatDouble", [-1e20f, -1e20]), Is.EqualTo(unchecked(int.MinValue + int.MinValue)));
    Assert.That((double)IrInterpreter.Call(mod, "toFloatDouble", [1, 2L, 3.0f, 4.0])!, Is.EqualTo(13.0).Within(1e-6));
    Assert.That(IrInterpreter.Call(mod, "l2iVal", [0x1_0000_002AL]), Is.EqualTo(42));
    Assert.That(IrInterpreter.Call(mod, "cmpLong", [1L, 2L]), Is.EqualTo(-1));
    Assert.That(IrInterpreter.Call(mod, "cmpLong", [2L, 2L]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "cmpLong", [3L, 2L]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(mod, "cmpFloatLt", [float.NaN, 1.0f]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "cmpFloatGt", [float.NaN, 1.0f]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "cmpDoubleLt", [double.NaN, 1.0]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "cmpDoubleGt", [double.NaN, 1.0]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "cmpDoubleLt", [1.0, 2.0]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(mod, "narrowParams", [true, (sbyte)-1, (short)2, (ushort)3]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(mod, "narrowParams", [false, (sbyte)1, (short)2, (ushort)3]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(mod, "retByte", [255]), Is.EqualTo((sbyte)-1));
    Assert.That(IrInterpreter.Call(mod, "retShort", [65535]), Is.EqualTo((short)-1));
    Assert.That(IrInterpreter.Call(mod, "retChar", [-1]), Is.EqualTo((ushort)65535));

    JvmTestHarness.EmitPython(mod);
  }

  [Test]
  public void BranchesLoopsSwitchesAndTernary_RecoverStructuredIr_AndEmitPython() {
    var bytes = JvmTestHarness.CompileClass("ControlOps", """
      public class ControlOps {
        public static int classify(int x) {
          if (x < 0) return -1;
          else if (x == 0) return 0;
          else if (x <= 10) return 1;
          else if (x > 100) return 3;
          else return 2;
        }
        public static int ternaryAndLogic(int a, int b) {
          int m = (a >= b && b != 0) ? a : (b > 0 ? b : -b);
          return m;
        }
        public static int sumFor(int n) {
          int acc = 0;
          for (int i = 0; i < n; i++) {
            if (i == 3) continue;
            if (i == 8) break;
            acc += i;
          }
          return acc;
        }
        public static int doWhilePow2(int n) {
          int p = 1;
          int i = 0;
          do {
            p *= 2;
            i++;
          } while (i < n);
          return p;
        }
        public static int tableSwitch(int k) {
          int r;
          switch (k) {
            case 0: r = 10; break;
            case 1:
            case 2: r = 20; break;
            case 3: r = 30; break;
            default: r = -1; break;
          }
          return r;
        }
        public static int lookupSwitch(int k) {
          switch (k) {
            case 10: return 1;
            case 100: return 2;
            case 1000: return 3;
            default: return 0;
          }
        }
        public static int gcd(int a, int b) {
          while (b != 0) {
            int t = a % b;
            a = b;
            b = t;
          }
          return a;
        }
        public static int switchInLoop(int limit) {
          int sum = 0;
          for (int i = 0; i < limit; i++) {
            switch (i) {
              case 2: sum += 100; break;
              case 5: return sum;
              default: sum += i; break;
            }
          }
          return sum;
        }
      }
      """);

    var mod = JvmTestHarness.ImportAndVerify("ControlOps.class", bytes);
    Assert.That(IrInterpreter.Call(mod, "classify", [-5]), Is.EqualTo(-1));
    Assert.That(IrInterpreter.Call(mod, "classify", [0]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "classify", [7]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(mod, "classify", [50]), Is.EqualTo(2));
    Assert.That(IrInterpreter.Call(mod, "classify", [200]), Is.EqualTo(3));
    Assert.That(IrInterpreter.Call(mod, "ternaryAndLogic", [5, 2]), Is.EqualTo(5));
    Assert.That(IrInterpreter.Call(mod, "ternaryAndLogic", [-5, -4]), Is.EqualTo(4));
    Assert.That(IrInterpreter.Call(mod, "ternaryAndLogic", [1, -4]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(mod, "sumFor", [12]), Is.EqualTo(0 + 1 + 2 + 4 + 5 + 6 + 7));
    Assert.That(IrInterpreter.Call(mod, "doWhilePow2", [4]), Is.EqualTo(16));
    Assert.That(IrInterpreter.Call(mod, "doWhilePow2", [0]), Is.EqualTo(2));
    Assert.That(IrInterpreter.Call(mod, "tableSwitch", [0]), Is.EqualTo(10));
    Assert.That(IrInterpreter.Call(mod, "tableSwitch", [1]), Is.EqualTo(20));
    Assert.That(IrInterpreter.Call(mod, "tableSwitch", [2]), Is.EqualTo(20));
    Assert.That(IrInterpreter.Call(mod, "tableSwitch", [3]), Is.EqualTo(30));
    Assert.That(IrInterpreter.Call(mod, "tableSwitch", [99]), Is.EqualTo(-1));
    Assert.That(IrInterpreter.Call(mod, "lookupSwitch", [10]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(mod, "lookupSwitch", [100]), Is.EqualTo(2));
    Assert.That(IrInterpreter.Call(mod, "lookupSwitch", [1000]), Is.EqualTo(3));
    Assert.That(IrInterpreter.Call(mod, "lookupSwitch", [42]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(mod, "gcd", [48, 18]), Is.EqualTo(6));
    Assert.That(IrInterpreter.Call(mod, "switchInLoop", [10]), Is.EqualTo(0 + 1 + 100 + 3 + 4));

    JvmTestHarness.EmitPython(mod);
  }

}
