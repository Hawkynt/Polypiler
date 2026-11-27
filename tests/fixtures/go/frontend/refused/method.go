package refused

type T struct{ X int32 }

func (t T) Get() int32 {
	return t.X
}
