# O0078 — Lower exceptions and traps for targets without exception handling

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#497](https://github.com/Hawkynt/Polypiler/issues/497). Once implemented it is switched off with `--disable-opt O0078`.

## What it does

C, classic BASIC, assembly and several embedded profiles have no exceptions, yet the sources translated to
them do: C# `checked` arithmetic, array bounds, `try`/`catch`/`finally`, user exceptions. PRD §17.2 lists
`Trap`/`Exception` as an effect with "cause, condition, ordering, handler state — never silently erase
observable failures", and §21.3 lists `Exceptions` as a capability. Lowering must keep which failure
occurs, after which effects, which handler runs, and that `finally` code runs — using only branches and
return values.

## Example

### Before

```
@f(%a: i32, %b: i32) -> i32:
  %q = SDiv.i32[checked] %a, %b              // may raise DivideByZero
  Ret.i32 %q
@g(%a: i32, %b: i32) -> i32:
  Try { %r = Call @f(%a, %b) ; Ret %r }
  Catch DivideByZero { Ret -1 }
```

### After

```
@f(%a: i32, %b: i32) -> (status: u16, value: i32):
  %z = ICmp.eq.i32 %b, 0
  Br %z, raise, ok
raise: Ret (1, 0)                            // status 1 = DivideByZero
ok:    %q = SDiv.i32[wrap] %a, %b            // divisor proven non-zero here; MinValue / -1 handled by a second test
       Ret (0, %q)
@g(%a: i32, %b: i32) -> (status: u16, value: i32):
  %t = Call.i32 @f(%a, %b)
  %s = Extract %t, status
  %h = ICmp.eq.u16 %s, 1
  Br %h, handler, cont
handler: Ret (0, -1)
cont:    %n = ICmp.ne.u16 %s, 0
         Br %n, propagate, done
propagate: Ret (%s, 0)
done:      Ret (0, (Extract %t, value))
```

## When it applies

(1) A status test is inserted immediately after every instruction or call that may raise ([#80](https://github.com/Hawkynt/Polypiler/issues/80)
`MayTrap`, [#115](https://github.com/Hawkynt/Polypiler/issues/115) raise sites); no instruction with an effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) executes between the
raise point and the transfer to the handler, so the effect log up to the failure is unchanged. (2) An
implicit trap becomes its condition test **before** the operation, with the operation relaxed to the
non-trapping contract on the continuing path; every trap condition of the contract is tested (`SDiv`:
divisor zero, and `MinValue / −1` for `[checked]`). (3) Handler selection: handlers of a `Try` are tested
in source order against the status code; a handler's label list is an explicit set of codes — for a typed
handler the set of the type and its descendants, expanded beforehand by [#758](https://github.com/Hawkynt/Polypiler/issues/758) — and is lowered
to a membership test (equality for one code, a compare chain or range test for several). This rule never
consults the type hierarchy; a handler whose label is still an exception type with descendants (O0247
disabled) is PLY0570 with the cause `typed handler not expanded`. (4) `finally`: executed
exactly once on each exit path — normal, handled, propagated, and early `Ret` inside the region — before
control leaves the region. (5) Propagation returns the status unchanged; values of the failed call are not
read. (6) Status codes: 0 = success; 1 DivideByZero, 2 Overflow, 3 IndexOutOfRange, 4 NullReference, 5
OutOfMemory, 6 ContractViolation, 7 InvalidCast, 8 Unreachable, 9 LengthMismatch, 10 EmptySequence, 11
ShapeMismatch, 12 OverlappingCopy, 13 InvalidOperation, 14 AssertionFailed, 15 StackOverflow — every trap
kind of `spec/ir/traps.yaml` ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) has exactly one code; `User` exception types get codes from 256 upward in
ordinal order of their fully qualified names, which is the order of `ExceptionTypeTable`
([#149](https://github.com/Hawkynt/Polypiler/issues/149)). (7) Exception data is not lowered here: the payload record and its accesses belong to
[#758](https://github.com/Hawkynt/Polypiler/issues/758). This rule guarantees only that nothing it inserts between a raise and the handler
entry writes the payload region, and it refuses a handler that reads exception data when the profile has
`exceptionPayload: none` (PLY0571). (8) Functions proven not to raise keep
their signature. (9) At the program entry an uncaught status calls `ply_trap(status)`; at an exported
boundary the profile's `uncaughtPolicy` (`terminate` or `return-status`) applies. (10) `Trap.Unreachable`
(control reached a point the IR declares unreachable) is a raise site like any other: it produces status 8
and is never deleted or turned into fall-through. (11) Contract of `ply_trap(code)`, normative for every
target's variant of the helper: write the line `ply_trap <code>` (decimal, followed by a newline) to
standard error, flush it, and end the process with exit status `100 + (code mod 100)`; it never returns.
**What the runner observes:** `RunOutcome.Completed` with that exit status and `ply_trap <code>` as the
last line of standard error; the language runner (`RUN-…`) maps the code back to the trap name through
`StatusCodeTable` and reports the tuple as `!<TrapName>`. The exit status alone is ambiguous for user
codes (256 and 356 both give 156), so the line is authoritative. A target without a process exit status
or standard error names its channel in the profile key `trapChannel` (for example a memory cell and a
halt instruction); observing that channel is the job of that target's runner.

## How it is proven

`structural+effect-facts`: status propagation is a continuation-passing encoding of the exception edges already explicit in the IR; the placement rule (1) keeps the effect order. Validated by the raise-at-every-site measurement. No solver, no timeout path.

## When it must not fire

- Testing the status only at the end of a block: `Call.void @f(); Call.void @log()` — if `@f` fails, `@log` must not run.
- Relaxing `SDiv[checked]` after testing only for zero: `−2147483648 / −1` still overflows.
- A `finally` block skipped on the propagation path, or run twice when the handler rethrows.
- A handler whose label set is `{256, 257}` lowered as a test for 256 only: status 257 escapes the handler it belongs to.
- Reading the value half of a failed call's result as if it were valid.
- Turning an uncaught trap into `return 0`.

## Switching it off

`--disable-opt O0078`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259) after all other legalization rules (their helpers may raise) and before the final [#156](https://github.com/Hawkynt/Polypiler/issues/156); `ply_trap` joins the closure of [#260](https://github.com/Hawkynt/Polypiler/issues/260); [#159](https://github.com/Hawkynt/Polypiler/issues/159)'s exception-region nesting is a precondition.
