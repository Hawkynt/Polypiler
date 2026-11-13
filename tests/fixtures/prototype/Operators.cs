// One function per operator and operand type of the subset, so that every one of them can be compared
// with what the C# compiler makes of the same line. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Operators {

  public static int AddInt(int a, int b) => a + b;
  public static int SubtractInt(int a, int b) => a - b;
  public static int MultiplyInt(int a, int b) => a * b;
  public static int DivideInt(int a, int b) => a / b;
  public static int RemainderInt(int a, int b) => a % b;
  public static int AndInt(int a, int b) => a & b;
  public static int OrInt(int a, int b) => a | b;
  public static int XorInt(int a, int b) => a ^ b;
  public static bool EqualInt(int a, int b) => a == b;
  public static bool NotEqualInt(int a, int b) => a != b;
  public static bool LessInt(int a, int b) => a < b;
  public static bool LessOrEqualInt(int a, int b) => a <= b;
  public static bool GreaterInt(int a, int b) => a > b;
  public static bool GreaterOrEqualInt(int a, int b) => a >= b;
  public static int ShiftLeftInt(int a, int count) => a << count;
  public static int ShiftRightInt(int a, int count) => a >> count;
  public static int ShiftRightLogicalInt(int a, int count) => a >>> count;
  public static int NotInt(int a) => ~a;
  public static int PlusInt(int a) => +a;
  public static int NegateInt(int a) => -a;
  public static int AddAssignInt(int a, int b) { a += b; return a; }
  public static int SubtractAssignInt(int a, int b) { a -= b; return a; }
  public static int MultiplyAssignInt(int a, int b) { a *= b; return a; }
  public static int DivideAssignInt(int a, int b) { a /= b; return a; }
  public static int RemainderAssignInt(int a, int b) { a %= b; return a; }
  public static int AndAssignInt(int a, int b) { a &= b; return a; }
  public static int OrAssignInt(int a, int b) { a |= b; return a; }
  public static int XorAssignInt(int a, int b) { a ^= b; return a; }
  public static int ShiftLeftAssignInt(int a, int count) { a <<= count; return a; }
  public static int ShiftRightAssignInt(int a, int count) { a >>= count; return a; }
  public static int ShiftRightLogicalAssignInt(int a, int count) { a >>>= count; return a; }
  public static int IncrementInt(int a) { a++; ++a; return a; }
  public static int DecrementInt(int a) { a--; --a; return a; }
  public static int ChooseInt(bool first, int a, int b) => first ? a : b;

  public static uint AddUInt(uint a, uint b) => a + b;
  public static uint SubtractUInt(uint a, uint b) => a - b;
  public static uint MultiplyUInt(uint a, uint b) => a * b;
  public static uint DivideUInt(uint a, uint b) => a / b;
  public static uint RemainderUInt(uint a, uint b) => a % b;
  public static uint AndUInt(uint a, uint b) => a & b;
  public static uint OrUInt(uint a, uint b) => a | b;
  public static uint XorUInt(uint a, uint b) => a ^ b;
  public static bool EqualUInt(uint a, uint b) => a == b;
  public static bool NotEqualUInt(uint a, uint b) => a != b;
  public static bool LessUInt(uint a, uint b) => a < b;
  public static bool LessOrEqualUInt(uint a, uint b) => a <= b;
  public static bool GreaterUInt(uint a, uint b) => a > b;
  public static bool GreaterOrEqualUInt(uint a, uint b) => a >= b;
  public static uint ShiftLeftUInt(uint a, int count) => a << count;
  public static uint ShiftRightUInt(uint a, int count) => a >> count;
  public static uint ShiftRightLogicalUInt(uint a, int count) => a >>> count;
  public static uint NotUInt(uint a) => ~a;
  public static uint PlusUInt(uint a) => +a;
  public static uint AddAssignUInt(uint a, uint b) { a += b; return a; }
  public static uint SubtractAssignUInt(uint a, uint b) { a -= b; return a; }
  public static uint MultiplyAssignUInt(uint a, uint b) { a *= b; return a; }
  public static uint DivideAssignUInt(uint a, uint b) { a /= b; return a; }
  public static uint RemainderAssignUInt(uint a, uint b) { a %= b; return a; }
  public static uint AndAssignUInt(uint a, uint b) { a &= b; return a; }
  public static uint OrAssignUInt(uint a, uint b) { a |= b; return a; }
  public static uint XorAssignUInt(uint a, uint b) { a ^= b; return a; }
  public static uint ShiftLeftAssignUInt(uint a, int count) { a <<= count; return a; }
  public static uint ShiftRightAssignUInt(uint a, int count) { a >>= count; return a; }
  public static uint ShiftRightLogicalAssignUInt(uint a, int count) { a >>>= count; return a; }
  public static uint IncrementUInt(uint a) { a++; ++a; return a; }
  public static uint DecrementUInt(uint a) { a--; --a; return a; }
  public static uint ChooseUInt(bool first, uint a, uint b) => first ? a : b;

  public static int AddByte(byte a, byte b) => a + b;
  public static int SubtractByte(byte a, byte b) => a - b;
  public static int MultiplyByte(byte a, byte b) => a * b;
  public static int DivideByte(byte a, byte b) => a / b;
  public static int RemainderByte(byte a, byte b) => a % b;
  public static int AndByte(byte a, byte b) => a & b;
  public static int OrByte(byte a, byte b) => a | b;
  public static int XorByte(byte a, byte b) => a ^ b;
  public static bool EqualByte(byte a, byte b) => a == b;
  public static bool NotEqualByte(byte a, byte b) => a != b;
  public static bool LessByte(byte a, byte b) => a < b;
  public static bool LessOrEqualByte(byte a, byte b) => a <= b;
  public static bool GreaterByte(byte a, byte b) => a > b;
  public static bool GreaterOrEqualByte(byte a, byte b) => a >= b;
  public static int ShiftLeftByte(byte a, int count) => a << count;
  public static int ShiftRightByte(byte a, int count) => a >> count;
  public static int ShiftRightLogicalByte(byte a, int count) => a >>> count;
  public static int NotByte(byte a) => ~a;
  public static int PlusByte(byte a) => +a;
  public static int NegateByte(byte a) => -a;
  public static byte AddAssignByte(byte a, byte b) { a += b; return a; }
  public static byte SubtractAssignByte(byte a, byte b) { a -= b; return a; }
  public static byte MultiplyAssignByte(byte a, byte b) { a *= b; return a; }
  public static byte DivideAssignByte(byte a, byte b) { a /= b; return a; }
  public static byte RemainderAssignByte(byte a, byte b) { a %= b; return a; }
  public static byte AndAssignByte(byte a, byte b) { a &= b; return a; }
  public static byte OrAssignByte(byte a, byte b) { a |= b; return a; }
  public static byte XorAssignByte(byte a, byte b) { a ^= b; return a; }
  public static byte ShiftLeftAssignByte(byte a, int count) { a <<= count; return a; }
  public static byte ShiftRightAssignByte(byte a, int count) { a >>= count; return a; }
  public static byte ShiftRightLogicalAssignByte(byte a, int count) { a >>>= count; return a; }
  public static byte IncrementByte(byte a) { a++; ++a; return a; }
  public static byte DecrementByte(byte a) { a--; --a; return a; }
  public static byte ChooseByte(bool first, byte a, byte b) => first ? a : b;

  public static uint IntToUInt(int a) => (uint)a;
  public static byte IntToByte(int a) => (byte)a;
  public static int UIntToInt(uint a) => (int)a;
  public static byte UIntToByte(uint a) => (byte)a;
  public static int ByteToInt(byte a) => a;
  public static uint ByteToUInt(byte a) => a;
  public static uint ByteToUIntCast(byte a) => (uint)a;
  public static int IntToInt(int a) => (int)a;

  public static bool AndBool(bool a, bool b) => a && b;
  public static bool OrBool(bool a, bool b) => a || b;
  public static bool NotBool(bool a) => !a;
  public static bool EqualBool(bool a, bool b) => a == b;
  public static bool NotEqualBool(bool a, bool b) => a != b;
  public static bool ChooseBool(bool first, bool a, bool b) => first ? a : b;

  // Constants the compiler folds, and the conversions it applies to them.
  public static uint Mask(uint a) => a & 0xFF00FF00;
  public static int Folded(int a) => a + (1 << 4) - int.MaxValue;
  public static uint HighBit(byte a) => a | 0x80000000;
  public static int Smallest() => int.MinValue;
  public static bool Truth() => true;

}
