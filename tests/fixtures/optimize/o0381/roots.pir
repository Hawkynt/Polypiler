; Tree-IR fixture of DeadFunctionSteps; roundtrip is an additional test entry point.
func api() -> i32 export { return 1 }
func roundtrip() -> i32 test { return helper() }
func helper() -> i32 { return 7 }
func orphan() -> i32 { return 8 }
