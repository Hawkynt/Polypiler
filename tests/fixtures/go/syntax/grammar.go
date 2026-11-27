// Every production of the Go grammar the frontend's parser has a path for: generics with type sets,
// interfaces, channels, select, type switches, labels, every literal and statement form. It only has
// to parse - the Go layer checks that go/parser accepts it too. Written for this repository.

//go:build go1.24

package grammar

import (
	"math"
	m "math/bits"
	_ "unsafe"
)

import . "strings"

const N = 4

type (
	Array  [N * 2]int
	Open   [N]*int
	Matrix [N][N]float64
	Ptr    *Array
	Fn     func(int, ...string) (bool, error)
	Fn2    func(a, b int) (c int)
	Ch     chan<- <-chan int
	Ch2    <-chan chan int
	Map    map[string][]int
	Alias  = Map
	Paren  (int)
	S      struct {
		A, B int `tag:"x"`
		*Embedded
		math.Embedded2
		Field  [N]int
		Inst   G[int]
		Inst2  [2]G[int]
		_      struct{}
		H[int, string]
		G[int]
	}
	G[P any]                                   struct{ v P }
	H[P, Q any]                                struct{}
	K[P interface{ ~int | ~string }]           []P
	L[P *int]                                  struct{}
	L2[P *int | *string]                       struct{}
	L3[P (int)]                                struct{}
	L4[P ~int, Q comparable]                   struct{}
	L5[P interface{ M() }, R []P]              struct{}
	L6[P interface{ int | string }]            int
	L7[P *int, Q any]                          struct{}
	L8[P *int | string, Q any]                 struct{}
	L9[P (int), Q any]                         struct{}
	LA[P *[]int]                               struct{}
	LB[P int | string]                         struct{}
	GA[P any]                                  = G[P]
	I                                          interface {
		M(int) string
		N()
		error
		~int | float64
		I2[int]
		struct{}
		I3[int, string]
		m.Embedded
		m.Generic[int]
	}
	I2[T any] interface{ Get() T }
	I3[T, U any] interface{}
	E interface{}
)

var (
	x, y    = 1, 2
	z       int
	f       = func(a int) int { return a }
	g       Fn
	chans   = make(chan int, 1)
	lit     = []S{{A: 1}, {}}
	nested  = [][]int{{1, 2}, {3}}
	keyed   = map[string]G[int]{"a": {v: 1}}
	arr     = [...]string{2: "b", "c"}
	ptrs    = []*S{{A: 1}}
	inst    = Map2[string, int]{}
	call    = Make[int](1, 2)
	slice3  = arr[1:2:3]
	slice2  = arr[:2]
	slice1  = arr[1:]
	slice0  = arr[:]
	assert  = any(1).(int)
	conv    = (*int)(nil)
	conv2   = []byte("x")
	unary   = -^+!true
	addr    = &S{}
	deref   = *addr
	recv    = <-chans
	funcLit = func() {}
	literal = 'a' + 1.5e3 + 0x1p2 + 2i + 0o7
	str     = "a" + `b`
	fnConv  = (func())(nil)
)

func Qualified(m.Builder) {}

func Unnamed(int, string) {}

func Union[P int | string]() {}

type Map2[K comparable, V any] map[K]V

func Make[T any](a, b T) []T { return []T{a, b} }

func (s *S) Method(a int, b ...string) (n int, err error) {
	return a, nil
}

func (G[P]) Get() (p P) { return }

func (h *H[P, Q]) Two() {}

func generic[T interface{ ~int }, U any](t T, u ...U) T { return t }

func Statements(c chan int, v any, s []int) (result int) {
	var a, b int = 1, 2
	const k, l = 1, 2
	type local struct{ x int }
	a, b = b, a
	a += 1
	a <<= 1
	a &^= 3
	a++
	b--
	c <- a
	_ = <-c
	v2, ok := <-c
	_, _ = v2, ok
	go func() {}()
	defer func() { recover() }()
	if a := 1; a > 0 {
	} else if b > 0 {
	} else {
	}
	for {
		break
	}
	for a < 10 {
		a++
	}
	for i := 0; i < 3; i++ {
		continue
	}
	for ; ; a++ {
		break
	}
	for range c {
	}
	for i := range s {
		_ = i
	}
	for i, x := range s {
		_, _ = i, x
	}
	for a, b = range s {
	}
	switch {
	case a > 0:
		fallthrough
	default:
	}
	switch x := a; x {
	case 1, 2:
	}
	switch a := 1; {
	}
	switch t := v.(type) {
	case int, string:
		_ = t
	case nil:
	}
	switch v.(type) {
	}
	select {
	case a = <-c:
	case a, ok := <-c:
		_ = ok
	case c <- 1:
	case <-c:
	default:
	}
outer:
	for {
		goto next
	next:
		break outer
	}
	{
		;
	}
	;
	empty:
	return
}
