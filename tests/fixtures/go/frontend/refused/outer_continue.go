package refused

func F() {
outer:
	for {
		for {
			continue outer
		}
	}
}
