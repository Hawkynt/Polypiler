# ADR 0008 — Traps in generated Rust: a trap policy of panics or error values

Area: foundation

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-16 |
| Issue | [#1392](https://github.com/Hawkynt/Polypiler/issues/1392), with the `result` parts of [#461](https://github.com/Hawkynt/Polypiler/issues/461) and [#462](https://github.com/Hawkynt/Polypiler/issues/462) |
| Requirements | [PRD §3.2](../prd/01-product-and-targets.md#s3-2) ("exception … lowering", "idiomatic module/public API") |

## Question

An IR trap (an overflow, a division by zero, an index outside an array, a field read through null, …) is a
`panic!` named after the trap in the Rust the backend writes. An exception of a class of the module is an
`Rc<PlyError>` handed back through `Result` and `?`. A `catch` of a class a trap belongs to therefore never sees
the trap, and the backend refuses it with `PLY1560` (`exceptions/Traps`). How should a trap reach a handler in
safe Rust, and how does a crate report a trap to a caller that does not want a panic in its process?

## Options

| Option | What the generated Rust does | Consequences |
| --- | --- | --- |
| A. `Result` only where a handler stands | every operation that may trap returns `Result` inside the functions between the trap and a handler; elsewhere a trap stays a panic | The shape of a function depends on who calls it; a function called both inside and outside a `try` needs two forms; a library user still gets panics from every export. |
| B. `std::panic::catch_unwind` at the handler | the handler is a `catch_unwind` around the `try` body; the trap stays a panic | Needs `panic = "unwind"` (fails silently under `panic = "abort"`), `UnwindSafe` bounds that `Rc`/`Cell` state does not meet without `AssertUnwindSafe`, a panic hook to silence the message, and is control flow no Rust code is written with; clippy and reviewers flag it. |
| C. A trap policy, `--trap-policy panic\|result` | `panic` (the default): today's output, unchanged. `result`: every function a trap can leave returns `Result<T, E>`, every trapping operation is an explicit check that hands the trap's error on, and a `catch` sees a trap like any exception | One crate-wide decision the user makes once; idiomatic in both forms; the cost of `result` (wider signatures, a branch per check) is paid only where asked for. |

## Decision

**C.** `polypiler translate … --to rust --trap-policy result` (CLI), `RustProfile.WithTrapPolicy(RustTrapPolicy.Result)`
(API). `panic` stays the default and its output does not change. Under `result`:

1. **Fallibility is a fixed point over the calls.** A function returns `Result<T, E>` when a trap, a throw or a call
   of such a function can leave it; the emitter finds the set by building the crate again with the functions that
   let an error out declared fallible until no new one does. A function nothing can leave keeps its plain type
   (`fn add(a: i32, b: i32) -> i32` beside `fn checked_sum(…) -> Result<i32, Error>`). Exports are included: the
   public API reports errors instead of panicking.
2. **Every trap is an explicit check.** `a.checked_add(b).ok_or(Error::IntegerOverflow)?`,
   `if b == 0 { return Err(Error::DivideByZero); }` (in the division's spelling function, which returns a `Result`),
   `usize::try_from(n).map_err(|_| Error::IntegerOverflow)?`, `*a.get(i).ok_or(Error::IndexOutOfRange)?` for an
   element read and `*a.get_mut(i).ok_or(…)? = v` for one written, `x.as_ref().ok_or(Error::NullReference)?` for an
   object that may be null. Inside a `try` body the same checks are a `match` whose empty arm leaves the body:
   `None => break 't0 ply_trap(4)`.
3. **The error type.** A module without exception classes and without catch clauses gets `src/error.rs`, re-exported
   from `src/lib.rs` as `pub use error::Error;`: `#[derive(Clone, Copy, Debug, PartialEq, Eq)] #[repr(u16)] pub enum Error`
   with one variant per trap the crate can produce, named after the IR trap, `Display` printing that name and
   `impl std::error::Error`. The discriminants are stable across crates: `DivideByZero = 1`, `IntegerOverflow = 2`,
   `IndexOutOfRange = 3`, then the other traps from 4 in the order of `IrTrap`. A module with exception classes or a
   catch clause uses its exceptions instead (`Rc<PlyError>`, the backend's form since #462): a trap is the exception
   of its class with the class's default message (`ply_trap(class)`), so `catch (OverflowException)` and
   `catch (ArithmeticException)` select it as .NET does; `Display` of an exception prints its trap's name where its
   class is a trap's, else the class's name. The class numbers play the role of the codes #462 reserved from 256 for
   user exception types.
4. **Tests.** A `#[test]` of a fallible function asserts the `Result`: `assert_eq!(checked_sum(1, 2, 0), Err(Error::DivideByZero));`
   instead of `#[should_panic(expected = "DivideByZero")]`, `Ok(5)` for a value.
5. **`PLY1552` names what `result` cannot write without a panic:** a call of a helper of the string and collection
   library that panics (`ply_list_index`, `ply_map_add`, `ply_parse_i32`, …, each named), a trap inside a closure
   (a function value cannot hand an error on through `Rc<dyn Fn>`), a step of an element of module state behind a
   lock, and a type of the module named `Error`.
6. `#![forbid(unsafe_code)]` stays, and no generated crate uses `catch_unwind`.

The exception regions `PLY1560` refuses under both policies stay refused by this decision: a filter with a
`finally` between it and the throw (.NET runs the filter first, `exceptions/order`), a `try` with both catch clauses
and a `finally`, a `finally` around a body that may throw or complete, and a jump out of a `try`. Catching a trap is
lifted under `result`; under `panic` it stays refused, since a panic reaches no clause.

## Evidence

The Rust layer (`RustTrapPolicyTests`, `RustFamilyDifferentialTests`) builds each crate with the pinned toolchain,
with overflow checks on and off, passes `cargo fmt --check` and `cargo clippy --all-targets -- -D warnings`, and
compares every call with .NET: `exceptions/Traps` (every trap of the IR caught by the clause of its class) agrees
under `result` with no panic, and the prototype fixtures written under both policies agree under each, the outcomes
under `result` read from the `Err` an export returns. `checked_sum(7, 3, 2)` is `Ok(5)`,
`checked_sum(2147483647, 1, 1)` is `Err(Error::IntegerOverflow)`, `checked_sum(1, 2, 0)` is
`Err(Error::DivideByZero)`, and under `panic` the same IR keeps `fn checked_sum(a: i32, b: i32, c: i32) -> i32`.

## Consequences

- The verification driver calls an export that returns a `Result` through its `Ok`, and reports an `Err` as `?`
  with what the error displays (`RustExport.Fallible`); a panic under `result` is a test failure.
- The `Error` variants are named after the IR's traps (`IntegerOverflow`), not after #461's draft (`Overflow`), so a
  variant and the panic message of the same trap under `panic` are the same word.
- The helpers that signal traps by panicking are refused by name until they return a `Result` of their own; each one
  converted shrinks `PLY1552`.
- Unwinding (option B) stays possible as a later dialect for code that must keep its plain signatures; nothing here
  depends on its absence.
