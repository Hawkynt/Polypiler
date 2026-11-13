// One function per operation of the base library that has an exact meaning, and per type it is defined
// on: Math and MathF, BitOperations, BitConverter and the constants of the numeric types. Written for
// this repository. The mixing step at the end uses the constants of MurmurHash3 (Appleby).
using System;
using System.Numerics;

namespace Polypiler.Fixtures.Prototype;

public static class Library {

  public static sbyte AbsSByte(sbyte a) => Math.Abs(a);
  public static short AbsShort(short a) => Math.Abs(a);
  public static int AbsInt(int a) => Math.Abs(a);
  public static long AbsLong(long a) => Math.Abs(a);
  public static float AbsFloat(float a) => Math.Abs(a);
  public static double AbsDouble(double a) => Math.Abs(a);
  public static float AbsSingle(float a) => MathF.Abs(a);
  public static long AbsOfUInt(uint a) => Math.Abs(a);

  public static sbyte MinSByte(sbyte a, sbyte b) => Math.Min(a, b);
  public static byte MinByte(byte a, byte b) => Math.Min(a, b);
  public static short MinShort(short a, short b) => Math.Min(a, b);
  public static ushort MinUShort(ushort a, ushort b) => Math.Min(a, b);
  public static int MinChar(char a, char b) => Math.Min(a, b);
  public static int MinInt(int a, int b) => Math.Min(a, b);
  public static uint MinUInt(uint a, uint b) => Math.Min(a, b);
  public static long MinLong(long a, long b) => Math.Min(a, b);
  public static ulong MinULong(ulong a, ulong b) => Math.Min(a, b);
  public static float MinFloat(float a, float b) => Math.Min(a, b);
  public static double MinDouble(double a, double b) => Math.Min(a, b);
  public static float MinSingle(float a, float b) => MathF.Min(a, b);
  public static long MinIntLong(int a, long b) => Math.Min(a, b);

  public static sbyte MaxSByte(sbyte a, sbyte b) => Math.Max(a, b);
  public static byte MaxByte(byte a, byte b) => Math.Max(a, b);
  public static short MaxShort(short a, short b) => Math.Max(a, b);
  public static ushort MaxUShort(ushort a, ushort b) => Math.Max(a, b);
  public static int MaxInt(int a, int b) => Math.Max(a, b);
  public static uint MaxUInt(uint a, uint b) => Math.Max(a, b);
  public static long MaxLong(long a, long b) => Math.Max(a, b);
  public static ulong MaxULong(ulong a, ulong b) => Math.Max(a, b);
  public static float MaxFloat(float a, float b) => Math.Max(a, b);
  public static double MaxDouble(double a, double b) => Math.Max(a, b);
  public static float MaxSingle(float a, float b) => MathF.Max(a, b);
  public static double MaxFloatDouble(float a, double b) => Math.Max(a, b);

  public static int SignSByte(sbyte a) => Math.Sign(a);
  public static int SignShort(short a) => Math.Sign(a);
  public static int SignInt(int a) => Math.Sign(a);
  public static int SignLong(long a) => Math.Sign(a);
  public static int SignFloat(float a) => Math.Sign(a);
  public static int SignDouble(double a) => Math.Sign(a);
  public static int SignSingle(float a) => MathF.Sign(a);
  public static int SignOfUInt(uint a) => Math.Sign(a);

  public static double SqrtDouble(double a) => Math.Sqrt(a);
  public static float SqrtSingle(float a) => MathF.Sqrt(a);
  public static double SqrtOfFloat(float a) => Math.Sqrt(a);
  public static double FloorDouble(double a) => Math.Floor(a);
  public static float FloorSingle(float a) => MathF.Floor(a);
  public static double CeilingDouble(double a) => Math.Ceiling(a);
  public static float CeilingSingle(float a) => MathF.Ceiling(a);
  public static double TruncateDouble(double a) => Math.Truncate(a);
  public static float TruncateSingle(float a) => MathF.Truncate(a);
  public static double RoundDouble(double a) => Math.Round(a);
  public static float RoundSingle(float a) => MathF.Round(a);
  public static double RoundToEvenDouble(double a) => Math.Round(a, MidpointRounding.ToEven);
  public static double RoundAwayDouble(double a) => Math.Round(a, MidpointRounding.AwayFromZero);
  public static float RoundAwaySingle(float a) => MathF.Round(a, MidpointRounding.AwayFromZero);
  public static int RoundToInt(double a) => (int)Math.Round(a);

  public static uint RotateLeftUInt(uint a, int count) => BitOperations.RotateLeft(a, count);
  public static uint RotateRightUInt(uint a, int count) => BitOperations.RotateRight(a, count);
  public static ulong RotateLeftULong(ulong a, int count) => BitOperations.RotateLeft(a, count);
  public static ulong RotateRightULong(ulong a, int count) => BitOperations.RotateRight(a, count);
  public static int PopCountUInt(uint a) => BitOperations.PopCount(a);
  public static int PopCountULong(ulong a) => BitOperations.PopCount(a);
  public static int LeadingZeroCountUInt(uint a) => BitOperations.LeadingZeroCount(a);
  public static int LeadingZeroCountULong(ulong a) => BitOperations.LeadingZeroCount(a);
  public static int TrailingZeroCountUInt(uint a) => BitOperations.TrailingZeroCount(a);
  public static int TrailingZeroCountULong(ulong a) => BitOperations.TrailingZeroCount(a);
  public static int Log2UInt(uint a) => BitOperations.Log2(a);
  public static int Log2ULong(ulong a) => BitOperations.Log2(a);
  public static ulong BigMul(uint a, uint b) => Math.BigMul(a, b);

  public static long DoubleBits(double a) => BitConverter.DoubleToInt64Bits(a);
  public static int SingleBits(float a) => BitConverter.SingleToInt32Bits(a);
  public static double DoubleOfBits(long a) => BitConverter.Int64BitsToDouble(a);
  public static float SingleOfBits(int a) => BitConverter.Int32BitsToSingle(a);

  // The limits of the numeric types and the constants of Math are constants of the compiler.
  public static long IntegerLimits() => sbyte.MinValue + byte.MaxValue + short.MinValue + ushort.MaxValue + char.MaxValue + (long)int.MinValue + uint.MaxValue;
  public static ulong WideLimits(ulong a) => a ^ ulong.MaxValue ^ (ulong)long.MaxValue ^ unchecked((ulong)long.MinValue);
  public static double FloatLimits(double a) => a + double.Epsilon + float.MaxValue + float.Epsilon - double.MaxValue + Math.PI + Math.E;
  public static bool IsLargest(double a) => a == double.MaxValue || a == double.MinValue || a == float.MinValue;

  // Operations combined, as the code that uses them combines them.
  public static uint Mix(uint hash, uint block) {
    block *= 0xCC9E2D51;
    block = BitOperations.RotateLeft(block, 15);
    block *= 0x1B873593;
    hash ^= block;
    hash = BitOperations.RotateLeft(hash, 13);
    return hash * 5 + 0xE6546B64;
  }

  public static double Hypotenuse(double a, double b) => Math.Sqrt(a * a + b * b);
  public static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(value, high));
  public static bool IsPowerOfTwo(ulong a) => BitOperations.PopCount(a) == 1;
  public static int BitLength(ulong a) => 64 - BitOperations.LeadingZeroCount(a);
  public static float NextUp(float a) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(Math.Abs(a)) + 1);

}
