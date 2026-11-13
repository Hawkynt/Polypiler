# O0249 — Lower On Error modes and Resume to explicit per-statement recovery edges

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#763](https://github.com/Hawkynt/Polypiler/issues/763). Once implemented it is switched off with `--disable-opt O0249`.

## What it does

PRD §30.4 (ERR-001): `On Error Resume Next` resumes at the next *statement* with an observable `Err`; "this
is not equivalent to a single try/catch wrapped around a whole function, nor to blindly ignoring all
errors". §30.1 gives the fallback: explicit error state and per-statement recovery edges. C, C#, Rust and
Perl have no such mode, so every statement of an on-error procedure needs its own edge to the place the
dialect resumes at.

## Example

### Before

```
func @f(%z: i32) -> i32 errors=on-error {
entry: OnError.ResumeNext
       Stmt.Begin #1
       %q = SDiv.i32[trap DivideByZero] 10, %z          ; a = 10 \ z
       Store.i32 @a, %q
       Stmt.Begin #2
       Store.i32 @b, (Err.Number.i32)                    ; b = ERR
       Stmt.Begin #3
       Store.i32 @c, 7
       Ret.i32 0 }
```

### After

```
func @f(%z: i32) -> i32 {                                ; mode folded to resume-next, no handler
s1:    %bad = ICmp.eq.i32 %z, 0
       Br %bad, r1, ok1
ok1:   %q = SDiv.i32[wrap] 10, %z
       Store.i32 @a, %q
       Br s2
r1:    Store.i32 @ply_err, 11                            ; DivideByZero in dialect pb35
       Br s2                                             ; statement after #1
s2:    Store.i32 @b, (Load.i32 @ply_err)
       Br s3
s3:    Store.i32 @c, 7
       Ret.i32 0 }
```

## When it applies

(1) The recovery edge leaves exactly at the trapping instruction: effects of
statement k before it are kept, those after it in statement k are not executed. (2) "The following
statement" is the next `Stmt.Begin` ordinal in source order ([#148](https://github.com/Hawkynt/Polypiler/issues/148)'s `StatementMap`), also
when statement k is the last of a loop body or branch arm. (3) Every trap condition of the instruction's
contract is tested, as O0078 requires. (4) The error number is cleared exactly at the points the function's dialect lists in `errClearedBy` ([#148](https://github.com/Hawkynt/Polypiler/issues/148)): for `vb6` and `pb35` each `OnError.*`, each `Resume.*`, `Err.Clear` and procedure exit; for `vbnet` the same without procedure exit, so a caller can still read the number after the call returns. (5) Folding a state variable requires that one mode operation
dominates every statement and no other mode operation exists. (6) While `active`, an error returns to the
caller.

## How it is proven

`structural+effect-facts`: recovery edges encode the dialect's resume rule over the
statement map; validated by interpreting the on-error form and the lowered form with a failure forced in
every statement in turn.

## When it must not fire

- One handler around the whole procedure: after the error in statement 1, statements 2 and 3 are skipped;
  the source runs them (b = 11, c = 7).
- Resuming at the next *instruction*: the store of the failed quotient to `a` would run with an undefined
  value.
- Ignoring the error without setting the number: `b` would be 0 instead of 11.
- Leaving the number set after `RESUME NEXT`: a later `IF ERR` in the source sees 0.

## Switching it off

`--disable-opt O0249`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(error-resume)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs before [#497](https://github.com/Hawkynt/Polypiler/issues/497) (the calls it tests use the status convention) and after
inlining decisions; the resulting control flow is ordinary and feeds jump threading.
