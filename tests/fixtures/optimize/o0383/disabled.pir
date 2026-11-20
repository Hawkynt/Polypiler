# Readable tree sketch (ADR 0009).
global ops: fn() -> i32 = factory()
export main() -> i32 { return 0; }
private factory() -> fn() -> i32 { return FuncRef area; }
private area() -> i32 { return 7; }
# --disable-opt O0383: every global initializer remains a root of O0381.
