package refused

func F() {
outer:
	for {
		for {
			break outer
		}
	}
}
