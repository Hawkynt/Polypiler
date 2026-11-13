// Evaluates the ADR 0005 input set with the Go standard library.
package main

import (
	"bufio"
	"fmt"
	"math"
	"os"
	"strconv"
	"strings"
)

func main() {
	in := bufio.NewScanner(os.Stdin)
	out := bufio.NewWriter(os.Stdout)
	defer out.Flush()
	for in.Scan() {
		parts := strings.Split(in.Text(), " ")
		var v [3]float64
		for i, h := range parts[2:] {
			b, _ := strconv.ParseUint(h, 16, 64)
			v[i] = math.Float64frombits(b)
		}
		x, y, z := v[0], v[1], v[2]
		var r float64
		switch parts[0] {
		case "sin":
			r = math.Sin(x)
		case "cos":
			r = math.Cos(x)
		case "tan":
			r = math.Tan(x)
		case "exp":
			r = math.Exp(x)
		case "log":
			r = math.Log(x)
		case "log2":
			r = math.Log2(x)
		case "log10":
			r = math.Log10(x)
		case "pow":
			r = math.Pow(x, y)
		case "atan2":
			r = math.Atan2(x, y)
		case "sinh":
			r = math.Sinh(x)
		case "cosh":
			r = math.Cosh(x)
		case "tanh":
			r = math.Tanh(x)
		case "cbrt":
			r = math.Cbrt(x)
		case "hypot":
			r = math.Hypot(x, y)
		case "add":
			r = x + y
		case "sub":
			r = x - y
		case "mul":
			r = x * y
		case "div":
			r = x / y
		case "sqrt":
			r = math.Sqrt(x)
		case "fma":
			r = math.FMA(x, y, z)
		}
		fmt.Fprintf(out, "%016x\n", math.Float64bits(r))
	}
}
