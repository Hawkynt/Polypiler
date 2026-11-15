# The Rust backend

**Status:** implemented for scalar code ([#461](https://github.com/Hawkynt/Polypiler/issues/461),
[#458](https://github.com/Hawkynt/Polypiler/issues/458), [#459](https://github.com/Hawkynt/Polypiler/issues/459));
arrays and slices, structs and enums, closures and `Result` follow in phase 2. The runner that builds
and executes the output is [#468](https://github.com/Hawkynt/Polypiler/issues/468).
Related: [backends](BACKENDS.md) · [pipeline](PIPELINE.md) · [exceptions](../reference/exceptions.md) ·
[integer operations](../reference/integer-operations.md).

`polypiler translate <file> --from <csharp|javascript> --to rust --output <dir>` writes a Cargo package:

| File | What it holds |
| --- | --- |
| `Cargo.toml` | `[package]` with `name` (the module's name in `snake_case`), `version = "0.1.0"`, the `edition` and, from Rust 1.56, `rust-version`; no dependencies |
| `src/lib.rs` | the header (`//!`: profile, helper licences, floating-point record, caveats, notices), `#![forbid(unsafe_code)]`, the lints the code needs allowed, `pub mod <module>;` |
| `src/<module>.rs` | the constants, the spelling functions and helpers, the functions; a `#[cfg(test)] mod tests` when tests are asked for |

The backend builds a Rust syntax tree from the IR (`Polypiler.Core.Backends.Rust/Syntax/RustSyntax.cs`),
runs the target passes over it - helper inlining (O0500), removal of what nothing names (O0501) -
and prints it with `RustPrinter`, which writes what `rustfmt` writes in its default configuration.
`RustSyntaxReader` reads every generated file back into the tree for the parse-back sweep;
`RustSyntaxRewriter` walks and rewrites it; `RustLiveness` finds the values a function stores and
never reads.

## Profiles

`--profile` names the edition, `--rust-version` raises the minimum supported Rust version (MSRV)
above the release that stabilized it:

| Profile | Edition | MSRV by default |
| --- | --- | --- |
| `rust2018` | 2018 | 1.31 |
| `rust2021` | 2021 | 1.56 |
| `rust2024` (default) | 2024 | 1.85 |

The edition decides the keywords (`async`, `await`, `dyn`, `try` from 2018, `gen` from 2024), which a
name escapes as a raw identifier (`r#gen`). The MSRV decides the rows of `RustCapabilities`:

| Row | Since | Without it |
| --- | --- | --- |
| `checked_abs`, `wrapping_abs` | 1.13 | the helper of the library |
| `to_bits`, `from_bits` | 1.20 | refused (`PLY1550`) |
| `TryFrom` (checked narrowing) | 1.34 | the spelling function `ply_i2i_trap_<from>_<to>`, a range test through `i128` |
| `copysign` | 1.35 | the portable implementation |
| `reverse_bits` | 1.37 | the helper of the library |
| `is_subnormal` | 1.53 | the portable implementation |
| `rust-version` key of `Cargo.toml` | 1.56 | left out |
| labelled blocks (`label_break_value`) | 1.65 | a `continue` in a `do`-`while` and a `break` inside a switch section are refused (`PLY1550`) |
| `round_ties_even` | 1.77 | the helper `RoundEven` of the library |

## Lowering

| IR | Rust |
| --- | --- |
| `Add`, `Subtract`, `Multiply` of integers | `a.wrapping_add(b)`, `wrapping_sub`, `wrapping_mul` - never a bare `+`, whose meaning depends on `overflow-checks` |
| `AddChecked`, `SubtractChecked`, `MultiplyChecked`; `NegateChecked` | `a.checked_add(b).expect("IntegerOverflow")`; `a.checked_neg().expect("IntegerOverflow")` |
| `Divide`, `Remainder` of integers | `ply_div_<t>(a, b)`, `ply_rem_<t>(a, b)`: `panic!("DivideByZero")` on 0, then `checked_div`/`checked_rem` with `IntegerOverflow`; `a / 7` where the divisor is a constant other than 0 and -1 |
| `ShiftLeft`, `ShiftRight`, `ShiftRightLogical` | `a.wrapping_shl(n as u32)`, `wrapping_shr`, which mask the count as the IR does; a logical shift of a signed value `(a as u32).wrapping_shr(n as u32) as i32` |
| `Negate`, `BitNot`, `LogicalNot` | `a.wrapping_neg()` (`-a` for floating point), `!a`, `!c` - a negated comparison of integers turned around (`a >= b`) |
| arithmetic and comparisons of floating point | the operators; a compound assignment where clippy's `assign_op_pattern` asks |
| conversions between integers | `i64::from(a)` where every value fits, `a as u8` wrapping, `u8::try_from(a).expect("IntegerOverflow")` trapping |
| conversions to and from floating point | `a as f64`, `f64::from(a)` for `f32` to `f64`, `a as i32` saturating (Rust's `as`), `ply_f2i_trap_<t>(a)` trapping |
| intrinsics with a method | `rotate_left`, `count_ones`, `leading_zeros`, `trailing_zeros`, `swap_bytes`, `reverse_bits`, `is_power_of_two`, `min`, `max`, `saturating_*`, `checked_abs`, `wrapping_abs`, `sqrt`, `floor`, `ceil`, `trunc`, `round`, `round_ties_even`, `abs`, `mul_add`, `copysign`, `is_nan`, `is_infinite`, `is_finite`, `is_sign_negative`, `is_normal`, `is_subnormal`, `to_bits`, `from_bits` |
| every other intrinsic | the helper of the IR library (`IntegerLibrary`, `MinimumDouble`, `SignOf`, `RoundHalfUp`, `ToInt32`, `Clamp`, …) or the portable floating-point implementation, copied into the module |
| `if`, `while`, `do`-`while`, `for` | `if`/`else if`, `while c` (`loop` for `while true`), `loop { …; if !c { break; } }`, a `while` whose step precedes every `continue`; a counted loop `for i in a..b` where its counter and bound allow |
| `switch` | `match` with or-patterns and ranges of three or more consecutive labels, `_` last; one section an `if`; a `break` inside a section leaves `'s<n>: { match … }` |
| `return` | the tail of the function, `return v;` elsewhere |
| a value assigned in every branch | `let x = if c { a } else { b };` (clippy's `needless_late_init`) |
| a throw of a library exception | `panic!("<IrTrap>")` |
| `try`/`catch` | refused, `PLY1560` |
| a read-only table | `const NAME: [u32; N] = […];`, read as `NAME[i as usize]` |

A trap is a panic whose message is the name of the IR trap - `IntegerOverflow`, `DivideByZero`,
`ArgumentOutOfRange`, … - in every build mode; the verification reads it back with `RustTraps`
([exceptions](../reference/exceptions.md#rust)).

## Refused

What the backend does not write yet is refused with `PLY1550`, naming the construct: arrays, views
and pointers (`arrays and views`), strings (`the type str`), structs, classes and enums (`declared
types`), module state (`module state (globals)`), closures, collections and sequences. Two exported
functions whose Rust names are the same are `PLY1574`; an exception handler `PLY1560`.

## Lints

`src/lib.rs` allows exactly the lints the module has a shape for, from a closed list:
`unused_assignments` (a value of the source stored and never read, found by `RustLiveness`),
`unused_comparisons`, `clippy::absurd_extreme_comparisons`, `clippy::approx_constant`,
`clippy::empty_loop`, `clippy::eq_op`, `clippy::erasing_op`, `clippy::identity_op`,
`clippy::manual_clamp` and `clippy::min_max` (a minimum and a maximum whose bounds may be out of
order, where `clamp` would panic), `clippy::manual_range_contains`, `clippy::neg_cmp_op_on_partial_ord`
and `clippy::nonminimal_bool` (a negated ordering of floating-point numbers), `clippy::too_many_arguments`.
No group is ever allowed. Every other lint is avoided by the shape the backend writes.

## Verification

- `RustScalarEmitTests`, `RustControlEmitTests`, `RustPackageEmitTests`, `RustSyntaxTests`,
  `RustProfileTests`: the emitted text per row, the printer's layout rules, the parse-back sweep over
  every prototype fixture.
- `RustDifferentialTests` (layer `rust`): every prototype fixture the backend writes is built by the
  pinned toolchain and called on every case of `PrototypeCases`, with overflow checks on and off; every
  outcome equals the C# compiler's and the interpreter's. The others are refused by name.
- `RustBackendScenarioTests` (layer `rust`): the scenarios of the issues with their hand-checked
  values, clippy with `-D warnings` and `cargo fmt --check` on the crates, the generated tests passing
  and a mutated one failing.

The runner (`Polypiler.Core.Verification/Runners`): `RustRunner` builds (`cargo build`), tests
(`cargo test`), format-checks (`cargo fmt --check`), lints (`cargo clippy -- -D warnings`) and calls
exports through a generated driver (`RustDriverGenerator`) on input images, with `RustBuildOptions`
(`OverflowChecks`, `DebugAssertions`, `OptLevel`) that have no defaults. Diagnostics: `PLY1591` the
compiler rejected the input, `PLY1592` the driver died, `PLY1595` a protocol error such as a signature
that does not fit the declaration.
