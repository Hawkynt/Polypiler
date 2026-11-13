package main

import (
	"fmt"
	"math"
)

func t(f func() any) (s string) {
	defer func() {
		if r := recover(); r != nil {
			s = fmt.Sprint("panic(", r, ")")
		}
	}()
	return fmt.Sprint(f())
}

func main() {
	var max, min, one, zero, m1 int32 = math.MaxInt32, math.MinInt32, 1, 0, -1
	var s32, s33 uint = 32, 33
	big, nan, inf := 3e9, math.NaN(), math.Inf(1)
	fmt.Println("add:", max+one, " sub:", min-one, " mul:", int32(65536)*(one*65536), " neg:", -min)
	fmt.Println("1 << 32:", one<<s32, " 1 << 33:", one<<s33, " -8 >> 33:", int32(-8)>>s33, " uint32(8) >> 33:", uint32(8)>>s33)
	fmt.Println("1 << -1:", t(func() any { return one << m1 }))
	fmt.Println("7 / 0:", t(func() any { return 7 / zero }), " 7 % 0:", t(func() any { return 7 % zero }))
	fmt.Println("MIN / -1:", t(func() any { return min / m1 }), " MIN % -1:", t(func() any { return min % m1 }))
	fmt.Println("-7 / 3:", -7/(one*3), " -7 % 3:", -7%(one*3), " 7 % -3:", 7%(m1*3))
	fmt.Println("int32(3e9):", int32(big), " int32(NaN):", int32(nan), " int32(Inf):", int32(inf), "(implementation-specific)")
}
