# Readable tree sketch (ADR 0009), constructed by DeadGlobalSteps and DeadGlobalEliminationTests.
global limit: i32 = 320
global table: u16[] = [1, 2, 3, 4]
global ops: fn() -> i32 = factory()
export main() -> i32 { return limit; }
private Unused() -> u16 { return table[0]; }
private factory() -> fn() -> i32 { return FuncRef area; }
private area() -> i32 { return 7; }
# O0381+O0383: only main and limit. O0383 alone: every function, limit and table.
