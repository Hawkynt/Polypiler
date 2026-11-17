# Readable tree sketch (ADR 0009); static initializers execute at module load.
global bad: i32 = Div.i32(1, 0)
export main() -> i32 { return 0; }
# The unread global stays: both versions trap before main can run.
