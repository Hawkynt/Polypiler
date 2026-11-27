// One function per construct of the subset the Go frontend imports, each with the arguments the
// differential test of the Go layer calls it with; Go itself runs this file as the oracle. Written for
// this repository.
package constructs

import (
	"math"
	"math/bits"
)

type Pair struct {
	A int16
	B uint16
}

type Celsius float64

type Code int32

const (
	Red Code = iota
	Green
	Blue
)

var counter int32

var table = []uint32{1, 2, 3, 5, 8}

var greeting = "hi"

func NarrowSigned(a, b int8) int8 {
	c := a*b - a
	c |= b
	c ^= a
	return -c + ^b
}

func NarrowUnsigned(a, b uint16) uint16 {
	c := a / (b | 1)
	d := a % (b | 1)
	return -(c + d) &^ 0xF0
}

func Wide(a int64, b uint64) uint64 {
	return uint64(a)*b + uint64(a/3) - uint64(a%5)
}

func ShiftCounts(x int16, n int8, m uint32) int16 {
	return x<<n>>m + x<<20 + x>>40
}

func ShiftLong(x uint64, n uint8) uint64 {
	return x<<n | x>>(n+1) | x<<63 | x>>64
}

func Conversions(x float32, y int16) float64 {
	a := float64(x) + float64(y)
	b := float32(a)
	c := uint8(y) + uint8(int64(a))
	return float64(b) + float64(c) + float64(Celsius(a))
}

func Truncations(x float64) uint32 {
	return uint32(x) + uint32(int8(x))
}

func Floats(x, y float64) float64 {
	return math.Sqrt(math.Abs(x)) + math.Floor(y) + math.Ceil(x) + math.Trunc(y) + math.RoundToEven(x) + math.Round(y) +
		math.Min(x, y) + math.Max(x, y) + math.Mod(x, y+3) + math.Copysign(x, -1) + math.FMA(x, y, 1) + math.Cbrt(x) + math.Hypot(x, y)
}

func Transcendental(x float64) float64 {
	return math.Sin(x) + math.Cos(x) + math.Tan(x) + math.Atan(x) + math.Atan2(x, 2) + math.Exp(x/10) + math.Log(math.Abs(x)+1) + math.Pow(2, x/8) +
		math.Sinh(x/10) + math.Cosh(x/10) + math.Tanh(x) + math.Exp2(x/10) + math.Expm1(x/10) + math.Log2(math.Abs(x)+1) + math.Log10(math.Abs(x)+1) +
		math.Log1p(math.Abs(x)) + math.Asin(x/100) + math.Acos(x/100)
}

func FloatBits(x float64, f float32) uint64 {
	b := math.Float64bits(x)
	g := math.Float32frombits(math.Float32bits(f) ^ 1)
	h := math.Float64frombits(b ^ 1)
	if math.IsNaN(h) || math.IsNaN(float64(g)) {
		return 0
	}
	return b + uint64(math.Float32bits(g))
}

func Specials(sign int) float64 {
	inf := math.Inf(sign)
	nan := math.NaN()
	if nan != nan {
		return inf
	}
	return 0
}

func Bits(x uint32, y uint64, z uint16, w uint8) int {
	return bits.OnesCount32(x) + bits.LeadingZeros64(y) + bits.TrailingZeros16(z) + bits.Len32(x) + bits.Len8(w) +
		int(bits.ReverseBytes16(z)) + int(bits.Reverse8(w)) + int(bits.RotateLeft(uint(x), -3)&0xFF) + int(bits.ReverseBytes64(y)&1) + int(bits.Reverse32(x)&1) +
		bits.OnesCount(uint(y)) + bits.LeadingZeros8(w) + bits.TrailingZeros(uint(x)|1)
}

func Comparisons(a, b uint8, c, d string, e, f bool) int {
	n := 0
	if a < b {
		n++
	}
	if a >= b || c == d {
		n += 2
	}
	if c != d && e == f {
		n += 4
	}
	if !e || f != e {
		n += 8
	}
	if c+d == "ab" {
		n += 16
	}
	return n
}

func Structs(a int16, b uint16) int32 {
	p := Pair{a, b}
	q := Pair{B: b}
	r := Pair{}
	r.A = a + 1
	q.A -= 2
	if p == q || p != r {
		return int32(p.A) + int32(q.A) + int32(r.A)
	}
	return 0
}

func Globals(n int32) int32 {
	counter += n
	counter++
	table[1] = uint32(counter)
	return counter + int32(table[1]) + int32(len(table))
}

func Slices(n int32) int32 {
	s := make([]int32, n)
	var t []int32
	u := []int32{}
	w := []int32{4, 5}
	for i := range s {
		s[i] = int32(i)
	}
	s[0] += 10
	s[1]++
	w[0], w[1] = w[1], w[0]
	var x, y int32 = 1, 2
	x, y = y, x
	return int32(len(s)+len(t)+len(u)+cap(w)) + w[0] + s[0] + s[1] + x*10 + y
}

func Indexes(s []byte, i uint32, j int8, k uint64, l int) byte {
	return s[i] + s[j] + s[k] + s[l] + s[2]
}

func Loops(n int) int {
	total := 0
	for i := 0; i < n; i += 2 {
		total += i
	}
	var j int
	for j = 0; j < 3; j++ {
		total++
	}
	for range 2 {
		total += 100
	}
	var k int
	for k = range 3 {
		total += k
	}
	values := []int{7, 8, 9}
	var v int
	for _, v = range values {
		total += v
	}
	for _, w := range values {
		total -= w
	}
	for total > 1000 {
		total /= 2
	}
	for {
		total--
		if total%7 == 0 {
			break
		}
	}
outer:
	for i := 0; i < 5; i++ {
		if i == 1 {
			continue outer
		}
		if i == 3 {
			break outer
		}
		total += i
	}
	return total + j
}

func Switches(x int32, code int32) int32 {
	c := Code(code)
	switch y := x * 2; y {
	case 2:
		return 1
	case 4, 6:
		x++
	}
	switch {
	case x > 10:
		return 2
	case x < 0:
		break
	default:
		x += 3
	}
	switch c {
	case Red:
		x += 100
	case Green, Blue:
		x += 200
	}
	switch z := x; z {
	case x * 2:
		return 3
	}
	return x
}

func Strings(n int) string {
	s := "a\tb\x41é\101" + `raw\n`
	if n > 0 {
		s += greeting
	}
	return s
}

func Minimum(a, b, c int32) int32 {
	return min(a, b, c) + max(a, 1)
}

func Unsigned(x uint32) uint32 {
	return -x + ^x
}

func Blank(x int32) int32 {
	_ = x * 2
	var _ int32 = x
	y, _ := x, x+1
	return y
}

func Constants() float64 {
	var f float64 = 3
	const c = 1e3
	var i int = c
	var u uint64 = 1 << 63
	var g float32 = 0.1
	var b = true
	n := 0
	if b {
		n = 1
	}
	return f + float64(i) + float64(u>>60) + float64(g) + float64(n)
}

func Escapes() string {
	return "\a\b\f\v\\\"\x7f​é😀"
}

func Unary(a float64, b int64, c bool) float64 {
	if !c {
		return +a
	}
	return -a + float64(^b) + float64(-b)
}
