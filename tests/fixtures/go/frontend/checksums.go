// Checksums and integer arithmetic with Go's own rules: sized integers that wrap, shifts that are
// not masked, exact untyped constants and truncating division. Written for this repository; the
// vectors Adler-32("Wikipedia") = 0x11E60398 and FNV-1a 32("a") = 0xE40C292C are the published ones.
package checksums

import "math/bits"

const (
	modAdler   = 65521
	fnvOffset  = 2166136261
	fnvPrime   = 16777619
	big        = 1 << 100
	quarter    = big >> 98
	exactSum   = 0.1 + 0.2
	notPromote = uint8(200)
)

// Adler32 is the checksum of RFC 1950.
func Adler32(data []byte) uint32 {
	a, b := uint32(1), uint32(0)
	for _, c := range data {
		a = (a + uint32(c)) % modAdler
		b = (b + a) % modAdler
	}
	return b<<16 | a
}

// Fnv1a32 is the 32-bit FNV-1a hash.
func Fnv1a32(data []byte) uint32 {
	h := uint32(fnvOffset)
	for i := 0; i < len(data); i++ {
		h ^= uint32(data[i])
		h *= fnvPrime
	}
	return h
}

func AddWrap(a, b int32) int32 {
	return a + b
}

func ByteSum(a, b uint8) uint8 {
	return a + b
}

func Promote() uint8 {
	x := notPromote
	return x + 100
}

func ShiftLeft(x uint32, n uint) uint32 {
	return x << n
}

func ShiftRight(x int32, n uint) int32 {
	return x >> n
}

func ShiftSigned(x uint32, n int) uint32 {
	return x << n
}

func Quarter() int {
	return quarter
}

func ExactSum() float64 {
	return exactSum
}

func VariableSum(a, b float64) float64 {
	return a + b
}

func Divide(a, b int32) int32 {
	return a / b
}

func Remainder(a, b int32) int32 {
	return a % b
}

func Complement(x uint8) uint8 {
	return ^x
}

func ClearBits(x, mask uint32) uint32 {
	return x &^ mask
}

func Rotate(x uint32, k int) uint32 {
	return bits.RotateLeft32(x, k)
}

func Ones(x uint64) int {
	return bits.OnesCount64(x)
}

func Truncate(x float64) int32 {
	return int32(x)
}

func Larger(a, b int64) int64 {
	return max(a, b)
}

func Classify(x int32) int32 {
	switch x {
	case 1, 7:
		return 0
	case 2, 3, 4, 5, 6:
		return x * 2
	default:
		return -1
	}
}

func Sign(x int32) int32 {
	switch {
	case x < 0:
		return -1
	case x == 0:
		return 0
	}
	return 1
}

func SumTo(n int32) int32 {
	var sum int32
	for i := int32(1); i <= n; i++ {
		sum += i
	}
	return sum
}

func CountDown(n int) int {
	steps := 0
	for n > 0 {
		n /= 2
		steps++
	}
	return steps
}

func Squares(n int) []int64 {
	out := make([]int64, n)
	for i := range n {
		out[i] = int64(i * i)
	}
	return out
}

func Fib(n int) uint32 {
	a, b := uint32(0), uint32(1)
	for range n {
		a, b = b, a+b
	}
	return a
}

func FirstAbove(values []int32, limit int32) int {
	for i, v := range values {
		if v > limit {
			return i
		}
	}
	return -1
}

type Point struct {
	X int32
	Y int32
}

func Dot(a, b Point) int32 {
	return a.X*b.X + a.Y*b.Y
}

func Moved(p Point, dx int32) Point {
	q := p
	q.X += dx
	return q
}

func Fail(code int32) int32 {
	if code < 0 {
		panic("negative code")
	}
	return code
}
