package refused

func F() {
	defer F()
}
