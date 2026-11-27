package refused

func F(s string) int32 {
	var n int32
	for range s {
		n++
	}
	return n
}
