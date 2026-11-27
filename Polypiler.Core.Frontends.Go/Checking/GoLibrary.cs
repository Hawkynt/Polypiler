namespace Polypiler.Core.Frontends.Go.Checking;

/// <summary>
/// The packages of the standard library the frontend type-checks against, as Go declarations without
/// bodies: the exported constants of <c>math</c> and <c>math/bits</c> exactly as the Go distribution
/// declares them (BSD-3-Clause, see <c>THIRD-PARTY-NOTICE.Go.txt</c>) and the signatures of their
/// exported functions. The checker reads them as it reads a source.
/// </summary>
internal static class GoLibrary {

  /// <summary>The declarations of <c>math</c>.</summary>
  public const string Math = """
    package math
    const (
      E   = 2.71828182845904523536028747135266249775724709369995957496696763
      Pi  = 3.14159265358979323846264338327950288419716939937510582097494459
      Phi = 1.61803398874989484820458683436563811772030917980576286213544862
      Sqrt2   = 1.41421356237309504880168872420969807856967187537694807317667974
      SqrtE   = 1.64872127070012814684865078781416357165377610071014801157507931
      SqrtPi  = 1.77245385090551602729816748334114518279754945612238712821380779
      SqrtPhi = 1.27201964951406896425242246173749149171560804184009624861664038
      Ln2    = 0.693147180559945309417232121458176568075500134360255254120680009
      Log2E  = 1 / Ln2
      Ln10   = 2.30258509299404568401799145468436420760110148862877297603332790
      Log10E = 1 / Ln10
    )
    const (
      MaxFloat32             = 0x1p127 * (1 + (1 - 0x1p-23))
      SmallestNonzeroFloat32 = 0x1p-126 * 0x1p-23
      MaxFloat64             = 0x1p1023 * (1 + (1 - 0x1p-52))
      SmallestNonzeroFloat64 = 0x1p-1022 * 0x1p-52
    )
    const (
      intSize = 32 << (^uint(0) >> 63)
      MaxInt    = 1<<(intSize-1) - 1
      MinInt    = -1 << (intSize - 1)
      MaxInt8   = 1<<7 - 1
      MinInt8   = -1 << 7
      MaxInt16  = 1<<15 - 1
      MinInt16  = -1 << 15
      MaxInt32  = 1<<31 - 1
      MinInt32  = -1 << 31
      MaxInt64  = 1<<63 - 1
      MinInt64  = -1 << 63
      MaxUint   = 1<<intSize - 1
      MaxUint8  = 1<<8 - 1
      MaxUint16 = 1<<16 - 1
      MaxUint32 = 1<<32 - 1
      MaxUint64 = 1<<64 - 1
    )
    func Abs(x float64) float64
    func Acos(x float64) float64
    func Acosh(x float64) float64
    func Asin(x float64) float64
    func Asinh(x float64) float64
    func Atan(x float64) float64
    func Atan2(y, x float64) float64
    func Atanh(x float64) float64
    func Cbrt(x float64) float64
    func Ceil(x float64) float64
    func Copysign(f, sign float64) float64
    func Cos(x float64) float64
    func Cosh(x float64) float64
    func Dim(x, y float64) float64
    func Erf(x float64) float64
    func Erfc(x float64) float64
    func Erfcinv(x float64) float64
    func Erfinv(x float64) float64
    func Exp(x float64) float64
    func Exp2(x float64) float64
    func Expm1(x float64) float64
    func FMA(x, y, z float64) float64
    func Float32bits(f float32) uint32
    func Float32frombits(b uint32) float32
    func Float64bits(f float64) uint64
    func Float64frombits(b uint64) float64
    func Floor(x float64) float64
    func Frexp(f float64) (frac float64, exp int)
    func Gamma(x float64) float64
    func Hypot(p, q float64) float64
    func Ilogb(x float64) int
    func Inf(sign int) float64
    func IsInf(f float64, sign int) bool
    func IsNaN(f float64) (is bool)
    func J0(x float64) float64
    func J1(x float64) float64
    func Jn(n int, x float64) float64
    func Ldexp(frac float64, exp int) float64
    func Lgamma(x float64) (lgamma float64, sign int)
    func Log(x float64) float64
    func Log10(x float64) float64
    func Log1p(x float64) float64
    func Log2(x float64) float64
    func Logb(x float64) float64
    func Max(x, y float64) float64
    func Min(x, y float64) float64
    func Mod(x, y float64) float64
    func Modf(f float64) (integer float64, fractional float64)
    func NaN() float64
    func Nextafter(x, y float64) (r float64)
    func Nextafter32(x, y float32) (r float32)
    func Pow(x, y float64) float64
    func Pow10(n int) float64
    func Remainder(x, y float64) float64
    func Round(x float64) float64
    func RoundToEven(x float64) float64
    func Signbit(x float64) bool
    func Sin(x float64) float64
    func Sincos(x float64) (sin, cos float64)
    func Sinh(x float64) float64
    func Sqrt(x float64) float64
    func Tan(x float64) float64
    func Tanh(x float64) float64
    func Trunc(x float64) float64
    func Y0(x float64) float64
    func Y1(x float64) float64
    func Yn(n int, x float64) float64
    """;

  /// <summary>The declarations of <c>math/bits</c>.</summary>
  public const string Bits = """
    package bits
    const uintSize = 32 << (^uint(0) >> 63)
    const UintSize = uintSize
    func Add(x, y, carry uint) (sum, carryOut uint)
    func Add32(x, y, carry uint32) (sum, carryOut uint32)
    func Add64(x, y, carry uint64) (sum, carryOut uint64)
    func Div(hi, lo, y uint) (quo, rem uint)
    func Div32(hi, lo, y uint32) (quo, rem uint32)
    func Div64(hi, lo, y uint64) (quo, rem uint64)
    func LeadingZeros(x uint) int
    func LeadingZeros16(x uint16) int
    func LeadingZeros32(x uint32) int
    func LeadingZeros64(x uint64) int
    func LeadingZeros8(x uint8) int
    func Len(x uint) int
    func Len16(x uint16) (n int)
    func Len32(x uint32) (n int)
    func Len64(x uint64) (n int)
    func Len8(x uint8) int
    func Mul(x, y uint) (hi, lo uint)
    func Mul32(x, y uint32) (hi, lo uint32)
    func Mul64(x, y uint64) (hi, lo uint64)
    func OnesCount(x uint) int
    func OnesCount16(x uint16) int
    func OnesCount32(x uint32) int
    func OnesCount64(x uint64) int
    func OnesCount8(x uint8) int
    func Rem(hi, lo, y uint) uint
    func Rem32(hi, lo, y uint32) uint32
    func Rem64(hi, lo, y uint64) uint64
    func Reverse(x uint) uint
    func Reverse16(x uint16) uint16
    func Reverse32(x uint32) uint32
    func Reverse64(x uint64) uint64
    func Reverse8(x uint8) uint8
    func ReverseBytes(x uint) uint
    func ReverseBytes16(x uint16) uint16
    func ReverseBytes32(x uint32) uint32
    func ReverseBytes64(x uint64) uint64
    func RotateLeft(x uint, k int) uint
    func RotateLeft16(x uint16, k int) uint16
    func RotateLeft32(x uint32, k int) uint32
    func RotateLeft64(x uint64, k int) uint64
    func RotateLeft8(x uint8, k int) uint8
    func Sub(x, y, borrow uint) (diff, borrowOut uint)
    func Sub32(x, y, borrow uint32) (diff, borrowOut uint32)
    func Sub64(x, y, borrow uint64) (diff, borrowOut uint64)
    func TrailingZeros(x uint) int
    func TrailingZeros16(x uint16) int
    func TrailingZeros32(x uint32) int
    func TrailingZeros64(x uint64) int
    func TrailingZeros8(x uint8) int
    """;

}
