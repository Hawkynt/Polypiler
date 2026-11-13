namespace Polypiler.Prototype.Testing;

/// <summary>
/// What the C# compiler makes of <c>a + b</c> for every pair of numeric types it accepts: the boxed result
/// carries the type the compiler promoted the operands to. The pairs it rejects, <c>ulong</c> with a signed
/// integer, are absent.
/// </summary>
internal static class PromotionOracle {

  public static object SByteSByte(sbyte a, sbyte b) => a + b;
  public static object SByteByte(sbyte a, byte b) => a + b;
  public static object SByteShort(sbyte a, short b) => a + b;
  public static object SByteUShort(sbyte a, ushort b) => a + b;
  public static object SByteChar(sbyte a, char b) => a + b;
  public static object SByteInt(sbyte a, int b) => a + b;
  public static object SByteUInt(sbyte a, uint b) => a + b;
  public static object SByteLong(sbyte a, long b) => a + b;
  public static object ByteSByte(byte a, sbyte b) => a + b;
  public static object ByteByte(byte a, byte b) => a + b;
  public static object ByteShort(byte a, short b) => a + b;
  public static object ByteUShort(byte a, ushort b) => a + b;
  public static object ByteChar(byte a, char b) => a + b;
  public static object ByteInt(byte a, int b) => a + b;
  public static object ByteUInt(byte a, uint b) => a + b;
  public static object ByteLong(byte a, long b) => a + b;
  public static object ByteULong(byte a, ulong b) => a + b;
  public static object ShortSByte(short a, sbyte b) => a + b;
  public static object ShortByte(short a, byte b) => a + b;
  public static object ShortShort(short a, short b) => a + b;
  public static object ShortUShort(short a, ushort b) => a + b;
  public static object ShortChar(short a, char b) => a + b;
  public static object ShortInt(short a, int b) => a + b;
  public static object ShortUInt(short a, uint b) => a + b;
  public static object ShortLong(short a, long b) => a + b;
  public static object UShortSByte(ushort a, sbyte b) => a + b;
  public static object UShortByte(ushort a, byte b) => a + b;
  public static object UShortShort(ushort a, short b) => a + b;
  public static object UShortUShort(ushort a, ushort b) => a + b;
  public static object UShortChar(ushort a, char b) => a + b;
  public static object UShortInt(ushort a, int b) => a + b;
  public static object UShortUInt(ushort a, uint b) => a + b;
  public static object UShortLong(ushort a, long b) => a + b;
  public static object UShortULong(ushort a, ulong b) => a + b;
  public static object CharSByte(char a, sbyte b) => a + b;
  public static object CharByte(char a, byte b) => a + b;
  public static object CharShort(char a, short b) => a + b;
  public static object CharUShort(char a, ushort b) => a + b;
  public static object CharChar(char a, char b) => a + b;
  public static object CharInt(char a, int b) => a + b;
  public static object CharUInt(char a, uint b) => a + b;
  public static object CharLong(char a, long b) => a + b;
  public static object CharULong(char a, ulong b) => a + b;
  public static object IntSByte(int a, sbyte b) => a + b;
  public static object IntByte(int a, byte b) => a + b;
  public static object IntShort(int a, short b) => a + b;
  public static object IntUShort(int a, ushort b) => a + b;
  public static object IntChar(int a, char b) => a + b;
  public static object IntInt(int a, int b) => a + b;
  public static object IntUInt(int a, uint b) => a + b;
  public static object IntLong(int a, long b) => a + b;
  public static object UIntSByte(uint a, sbyte b) => a + b;
  public static object UIntByte(uint a, byte b) => a + b;
  public static object UIntShort(uint a, short b) => a + b;
  public static object UIntUShort(uint a, ushort b) => a + b;
  public static object UIntChar(uint a, char b) => a + b;
  public static object UIntInt(uint a, int b) => a + b;
  public static object UIntUInt(uint a, uint b) => a + b;
  public static object UIntLong(uint a, long b) => a + b;
  public static object UIntULong(uint a, ulong b) => a + b;
  public static object LongSByte(long a, sbyte b) => a + b;
  public static object LongByte(long a, byte b) => a + b;
  public static object LongShort(long a, short b) => a + b;
  public static object LongUShort(long a, ushort b) => a + b;
  public static object LongChar(long a, char b) => a + b;
  public static object LongInt(long a, int b) => a + b;
  public static object LongUInt(long a, uint b) => a + b;
  public static object LongLong(long a, long b) => a + b;
  public static object ULongByte(ulong a, byte b) => a + b;
  public static object ULongUShort(ulong a, ushort b) => a + b;
  public static object ULongChar(ulong a, char b) => a + b;
  public static object ULongUInt(ulong a, uint b) => a + b;
  public static object ULongULong(ulong a, ulong b) => a + b;

}
