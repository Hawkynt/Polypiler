; Tree-IR fixture of DeadFunctionSteps: global initializers admit pure factory calls.
global table: fn() -> i32 = factory()
func main() -> i32 export { return invoke table() }
func factory() -> fn() -> i32 { return FuncRef handler }
func handler() -> i32 { return 7 }
