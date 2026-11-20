# Readable tree sketch (ADR 0009); host/profile supplies GlobalRoots by name.
global external: i32 = 1
global device: i32 = 2
export main() -> i32 { return 0; }
# Retaining either named global keeps it; the other, unreferenced global disappears.
