// Forms of the subset the Go frontend lowers beyond the checksums: unnamed parameters, blocks, if with
// an init statement and else-if chains, calls as statements, struct places in multiple assignments,
// float arithmetic and the fused-multiply-add caveat, every index width, globals without a value. Written
// for this repository.
package forms

type Pair struct {
	A int32
	B int32
}

var count int32

var unset float64

func Ignore(int32, int32) int32 { return 7 }

func Swap(p Pair) Pair {
	p.A, p.B = p.B, p.A
	return p
}

func Grade(x int32) int32 {
	if y := x * 2; y > 10 {
		return 3
	} else if y > 4 {
		return 2
	} else {
		return 1
	}
}

func Touch(p Pair) {}

func Calls(x int32) int32 {
	Touch(Pair{})
	{
		x++
	}
	return x + count
}

func Floats(a, b, c float64) float64 {
	return a*b + c - a*b*c - unset
}

func Pick(s []int32, i int32, j uint8, k int16, l uint16, m uint32) int32 {
	return s[i] + s[j] + s[k] + s[l] + s[m]
}
