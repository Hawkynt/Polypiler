# O0247 — Lower typed handlers, filters, exception data and rethrow to status-code form

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#758](https://github.com/Hawkynt/Polypiler/issues/758). Once implemented it is switched off with `--disable-opt O0247`.

## What it does

[#497](https://github.com/Hawkynt/Polypiler/issues/497) (O0078) turns raise sites and handlers into status results and tests, and it expects handler
selection "through the type table" and exception data in a payload slot — both of which it leaves to this
area. This rule supplies them: it expands a handler for a base type into the set of derived types, lowers
filters with their timing, stores and reads exception fields, and keeps a rethrown exception intact. PRD
§30.1 row "Exceptions/filters": handler selection, propagation and failure order are the semantics; explicit
handler control flow and error results the fallback.

## Example

### Before

```
exceptions { ArithmeticException : Exception; DivideByZeroException : ArithmeticException = DivideByZero; AppError : Exception { code: i32 } }
body: try(h)
       %e0 = Exception.New.exception AppError(code: %c)
       Throw %e0
h:     %e = Landing.exception [AppError] when λ(%x): ICmp.eq.i32 (Exception.Field.i32 %x, code), 7
       Ret.i32 1
```

### After

```
region @ply_exc_payload: ExcPayload [zero]               ; record { AppError_code: i32 }
body: try(h)
       Store.i32 (FieldAddr.ptr<i32> @ply_exc_payload, AppError_code), %c
       Trap.User [code=256]                              ; status of AppError in O0078's table
h:     %e = Landing.exception [256]
       %k = Load.i32 (FieldAddr.ptr<i32> @ply_exc_payload, AppError_code)
       %f = ICmp.eq.i32 %k, 7
       Br %f, take, pass
take:  Ret.i32 1
pass:  Resume %e
```

## When it applies

(1) The expanded label set of a handler equals `Descendants(T)` plus T;
handlers keep their source order. (2) The payload is written before the raise and not written again until
the handler that takes the exception has read what it needs; a nested raise inside a handler saves and
restores the payload it still reads. (3) A filter is evaluated exactly once per handler considered, reads
only, and under `before-unwind` runs before every cleanup between raise and handler. (4) A filter result
of false leaves status and payload unchanged. (5) The target profile provides payload storage
(`exceptionPayload: slot`); without it, handlers that read fields are refused by O0078's own diagnostic.

## How it is proven

`structural+effect-facts`: handler sets come from the type table; the before-unwind
condition rests on effect facts for the intervening cleanups; validated by interpreting the typed form and
the lowered form on every fixture.

## When it must not fire

- Matching `catch (ArithmeticException)` by the code of `ArithmeticException` alone: `10 / 0` raises
  `DivideByZeroException`, a different code, and escapes.
- Evaluating a `before-unwind` filter after an inner `finally` that changes the state the filter reads: C#
  sees the state before the `finally`.
- Treating a false filter as handled: `Classify(8)` must reach the second handler and return 8.
- Raising a second exception inside a handler and then reading the first exception's field from the shared
  slot.

## Switching it off

`--disable-opt O0247`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(exception-hierarchy)`. Disabled, a typed handler reaches O0078 unexpanded and that rule reports `PLY0570` (`typed handler not expanded`).

## Interactions with other passes

runs immediately before [#497](https://github.com/Hawkynt/Polypiler/issues/497) (O0078) in phase `legalize`; shares the
status-code table; [#757](https://github.com/Hawkynt/Polypiler/issues/757) is the alternative for the propagation half.
