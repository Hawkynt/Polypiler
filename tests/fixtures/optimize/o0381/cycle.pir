; Tree-IR fixture of DeadFunctionSteps: Trace models the observable print(1) write.
global trace: i32 = 0
func main() -> i32 export {
  Used()
  return trace
}
func Used() -> void { trace = 1; return }
func Unused() -> void { AlsoUnused(); return }
func AlsoUnused() -> void { Unused(); return }
