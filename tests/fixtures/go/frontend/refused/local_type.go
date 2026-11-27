package refused

func F() {
	type T int32
	var t T
	_ = t
}
