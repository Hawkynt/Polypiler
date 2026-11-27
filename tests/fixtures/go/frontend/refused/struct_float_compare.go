package refused

type P struct{ X float64 }

func F(a, b P) bool {
	return a == b
}
