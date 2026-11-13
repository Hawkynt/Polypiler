// One function per operator and operand type beyond the three of Operators.cs, and the checked form of
// every operator that has one, so that each can be compared with what the C# compiler makes of the
// same line. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class OperatorsWide {

  public static int AddSByte(sbyte a, sbyte b) => a + b;
  public static int SubtractSByte(sbyte a, sbyte b) => a - b;
  public static int MultiplySByte(sbyte a, sbyte b) => a * b;
  public static int DivideSByte(sbyte a, sbyte b) => a / b;
  public static int RemainderSByte(sbyte a, sbyte b) => a % b;
  public static int AndSByte(sbyte a, sbyte b) => a & b;
  public static int OrSByte(sbyte a, sbyte b) => a | b;
  public static int XorSByte(sbyte a, sbyte b) => a ^ b;
  public static bool EqualSByte(sbyte a, sbyte b) => a == b;
  public static bool NotEqualSByte(sbyte a, sbyte b) => a != b;
  public static bool LessSByte(sbyte a, sbyte b) => a < b;
  public static bool LessOrEqualSByte(sbyte a, sbyte b) => a <= b;
  public static bool GreaterSByte(sbyte a, sbyte b) => a > b;
  public static bool GreaterOrEqualSByte(sbyte a, sbyte b) => a >= b;
  public static int ShiftLeftSByte(sbyte a, int count) => a << count;
  public static int ShiftRightSByte(sbyte a, int count) => a >> count;
  public static int ShiftRightLogicalSByte(sbyte a, int count) => a >>> count;
  public static int NotSByte(sbyte a) => ~a;
  public static int PlusSByte(sbyte a) => +a;
  public static int NegateSByte(sbyte a) => -a;
  public static sbyte AddAssignSByte(sbyte a, sbyte b) { a += b; return a; }
  public static sbyte SubtractAssignSByte(sbyte a, sbyte b) { a -= b; return a; }
  public static sbyte MultiplyAssignSByte(sbyte a, sbyte b) { a *= b; return a; }
  public static sbyte DivideAssignSByte(sbyte a, sbyte b) { a /= b; return a; }
  public static sbyte RemainderAssignSByte(sbyte a, sbyte b) { a %= b; return a; }
  public static sbyte AndAssignSByte(sbyte a, sbyte b) { a &= b; return a; }
  public static sbyte OrAssignSByte(sbyte a, sbyte b) { a |= b; return a; }
  public static sbyte XorAssignSByte(sbyte a, sbyte b) { a ^= b; return a; }
  public static sbyte ShiftLeftAssignSByte(sbyte a, int count) { a <<= count; return a; }
  public static sbyte ShiftRightAssignSByte(sbyte a, int count) { a >>= count; return a; }
  public static sbyte ShiftRightLogicalAssignSByte(sbyte a, int count) { a >>>= count; return a; }
  public static sbyte IncrementSByte(sbyte a) { a++; ++a; return a; }
  public static sbyte DecrementSByte(sbyte a) { a--; --a; return a; }
  public static sbyte ChooseSByte(bool first, sbyte a, sbyte b) => first ? a : b;
  public static int AddCheckedSByte(sbyte a, sbyte b) => checked(a + b);
  public static int SubtractCheckedSByte(sbyte a, sbyte b) => checked(a - b);
  public static int MultiplyCheckedSByte(sbyte a, sbyte b) => checked(a * b);
  public static int NegateCheckedSByte(sbyte a) => checked(-a);
  public static sbyte AddAssignCheckedSByte(sbyte a, sbyte b) { checked { a += b; } return a; }
  public static sbyte SubtractAssignCheckedSByte(sbyte a, sbyte b) { checked { a -= b; } return a; }
  public static sbyte MultiplyAssignCheckedSByte(sbyte a, sbyte b) { checked { a *= b; } return a; }
  public static sbyte ShiftLeftAssignCheckedSByte(sbyte a, int count) { checked { a <<= count; } return a; }
  public static sbyte IncrementCheckedSByte(sbyte a) { checked { a++; ++a; } return a; }
  public static sbyte DecrementCheckedSByte(sbyte a) { checked { a--; --a; } return a; }
  public static int UncheckedInsideCheckedSByte(sbyte a, sbyte b) => checked(unchecked(a + b) - b);

  public static int AddCheckedByte(byte a, byte b) => checked(a + b);
  public static int SubtractCheckedByte(byte a, byte b) => checked(a - b);
  public static int MultiplyCheckedByte(byte a, byte b) => checked(a * b);
  public static int NegateCheckedByte(byte a) => checked(-a);
  public static byte AddAssignCheckedByte(byte a, byte b) { checked { a += b; } return a; }
  public static byte SubtractAssignCheckedByte(byte a, byte b) { checked { a -= b; } return a; }
  public static byte MultiplyAssignCheckedByte(byte a, byte b) { checked { a *= b; } return a; }
  public static byte ShiftLeftAssignCheckedByte(byte a, int count) { checked { a <<= count; } return a; }
  public static byte IncrementCheckedByte(byte a) { checked { a++; ++a; } return a; }
  public static byte DecrementCheckedByte(byte a) { checked { a--; --a; } return a; }
  public static int UncheckedInsideCheckedByte(byte a, byte b) => checked(unchecked(a + b) - b);

  public static int AddShort(short a, short b) => a + b;
  public static int SubtractShort(short a, short b) => a - b;
  public static int MultiplyShort(short a, short b) => a * b;
  public static int DivideShort(short a, short b) => a / b;
  public static int RemainderShort(short a, short b) => a % b;
  public static int AndShort(short a, short b) => a & b;
  public static int OrShort(short a, short b) => a | b;
  public static int XorShort(short a, short b) => a ^ b;
  public static bool EqualShort(short a, short b) => a == b;
  public static bool NotEqualShort(short a, short b) => a != b;
  public static bool LessShort(short a, short b) => a < b;
  public static bool LessOrEqualShort(short a, short b) => a <= b;
  public static bool GreaterShort(short a, short b) => a > b;
  public static bool GreaterOrEqualShort(short a, short b) => a >= b;
  public static int ShiftLeftShort(short a, int count) => a << count;
  public static int ShiftRightShort(short a, int count) => a >> count;
  public static int ShiftRightLogicalShort(short a, int count) => a >>> count;
  public static int NotShort(short a) => ~a;
  public static int PlusShort(short a) => +a;
  public static int NegateShort(short a) => -a;
  public static short AddAssignShort(short a, short b) { a += b; return a; }
  public static short SubtractAssignShort(short a, short b) { a -= b; return a; }
  public static short MultiplyAssignShort(short a, short b) { a *= b; return a; }
  public static short DivideAssignShort(short a, short b) { a /= b; return a; }
  public static short RemainderAssignShort(short a, short b) { a %= b; return a; }
  public static short AndAssignShort(short a, short b) { a &= b; return a; }
  public static short OrAssignShort(short a, short b) { a |= b; return a; }
  public static short XorAssignShort(short a, short b) { a ^= b; return a; }
  public static short ShiftLeftAssignShort(short a, int count) { a <<= count; return a; }
  public static short ShiftRightAssignShort(short a, int count) { a >>= count; return a; }
  public static short ShiftRightLogicalAssignShort(short a, int count) { a >>>= count; return a; }
  public static short IncrementShort(short a) { a++; ++a; return a; }
  public static short DecrementShort(short a) { a--; --a; return a; }
  public static short ChooseShort(bool first, short a, short b) => first ? a : b;
  public static int AddCheckedShort(short a, short b) => checked(a + b);
  public static int SubtractCheckedShort(short a, short b) => checked(a - b);
  public static int MultiplyCheckedShort(short a, short b) => checked(a * b);
  public static int NegateCheckedShort(short a) => checked(-a);
  public static short AddAssignCheckedShort(short a, short b) { checked { a += b; } return a; }
  public static short SubtractAssignCheckedShort(short a, short b) { checked { a -= b; } return a; }
  public static short MultiplyAssignCheckedShort(short a, short b) { checked { a *= b; } return a; }
  public static short ShiftLeftAssignCheckedShort(short a, int count) { checked { a <<= count; } return a; }
  public static short IncrementCheckedShort(short a) { checked { a++; ++a; } return a; }
  public static short DecrementCheckedShort(short a) { checked { a--; --a; } return a; }
  public static int UncheckedInsideCheckedShort(short a, short b) => checked(unchecked(a + b) - b);

  public static int AddUShort(ushort a, ushort b) => a + b;
  public static int SubtractUShort(ushort a, ushort b) => a - b;
  public static int MultiplyUShort(ushort a, ushort b) => a * b;
  public static int DivideUShort(ushort a, ushort b) => a / b;
  public static int RemainderUShort(ushort a, ushort b) => a % b;
  public static int AndUShort(ushort a, ushort b) => a & b;
  public static int OrUShort(ushort a, ushort b) => a | b;
  public static int XorUShort(ushort a, ushort b) => a ^ b;
  public static bool EqualUShort(ushort a, ushort b) => a == b;
  public static bool NotEqualUShort(ushort a, ushort b) => a != b;
  public static bool LessUShort(ushort a, ushort b) => a < b;
  public static bool LessOrEqualUShort(ushort a, ushort b) => a <= b;
  public static bool GreaterUShort(ushort a, ushort b) => a > b;
  public static bool GreaterOrEqualUShort(ushort a, ushort b) => a >= b;
  public static int ShiftLeftUShort(ushort a, int count) => a << count;
  public static int ShiftRightUShort(ushort a, int count) => a >> count;
  public static int ShiftRightLogicalUShort(ushort a, int count) => a >>> count;
  public static int NotUShort(ushort a) => ~a;
  public static int PlusUShort(ushort a) => +a;
  public static int NegateUShort(ushort a) => -a;
  public static ushort AddAssignUShort(ushort a, ushort b) { a += b; return a; }
  public static ushort SubtractAssignUShort(ushort a, ushort b) { a -= b; return a; }
  public static ushort MultiplyAssignUShort(ushort a, ushort b) { a *= b; return a; }
  public static ushort DivideAssignUShort(ushort a, ushort b) { a /= b; return a; }
  public static ushort RemainderAssignUShort(ushort a, ushort b) { a %= b; return a; }
  public static ushort AndAssignUShort(ushort a, ushort b) { a &= b; return a; }
  public static ushort OrAssignUShort(ushort a, ushort b) { a |= b; return a; }
  public static ushort XorAssignUShort(ushort a, ushort b) { a ^= b; return a; }
  public static ushort ShiftLeftAssignUShort(ushort a, int count) { a <<= count; return a; }
  public static ushort ShiftRightAssignUShort(ushort a, int count) { a >>= count; return a; }
  public static ushort ShiftRightLogicalAssignUShort(ushort a, int count) { a >>>= count; return a; }
  public static ushort IncrementUShort(ushort a) { a++; ++a; return a; }
  public static ushort DecrementUShort(ushort a) { a--; --a; return a; }
  public static ushort ChooseUShort(bool first, ushort a, ushort b) => first ? a : b;
  public static int AddCheckedUShort(ushort a, ushort b) => checked(a + b);
  public static int SubtractCheckedUShort(ushort a, ushort b) => checked(a - b);
  public static int MultiplyCheckedUShort(ushort a, ushort b) => checked(a * b);
  public static int NegateCheckedUShort(ushort a) => checked(-a);
  public static ushort AddAssignCheckedUShort(ushort a, ushort b) { checked { a += b; } return a; }
  public static ushort SubtractAssignCheckedUShort(ushort a, ushort b) { checked { a -= b; } return a; }
  public static ushort MultiplyAssignCheckedUShort(ushort a, ushort b) { checked { a *= b; } return a; }
  public static ushort ShiftLeftAssignCheckedUShort(ushort a, int count) { checked { a <<= count; } return a; }
  public static ushort IncrementCheckedUShort(ushort a) { checked { a++; ++a; } return a; }
  public static ushort DecrementCheckedUShort(ushort a) { checked { a--; --a; } return a; }
  public static int UncheckedInsideCheckedUShort(ushort a, ushort b) => checked(unchecked(a + b) - b);

  public static int AddChar(char a, char b) => a + b;
  public static int SubtractChar(char a, char b) => a - b;
  public static int MultiplyChar(char a, char b) => a * b;
  public static int DivideChar(char a, char b) => a / b;
  public static int RemainderChar(char a, char b) => a % b;
  public static int AndChar(char a, char b) => a & b;
  public static int OrChar(char a, char b) => a | b;
  public static int XorChar(char a, char b) => a ^ b;
  public static bool EqualChar(char a, char b) => a == b;
  public static bool NotEqualChar(char a, char b) => a != b;
  public static bool LessChar(char a, char b) => a < b;
  public static bool LessOrEqualChar(char a, char b) => a <= b;
  public static bool GreaterChar(char a, char b) => a > b;
  public static bool GreaterOrEqualChar(char a, char b) => a >= b;
  public static int ShiftLeftChar(char a, int count) => a << count;
  public static int ShiftRightChar(char a, int count) => a >> count;
  public static int ShiftRightLogicalChar(char a, int count) => a >>> count;
  public static int NotChar(char a) => ~a;
  public static int PlusChar(char a) => +a;
  public static int NegateChar(char a) => -a;
  public static char AddAssignChar(char a, char b) { a += b; return a; }
  public static char SubtractAssignChar(char a, char b) { a -= b; return a; }
  public static char MultiplyAssignChar(char a, char b) { a *= b; return a; }
  public static char DivideAssignChar(char a, char b) { a /= b; return a; }
  public static char RemainderAssignChar(char a, char b) { a %= b; return a; }
  public static char AndAssignChar(char a, char b) { a &= b; return a; }
  public static char OrAssignChar(char a, char b) { a |= b; return a; }
  public static char XorAssignChar(char a, char b) { a ^= b; return a; }
  public static char ShiftLeftAssignChar(char a, int count) { a <<= count; return a; }
  public static char ShiftRightAssignChar(char a, int count) { a >>= count; return a; }
  public static char ShiftRightLogicalAssignChar(char a, int count) { a >>>= count; return a; }
  public static char IncrementChar(char a) { a++; ++a; return a; }
  public static char DecrementChar(char a) { a--; --a; return a; }
  public static char ChooseChar(bool first, char a, char b) => first ? a : b;
  public static int AddCheckedChar(char a, char b) => checked(a + b);
  public static int SubtractCheckedChar(char a, char b) => checked(a - b);
  public static int MultiplyCheckedChar(char a, char b) => checked(a * b);
  public static int NegateCheckedChar(char a) => checked(-a);
  public static char AddAssignCheckedChar(char a, char b) { checked { a += b; } return a; }
  public static char SubtractAssignCheckedChar(char a, char b) { checked { a -= b; } return a; }
  public static char MultiplyAssignCheckedChar(char a, char b) { checked { a *= b; } return a; }
  public static char ShiftLeftAssignCheckedChar(char a, int count) { checked { a <<= count; } return a; }
  public static char IncrementCheckedChar(char a) { checked { a++; ++a; } return a; }
  public static char DecrementCheckedChar(char a) { checked { a--; --a; } return a; }
  public static int UncheckedInsideCheckedChar(char a, char b) => checked(unchecked(a + b) - b);

  public static int AddCheckedInt(int a, int b) => checked(a + b);
  public static int SubtractCheckedInt(int a, int b) => checked(a - b);
  public static int MultiplyCheckedInt(int a, int b) => checked(a * b);
  public static int NegateCheckedInt(int a) => checked(-a);
  public static int AddAssignCheckedInt(int a, int b) { checked { a += b; } return a; }
  public static int SubtractAssignCheckedInt(int a, int b) { checked { a -= b; } return a; }
  public static int MultiplyAssignCheckedInt(int a, int b) { checked { a *= b; } return a; }
  public static int ShiftLeftAssignCheckedInt(int a, int count) { checked { a <<= count; } return a; }
  public static int IncrementCheckedInt(int a) { checked { a++; ++a; } return a; }
  public static int DecrementCheckedInt(int a) { checked { a--; --a; } return a; }
  public static int UncheckedInsideCheckedInt(int a, int b) => checked(unchecked(a + b) - b);

  public static uint AddCheckedUInt(uint a, uint b) => checked(a + b);
  public static uint SubtractCheckedUInt(uint a, uint b) => checked(a - b);
  public static uint MultiplyCheckedUInt(uint a, uint b) => checked(a * b);
  public static long NegateCheckedUInt(uint a) => checked(-a);
  public static uint AddAssignCheckedUInt(uint a, uint b) { checked { a += b; } return a; }
  public static uint SubtractAssignCheckedUInt(uint a, uint b) { checked { a -= b; } return a; }
  public static uint MultiplyAssignCheckedUInt(uint a, uint b) { checked { a *= b; } return a; }
  public static uint ShiftLeftAssignCheckedUInt(uint a, int count) { checked { a <<= count; } return a; }
  public static uint IncrementCheckedUInt(uint a) { checked { a++; ++a; } return a; }
  public static uint DecrementCheckedUInt(uint a) { checked { a--; --a; } return a; }
  public static uint UncheckedInsideCheckedUInt(uint a, uint b) => checked(unchecked(a + b) - b);

  public static long AddLong(long a, long b) => a + b;
  public static long SubtractLong(long a, long b) => a - b;
  public static long MultiplyLong(long a, long b) => a * b;
  public static long DivideLong(long a, long b) => a / b;
  public static long RemainderLong(long a, long b) => a % b;
  public static long AndLong(long a, long b) => a & b;
  public static long OrLong(long a, long b) => a | b;
  public static long XorLong(long a, long b) => a ^ b;
  public static bool EqualLong(long a, long b) => a == b;
  public static bool NotEqualLong(long a, long b) => a != b;
  public static bool LessLong(long a, long b) => a < b;
  public static bool LessOrEqualLong(long a, long b) => a <= b;
  public static bool GreaterLong(long a, long b) => a > b;
  public static bool GreaterOrEqualLong(long a, long b) => a >= b;
  public static long ShiftLeftLong(long a, int count) => a << count;
  public static long ShiftRightLong(long a, int count) => a >> count;
  public static long ShiftRightLogicalLong(long a, int count) => a >>> count;
  public static long NotLong(long a) => ~a;
  public static long PlusLong(long a) => +a;
  public static long NegateLong(long a) => -a;
  public static long AddAssignLong(long a, long b) { a += b; return a; }
  public static long SubtractAssignLong(long a, long b) { a -= b; return a; }
  public static long MultiplyAssignLong(long a, long b) { a *= b; return a; }
  public static long DivideAssignLong(long a, long b) { a /= b; return a; }
  public static long RemainderAssignLong(long a, long b) { a %= b; return a; }
  public static long AndAssignLong(long a, long b) { a &= b; return a; }
  public static long OrAssignLong(long a, long b) { a |= b; return a; }
  public static long XorAssignLong(long a, long b) { a ^= b; return a; }
  public static long ShiftLeftAssignLong(long a, int count) { a <<= count; return a; }
  public static long ShiftRightAssignLong(long a, int count) { a >>= count; return a; }
  public static long ShiftRightLogicalAssignLong(long a, int count) { a >>>= count; return a; }
  public static long IncrementLong(long a) { a++; ++a; return a; }
  public static long DecrementLong(long a) { a--; --a; return a; }
  public static long ChooseLong(bool first, long a, long b) => first ? a : b;
  public static long AddCheckedLong(long a, long b) => checked(a + b);
  public static long SubtractCheckedLong(long a, long b) => checked(a - b);
  public static long MultiplyCheckedLong(long a, long b) => checked(a * b);
  public static long NegateCheckedLong(long a) => checked(-a);
  public static long AddAssignCheckedLong(long a, long b) { checked { a += b; } return a; }
  public static long SubtractAssignCheckedLong(long a, long b) { checked { a -= b; } return a; }
  public static long MultiplyAssignCheckedLong(long a, long b) { checked { a *= b; } return a; }
  public static long ShiftLeftAssignCheckedLong(long a, int count) { checked { a <<= count; } return a; }
  public static long IncrementCheckedLong(long a) { checked { a++; ++a; } return a; }
  public static long DecrementCheckedLong(long a) { checked { a--; --a; } return a; }
  public static long UncheckedInsideCheckedLong(long a, long b) => checked(unchecked(a + b) - b);

  public static ulong AddULong(ulong a, ulong b) => a + b;
  public static ulong SubtractULong(ulong a, ulong b) => a - b;
  public static ulong MultiplyULong(ulong a, ulong b) => a * b;
  public static ulong DivideULong(ulong a, ulong b) => a / b;
  public static ulong RemainderULong(ulong a, ulong b) => a % b;
  public static ulong AndULong(ulong a, ulong b) => a & b;
  public static ulong OrULong(ulong a, ulong b) => a | b;
  public static ulong XorULong(ulong a, ulong b) => a ^ b;
  public static bool EqualULong(ulong a, ulong b) => a == b;
  public static bool NotEqualULong(ulong a, ulong b) => a != b;
  public static bool LessULong(ulong a, ulong b) => a < b;
  public static bool LessOrEqualULong(ulong a, ulong b) => a <= b;
  public static bool GreaterULong(ulong a, ulong b) => a > b;
  public static bool GreaterOrEqualULong(ulong a, ulong b) => a >= b;
  public static ulong ShiftLeftULong(ulong a, int count) => a << count;
  public static ulong ShiftRightULong(ulong a, int count) => a >> count;
  public static ulong ShiftRightLogicalULong(ulong a, int count) => a >>> count;
  public static ulong NotULong(ulong a) => ~a;
  public static ulong PlusULong(ulong a) => +a;
  public static ulong AddAssignULong(ulong a, ulong b) { a += b; return a; }
  public static ulong SubtractAssignULong(ulong a, ulong b) { a -= b; return a; }
  public static ulong MultiplyAssignULong(ulong a, ulong b) { a *= b; return a; }
  public static ulong DivideAssignULong(ulong a, ulong b) { a /= b; return a; }
  public static ulong RemainderAssignULong(ulong a, ulong b) { a %= b; return a; }
  public static ulong AndAssignULong(ulong a, ulong b) { a &= b; return a; }
  public static ulong OrAssignULong(ulong a, ulong b) { a |= b; return a; }
  public static ulong XorAssignULong(ulong a, ulong b) { a ^= b; return a; }
  public static ulong ShiftLeftAssignULong(ulong a, int count) { a <<= count; return a; }
  public static ulong ShiftRightAssignULong(ulong a, int count) { a >>= count; return a; }
  public static ulong ShiftRightLogicalAssignULong(ulong a, int count) { a >>>= count; return a; }
  public static ulong IncrementULong(ulong a) { a++; ++a; return a; }
  public static ulong DecrementULong(ulong a) { a--; --a; return a; }
  public static ulong ChooseULong(bool first, ulong a, ulong b) => first ? a : b;
  public static ulong AddCheckedULong(ulong a, ulong b) => checked(a + b);
  public static ulong SubtractCheckedULong(ulong a, ulong b) => checked(a - b);
  public static ulong MultiplyCheckedULong(ulong a, ulong b) => checked(a * b);
  public static ulong AddAssignCheckedULong(ulong a, ulong b) { checked { a += b; } return a; }
  public static ulong SubtractAssignCheckedULong(ulong a, ulong b) { checked { a -= b; } return a; }
  public static ulong MultiplyAssignCheckedULong(ulong a, ulong b) { checked { a *= b; } return a; }
  public static ulong ShiftLeftAssignCheckedULong(ulong a, int count) { checked { a <<= count; } return a; }
  public static ulong IncrementCheckedULong(ulong a) { checked { a++; ++a; } return a; }
  public static ulong DecrementCheckedULong(ulong a) { checked { a--; --a; } return a; }
  public static ulong UncheckedInsideCheckedULong(ulong a, ulong b) => checked(unchecked(a + b) - b);

  public static float AddFloat(float a, float b) => a + b;
  public static float SubtractFloat(float a, float b) => a - b;
  public static float MultiplyFloat(float a, float b) => a * b;
  public static float DivideFloat(float a, float b) => a / b;
  public static float RemainderFloat(float a, float b) => a % b;
  public static bool EqualFloat(float a, float b) => a == b;
  public static bool NotEqualFloat(float a, float b) => a != b;
  public static bool LessFloat(float a, float b) => a < b;
  public static bool LessOrEqualFloat(float a, float b) => a <= b;
  public static bool GreaterFloat(float a, float b) => a > b;
  public static bool GreaterOrEqualFloat(float a, float b) => a >= b;
  public static float PlusFloat(float a) => +a;
  public static float NegateFloat(float a) => -a;
  public static float AddAssignFloat(float a, float b) { a += b; return a; }
  public static float SubtractAssignFloat(float a, float b) { a -= b; return a; }
  public static float MultiplyAssignFloat(float a, float b) { a *= b; return a; }
  public static float DivideAssignFloat(float a, float b) { a /= b; return a; }
  public static float RemainderAssignFloat(float a, float b) { a %= b; return a; }
  public static float IncrementFloat(float a) { a++; ++a; return a; }
  public static float DecrementFloat(float a) { a--; --a; return a; }
  public static float ChooseFloat(bool first, float a, float b) => first ? a : b;
  public static float AddCheckedFloat(float a, float b) => checked(a + b);
  public static float SubtractCheckedFloat(float a, float b) => checked(a - b);
  public static float MultiplyCheckedFloat(float a, float b) => checked(a * b);
  public static float NegateCheckedFloat(float a) => checked(-a);
  public static float AddAssignCheckedFloat(float a, float b) { checked { a += b; } return a; }
  public static float SubtractAssignCheckedFloat(float a, float b) { checked { a -= b; } return a; }
  public static float MultiplyAssignCheckedFloat(float a, float b) { checked { a *= b; } return a; }
  public static float IncrementCheckedFloat(float a) { checked { a++; ++a; } return a; }
  public static float DecrementCheckedFloat(float a) { checked { a--; --a; } return a; }
  public static float UncheckedInsideCheckedFloat(float a, float b) => checked(unchecked(a + b) - b);

  public static double AddDouble(double a, double b) => a + b;
  public static double SubtractDouble(double a, double b) => a - b;
  public static double MultiplyDouble(double a, double b) => a * b;
  public static double DivideDouble(double a, double b) => a / b;
  public static double RemainderDouble(double a, double b) => a % b;
  public static bool EqualDouble(double a, double b) => a == b;
  public static bool NotEqualDouble(double a, double b) => a != b;
  public static bool LessDouble(double a, double b) => a < b;
  public static bool LessOrEqualDouble(double a, double b) => a <= b;
  public static bool GreaterDouble(double a, double b) => a > b;
  public static bool GreaterOrEqualDouble(double a, double b) => a >= b;
  public static double PlusDouble(double a) => +a;
  public static double NegateDouble(double a) => -a;
  public static double AddAssignDouble(double a, double b) { a += b; return a; }
  public static double SubtractAssignDouble(double a, double b) { a -= b; return a; }
  public static double MultiplyAssignDouble(double a, double b) { a *= b; return a; }
  public static double DivideAssignDouble(double a, double b) { a /= b; return a; }
  public static double RemainderAssignDouble(double a, double b) { a %= b; return a; }
  public static double IncrementDouble(double a) { a++; ++a; return a; }
  public static double DecrementDouble(double a) { a--; --a; return a; }
  public static double ChooseDouble(bool first, double a, double b) => first ? a : b;
  public static double AddCheckedDouble(double a, double b) => checked(a + b);
  public static double SubtractCheckedDouble(double a, double b) => checked(a - b);
  public static double MultiplyCheckedDouble(double a, double b) => checked(a * b);
  public static double NegateCheckedDouble(double a) => checked(-a);
  public static double AddAssignCheckedDouble(double a, double b) { checked { a += b; } return a; }
  public static double SubtractAssignCheckedDouble(double a, double b) { checked { a -= b; } return a; }
  public static double MultiplyAssignCheckedDouble(double a, double b) { checked { a *= b; } return a; }
  public static double IncrementCheckedDouble(double a) { checked { a++; ++a; } return a; }
  public static double DecrementCheckedDouble(double a) { checked { a--; --a; } return a; }
  public static double UncheckedInsideCheckedDouble(double a, double b) => checked(unchecked(a + b) - b);

  // uint with int is computed in 64 bits, and so is the negation of a uint.
  public static long AddUIntInt(uint a, int b) => a + b;
  public static long MultiplyIntUInt(int a, uint b) => a * b;
  public static bool LessUIntInt(uint a, int b) => a < b;
  public static long NegateUInt(uint a) => -a;
  public static long AddLongInt(long a, int b) => a + b;
  public static ulong AddULongUInt(ulong a, uint b) => a + b;
  public static int AddSByteByte(sbyte a, byte b) => a + b;
  public static long ShiftLongBySByte(long a, sbyte count) => a << count;

  // Constants the compiler folds, and the conversions it applies to them.
  public static ulong MaskLong(ulong a) => a & 0xFF00FF00FF00FF00;
  public static long SmallestLong() => long.MinValue;
  public static ulong LargestULong() => ulong.MaxValue;
  public static long WidenedConstant(long a) => a + 1;
  public static ulong UnsignedConstant(ulong a) => a * 0x100000001B3UL + 1UL;
  public static short SmallestShort() => short.MinValue;
  public static sbyte SmallestSByte() => sbyte.MinValue;
  public static char Letter() => 'A';
  public static bool IsDigit(char c) => c >= '0' && c <= '9';
  public static int DigitValue(char c) => c - '0';

  // Floating point: mixed operands, comparisons with NaN, constants the compiler folds.
  public static double AddFloatDouble(float a, double b) => a + b;
  public static float AddIntFloat(int a, float b) => a + b;
  public static double AddLongDouble(long a, double b) => a + b;
  public static float MultiplyAdd(float a, float b) => a * b + a;
  public static double MultiplyAddDouble(double a, double b) => a * b + a;
  public static bool IsNaN(double a) { double b = a; return a != b; }
  public static bool NotLess(double a, double b) => !(a < b);
  public static double Third() => 1.0 / 3.0;
  public static float ThirdFloat() => 1f / 3f;
  public static double NotANumber() => double.NaN;
  public static double Infinite() => double.NegativeInfinity;
  public static float InfiniteFloat() => float.PositiveInfinity;
  public static double NegativeZero() => -0.0;
  public static double Largest() => double.MaxValue;
  public static float SmallestFloat() => float.Epsilon;
  public static double Scale(double a) => a * 1e-7 + 1e21;
  public static float Tenth(float a) => a * 0.1f;

}
