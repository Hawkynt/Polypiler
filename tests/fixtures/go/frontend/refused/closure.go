package refused

func F() func() int32 {
	return func() int32 { return 1 }
}
