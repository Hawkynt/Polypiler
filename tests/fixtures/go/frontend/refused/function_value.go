package refused

func G() {
}

func F() {
	h := G
	h()
}
