# Readable tree sketch (ADR 0009); global initializers may name earlier globals.
global inner: i32 = 7
global outer: i32 = inner
export main() -> i32 { return outer; }
# Both globals remain; main returns 7 before and after.
