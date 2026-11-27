package refused

func F(x int32) int32 {
	switch x {
	case 1:
		fallthrough
	default:
		return 2
	}
}
