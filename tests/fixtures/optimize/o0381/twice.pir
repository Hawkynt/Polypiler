; Tree-IR fixture of DeadFunctionSteps; Twice is registered as Fixture.Twice for O0006.
func main() -> i32 export { return Twice(3) + Twice(4) }
func Twice(v: i32) -> i32 { return v * 2 }
