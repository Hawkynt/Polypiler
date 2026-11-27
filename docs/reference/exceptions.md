# Exceptions

Every way an IR operation fails at run time - a trap - is the throw of an object of an exception
class, one class per trap, arranged in the same hierarchy in C# and JavaScript, so that a `catch`
clause selects the same handler in the source and in every translation.
Related: [repository README](../../README.md) · [classes](classes.md) ·
[optional values](optional-values.md) · [floating-point operations](floating-point-operations.md).

## The classes

The IR's library has the exception classes of .NET, arranged as .NET arranges them
(`IrExceptionClass`); no module declares them and every module may name them:

```text
Exception
├── ArithmeticException
│   ├── DivideByZeroException
│   └── OverflowException
├── IndexOutOfRangeException
├── NullReferenceException
├── InvalidCastException
├── InvalidOperationException
├── ArgumentException
│   └── ArgumentOutOfRangeException
├── KeyNotFoundException
├── FormatException
└── AssertionException
```

`AssertionException` is the one class .NET has none of: a failed assertion of the source, Python's
`AssertionError`. No trap throws it; a frontend throws it where the source asserts. C# nests a
`public sealed class AssertionException : System.Exception` with the three constructors in a class
that names it, JavaScript declares the error class `AssertionError extends Error`, Python raises the
built-in `AssertionError`.

An exception has a `Message` (a string) and an `InnerException` (an exception or `null`), two
read-only fields of `Exception` that its constructors write: none, the message, or the message and
the inner exception.

## Traps

Every `IrTrap` is the throw of an object of its class. The interpreter's `IrTrapException` carries
that object (`IrException.Exception`, its class in `IrException.Class`). The table is read by the
tests: a trap without a row fails them, so a trap a later change introduces adds its row here.

| IR class (trap) | C# throws | C# message | JavaScript class | extends | JavaScript message |
| --- | --- | --- | --- | --- | --- |
| `DivideByZeroException` (`DivideByZero`) | `System.DivideByZeroException` | Attempted to divide by zero. | `DivideByZeroError` | `ArithmeticError` | divide by zero |
| `OverflowException` (`IntegerOverflow`) | `System.OverflowException` | Arithmetic operation resulted in an overflow. | `OverflowError` | `ArithmeticError` | integer overflow |
| `ArithmeticException` (`InvalidOperation`) | `System.ArithmeticException` | Function does not accept floating point Not-a-Number values. | `ArithmeticError` | `RangeError` | invalid operation |
| `IndexOutOfRangeException` (`IndexOutOfRange`) | `System.IndexOutOfRangeException` | Index was outside the bounds of the array. | `IndexOutOfRangeError` | `RangeError` | index out of range |
| `ArgumentOutOfRangeException` (`ArgumentOutOfRange`) | `System.ArgumentOutOfRangeException` | the parameter name, as .NET formats it | `ArgumentOutOfRangeError` | `RangeError` | argument out of range |
| `NullReferenceException` (`NullReference`) | `System.NullReferenceException` | Object reference not set to an instance of an object. | `TypeError` (native) | — | the engine's text |
| `InvalidCastException` (`InvalidCast`) | `System.InvalidCastException` | Unable to cast object of type 'A' to type 'B'. | `InvalidCastError` | `TypeError` | invalid cast |
| `InvalidOperationException` (`MissingValue`) | `System.InvalidOperationException` | Nullable object must have a value. | `InvalidOperationError` | `Error` | missing value |
| `InvalidOperationException` (`CollectionModified`) | `System.InvalidOperationException` | Collection was modified; enumeration operation may not execute. | `InvalidOperationError` | `Error` | collection modified |
| `KeyNotFoundException` (`KeyNotFound`) | `System.Collections.Generic.KeyNotFoundException` | The given key 'k' was not present in the dictionary. | `KeyNotFoundError` | `Error` | key not found |
| `ArgumentException` (`DuplicateKey`) | `System.ArgumentException` | An item with the same key has already been added. Key: k | `ArgumentError` | `Error` | duplicate key |
| `ArgumentException` (`UnknownName`) | `System.ArgumentException` | Requested value 'x' was not found. | `ArgumentError` | `Error` | unknown name |
| `FormatException` (`InvalidFormat`) | `System.FormatException` | The input string 'x' was not in a correct format. | `FormatError` | `RangeError` | invalid format |

- **JavaScript.** A generated class is declared in a module only where it is used, with the classes
  of its chain. A class keeps `RangeError` in its chain where the code generated before these classes
  threw a `RangeError`, so a caller testing `instanceof RangeError` still catches it; for that reason
  `ArgumentOutOfRangeError` extends `RangeError`, not the generated `ArgumentError` (which extends
  `Error`). From ECMAScript 2015 a class is `class X extends Base { constructor(m) { super(m);
  this.name = "X"; } }`; at ECMAScript 5 it is a constructor function that sets `name`, `message` and
  the `stack` of a native error of its root, with `X.prototype = Object.create(Base.prototype)` and
  `X.prototype.constructor = X`. Both give `String(e)` `"X: message"`.
- **`NullReferenceException`** is the engine's own `TypeError`: in code generated from typed IR only a
  member access on `null` raises one, and a check before every access would cost every program.
- **Messages of traps are no part of the cross-language contract**: a trap is compared by its class,
  each language keeps its own text. The interpreter's message is the JavaScript text.
- **`RoundDigits`** with digits outside 0…15 (0…6 for `f32`) traps with `ArgumentOutOfRange`, because
  .NET throws `ArgumentOutOfRangeException` there.

### Python

The **backend** raises the Python class of the trap's class: a built-in exception where one has
exactly the IR's meaning, and otherwise a class the module declares - only where it uses it - that
derives from the built-in exception of the nearest meaning, so that a caller still sees a
`ValueError` or a `TypeError` and an `except` clause selects what the IR's catch clause selects. A
trap is compared by its class, as everywhere; the message is the trap's own text where the backend
raises it (`missing value` and `collection modified` tell the two traps of `RuntimeError` apart),
and Python's own where Python raises (`//` and `%` by zero, an index past the end of a list, an
absent key of a `dict`).

| IR class (trap) | Python raises | derives from |
| --- | --- | --- |
| `DivideByZeroException` (`DivideByZero`) | `ZeroDivisionError` | `ArithmeticError` |
| `OverflowException` (`IntegerOverflow`) | `OverflowError` | `ArithmeticError` |
| `ArithmeticException` (`InvalidOperation`) | `ArithmeticError` | `Exception` |
| `IndexOutOfRangeException` (`IndexOutOfRange`) | `IndexError` (a negative index is checked first: Python would count it from the end) | `LookupError` |
| `ArgumentException` (`DuplicateKey`, `UnknownName`) | `ValueError` | `Exception` |
| `ArgumentOutOfRangeException` (`ArgumentOutOfRange`) | `ArgumentOutOfRangeError`, declared | `ValueError` |
| `FormatException` (`InvalidFormat`) | `FormatError`, declared | `ValueError` |
| `NullReferenceException` (`NullReference`) | `AttributeError`, or the `TypeError` Python raises for a member of `None` | `Exception` |
| `InvalidCastException` (`InvalidCast`) | `InvalidCastError`, declared | `TypeError` |
| `InvalidOperationException` (`MissingValue`, `CollectionModified`) | `RuntimeError` | `Exception` |
| `KeyNotFoundException` (`KeyNotFound`) | `KeyError` | `LookupError` |
| `AssertionException` | `AssertionError` | `Exception` |
| `Exception` | `Exception` | `BaseException` |

A class of the module that derives from an exception class is a Python class that derives from the
Python class of its base; its constructor passes the message to `super().__init__`, and the message
.NET gives where the source gives none - "Exception of type 'X' was thrown." with the name of the
object's class for `Exception`, the library class's own text otherwise. `Message` is the first
argument of the exception (`_ply_message`), `InnerException` its `__cause__`.

**Handling** (#448). `IrThrow` is `raise e`, `IrRethrow` a bare `raise`, `IrThrowExpression` the
call of `_ply_throw(e)`. `IrTry` is `try:` with `finally:` for its finally block, which Python runs on
every way out - the end, `break`, `continue`, `return` and an exception - as the IR does. Its catch
clauses are `except` clauses in order, `except Class as e:` where the clause binds the exception:
the Python classes above keep the hierarchy of the IR's library classes, so Python's first matching
clause is the IR's. Where a clause has a filter, catches `NullReferenceException` (two Python
classes, one of which an `InvalidCastError` extends) or `ArgumentException` (a `ValueError` that is no
`FormatError`), the clauses are one `except Exception as e:`
whose body tests them in order with `isinstance` and the filters and raises again what none selects.
A filter then runs after the finally blocks inside the try; a filter for which that can make a
difference (`IrExceptionFilter.MayRunLate`) is refused with `PLY1851` ("the exception filter … runs
before finally blocks"), as in JavaScript. `IrUsing` is its lowering: the resource bound, then
`try:` the body `finally:` `if r is not None: r.dispose()`.

The **frontend** (#453) imports `raise E`, `raise E("message")`, `raise e` of an exception, bare
`raise` in a handler, and `try` with `except E`, `except (A, B) as e`, bare `except`, `else` and
`finally`. A built-in exception is the class of the IR's library that stands for it; the few the
library has not are classes the module declares beside them, derived from the class Python derives
them from, so that a handler matches as in Python. A class of the module derived from one of them
(`class MyError(ValueError): pass`) is a class derived from that class. `str(e)` is the message.

| Python | IR class | handler of it also catches |
| --- | --- | --- |
| `Exception`, `BaseException` | `Exception` | every exception |
| `ArithmeticError` | `ArithmeticException` | `ZeroDivisionError`, `OverflowError` |
| `ZeroDivisionError` | `DivideByZeroException` | |
| `OverflowError` | `OverflowException` | |
| `ValueError` | `ArgumentException` | `UnicodeEncodeError`, user classes derived from it |
| `TypeError` | `InvalidCastException` | |
| `IndexError` | `IndexOutOfRangeException` | |
| `KeyError` | `KeyNotFoundException` | |
| `AssertionError` | `AssertionException` | |
| `RuntimeError` | `InvalidOperationException` | `NotImplementedError` |
| `LookupError` | declared, derived from `Exception` | `IndexError` and `KeyError` (one catch clause each) |
| `NotImplementedError` | declared, derived from `InvalidOperationException` | |
| `UnicodeEncodeError` | declared, derived from `ArgumentException` | |
| `AttributeError` | declared, derived from `Exception` | |
| `struct.error` | declared `error`, derived from `Exception` | |

The clauses of a `try` are the `except` clauses in source order, one catch clause per class a tuple
names. `else` runs after the body under a flag the body's last statement sets, outside the
protected block, so that the handlers do not catch what it raises; `finally` is the `finally` of a
`try` around both. `return`, `break` and `continue` in a `finally` block, `raise … from`, `with` and
`except*` are refused (`PLY1843`), and so is a `yield` inside a `try` with handlers, which the IR has
no form for.

An operation that fails raises the exception CPython raises, as the class of the IR that stands for
it, with CPython's message:

| Python raises | IR class | where |
| --- | --- | --- |
| `ZeroDivisionError` | `DivideByZeroException` | `/`, `//`, `%`, `divmod` by zero, `0.0` to a negative power |
| `OverflowError` | `OverflowException` | an `int` too large for a `float` or a left shift beyond 2**31 - 1 bits, `int()`/`round()` of an infinity, a float power that overflows |
| `ValueError` | `ArgumentException` | a negative shift count, `int()`/`round()` of a NaN, a zero step of `range`, a negative power of an `int` whose sign was not known |
| `AssertionError` | `AssertionException` | a failing `assert` |
| `IndexError` | `IndexOutOfRangeException` | an index outside a list, bytes, a str or a table after a negative one is counted from the end; `pop()` of an empty list |
| `ValueError` | `ArgumentException` | a zero step of a slice, a byte outside 0 to 255 stored into a `bytearray` or made into `bytes`, `chr` outside 0 to 0x10FFFF, `min`/`max` of an empty sequence, `E(v)` of a value no member has, `math.sqrt` of a negative number |
| `TypeError` | `InvalidCastException` | arithmetic on `None`, `ord` of a str that is not one code point |
| `AttributeError` | declared | an attribute of `None` |
| `UnicodeEncodeError` | declared | `str.encode` of a lone surrogate |
| `struct.error` | declared | `struct.unpack` of a buffer of the wrong length, `struct.pack` of an integer outside its field |
| `OverflowError` | `OverflowException` | `int.to_bytes` of a value that does not fit, `struct.pack('f', …)` of a float a binary32 cannot hold |
| a `complex` result | `ArithmeticException` | a negative float to a fractional power (`py-complex-power`) |

### Go

Go has no exception classes: a failure is a run-time panic, recovered by nothing in the generated
code, or an `error` value. The **backend** writes every trap as a panic and leaves the ones Go raises
itself to Go; a panic is compared by its message, without the `runtime error: ` of Go's own:

| IR class (trap) | Go panics with |
| --- | --- |
| `DivideByZeroException` (`DivideByZero`) | Go's own `integer divide by zero` of `/` and `%` |
| `OverflowException` (`IntegerOverflow`) | `"integer overflow"`: the checked arithmetic, negation and conversions and the signed division of `MinInt` by -1 (`polyrt.go`), and Go's own `makeslice: len out of range` of a negative length |
| `IndexOutOfRangeException` (`IndexOutOfRange`) | Go's own `index out of range [i] with length n` |
| `ArithmeticException` (`InvalidOperation`) | `"invalid operation"`: the sign of a NaN (`plySign`) |
| `NullReferenceException` (`NullReference`) | Go's own `invalid memory address or nil pointer dereference` |
| a thrown library exception | its message, `panic("…")` |

Handlers (`IrTry`) are not written yet (`PLY2160`); error results are #693.

The **frontend** imports `panic(s)` of a string as the throw of an `Exception` with the message `s`,
Go's `integer divide by zero` as `DivideByZero`, an index outside a slice as `IndexOutOfRange`, the
panic `negative shift amount` of a shift by a negative count as an `ArithmeticException` with that
message, and `make` of a length outside 32 bits as `IntegerOverflow`. Go's `MinInt / -1`, which wraps,
traps nowhere.

## Handling

The IR has `IrTry(Body, Catches, Finally)` with clauses `IrCatch(Class, Local?, Filter?, Body)`,
`IrThrow(e)`, `IrRethrow` (only inside a catch clause) and `IrThrowExpression(T, e)` where a value
stands. `IrThrow` states the semantics once for every implementation:

- The clauses are tested in order; a clause selects an exception that is an object of its class and
  whose filter, if any, is true. A filter runs in the first pass, **before** any `finally` block
  between the throw and the handler (ECMA-335 Partition I, 12.4.2); a filter that throws is false.
- `finally` runs on every way out - the end, `break`, `continue`, `return` (whose value is computed
  before it runs) and an exception. An exception thrown in `finally` replaces the one in flight.
  Nothing jumps out of a `finally` block (C# has no form for it: CS0157), which the verifier checks.
- `throw;` rethrows the caught object unchanged; `throw null` traps with `NullReference`.
- A class of a module derives from any library class; the library constructors are none, the message,
  and the message and the inner exception. Without a message `Exception` words one for the class of
  the object - "Exception of type 'X' was thrown." - and every other library class gives its own.

The interpreter tests the clauses in a .NET exception filter, which gives the CLR's two passes.

## Frontends

- **C#:** `try`, `catch`, `catch (T)`, `catch (T e)`, `when` filters, `finally`, `throw e`, `throw;`,
  throw expressions (`x ?? throw …`, `c ? v : throw …`), classes deriving from `Exception` or a class
  of the table with the constructors `()`, `(string)`, `(string, Exception)`, `Message` and
  `InnerException`. Any other library exception class, `ArgumentOutOfRangeException(string paramName)`
  and any other member of an exception are refused by name (`PLY1002`).
- **JavaScript:** `try`, `catch (e)`, `catch`, `finally`, `throw`, classes that `extend Error` with
  `super(m)` and `super(m, { cause })` (their `this.name = '…'` is dropped: every backend names the
  class it writes), `new Error(m)`, `new Error(m, { cause })`, `e.message`, `e.cause`, and
  `instanceof` a class of the file, `Error` (every exception), `RangeError` (the arithmetic family: in a
  JavaScript source only a `BigInt` division by zero and an invalid array length raise it) and
  `TypeError` (`NullReferenceException`: in a typed import only a member access on `null` raises it).
  A catch catches every exception into its binding and keeps the tests of its block as type tests;
  `throw e` of a binding that is never assigned is the rethrow. `return`, `break` and `continue`
  leaving a `finally` block, `instanceof` another native error and `new` of a native error other
  than `Error` are refused (`PLY1102`). A thrown value that is not an `Error` is accepted only where
  nothing observes it - a catch without a binding or with one it never reads - and is then the throw
  of an `Error`; where a catch reads it or it can leave the function it is refused with `PLY1107`
  ("a thrown value that is not an Error reaches {place}, which observes it"). A string that leaves
  the function - `throw 'Result() not implemented'` of a base class the Cipher framework leaves to
  its derived ones (#1365) - is an `Error` whose message is the string, with the caveat
  **`js-throw-string`**: JavaScript's caller receives the string itself.
- **Python:** see [Python](#python) under Traps: the exceptions of the frontend and of the backend are
  one table each there.

- **Rust:** see [Rust](#rust), which covers both directions.

## Forms per profile

| Construct | C# below 6 (`net2.0`, `net3.5`) | C# 6 (`net4.6`) | C# 7 and later |
| --- | --- | --- | --- |
| `try`/`catch`/`finally`/`throw`/`throw;`, classes, `InnerException` | native | native | native |
| filter `when (c)` | one `catch (global::System.Exception caught)` whose block tests the clauses in order (`T e = caught as T; if (e != null && c) … else throw;`) where the filter may run late; otherwise `PLY1053` | native | native |
| throw expression | helper `static T Throw<T>(global::System.Exception e) { throw e; }` | helper | native |

| Construct | ES5 | ES2015, ES2017 | ES2020 | ES2022 |
| --- | --- | --- | --- | --- |
| class of an exception | constructor function setting `message`, `stack`, `name` on an `Object.create` chain | `class X extends Base` with `super(m)`, several constructors as branches of one | same | same, `super(m, { cause })` from `Error` |
| inner exception | `this.cause = inner` | `this.cause = inner` | same | native `{ cause }` |
| catch clauses | one `catch (e)` testing `if (e instanceof A) … else if (e instanceof B) … else throw e;` in source order | same | same | same |
| catch without binding | `catch ($e)` | same | `catch { … }` (ECMAScript 2019) | same |
| filter | joined to the `instanceof` test where it may run late; otherwise `PLY1152` | same | same | same |
| `throw;` | `throw e;` of the binding | same | same | same |
| throw expression | helper `$throw(e)` | same | same | same |

A test for `NullReferenceException` is `e instanceof TypeError && !(e instanceof InvalidCastError)` in
a module that casts, `e instanceof TypeError` otherwise; one for `ArgumentException` also takes the
`ArgumentOutOfRangeError`, which extends `RangeError`. The inner exception is read as
`$cause(e)` - `null` where an error has no `cause`.

**The filter rule** (`IrExceptionFilter.MayRunLate`): a target without exception filters - C# below 6,
JavaScript at every level - can only catch first and test after, when the `finally` blocks inside the
try have already run. That gives the same result exactly when the filter has no effect, cannot trap
and reads no variable such a `finally` block writes; the clauses after it are still tried, since one
catch block tests them all in order. Any other filter is refused with "the exception filter … runs
before finally blocks; it cannot be expressed in profile '{profile}'" - `PLY1053` in C#, `PLY1152` in
JavaScript. The emulation that would catch and rethrow runs the `finally` block first ("F,W,C" in node
where .NET logs "W,F,C"), which is why it is never written.

## Resource scopes

`IrUsing(Resource, Initializer, Dispose, Body)` is C#'s `using` (ECMA-334, the using statement):
the initializer is evaluated and bound to the resource, a read-only local; the body runs; on every way
out of it - the end, `break`, `continue`, `return` (whose value is computed before) and an exception -
`Dispose()` is called through the interface of cleanup unless the resource is `null`. It is exactly
`IrUsing.Lowered()`, `{ r = e; try { body } finally { if (r != null) r.Dispose(); } }`, which the
interpreter runs, so the rules of `finally` above hold: an exception thrown by `Dispose` replaces the
one leaving the body. Several resources are scopes nested in the order they are acquired, so they are
disposed in the reverse order; when two `Dispose` calls throw, both run and the outer one's exception
leaves. The interface of cleanup is `IrDisposable`: an interface the module declares with
`IsDisposable` set and the one method `Dispose()`, which the verifier checks, as it checks that the
resource implements it and that nothing assigns the resource.

| Construct | C# 2 - 7.3 (`net2.0` … `net4.8`, `netstandard2.0`) | C# 8 and later (`netstandard2.1`, `netcoreapp3.1`, `net8.0`, `net10.0`) |
| --- | --- | --- |
| using statement, several declarators, stacked statements | native, one `using (…)` per resource, stacked where a scope holds only the next | same |
| using declaration | `using (…) { rest of the block }` | native `using var r = …;` (row `using declaration`, C# 8; CS8370 at 7.3) |
| `IDisposable` | `global::System.IDisposable`; the module declares no interface of its own | same |

| Construct | ES5 (with or without typed arrays) | ES2015, ES2017, ES2020, ES2022 |
| --- | --- | --- |
| one resource | `var r = e; try { … } finally { if (r != null) r.Dispose(); }` | `const r = e; try { … } finally { if (r != null) r.Dispose(); }` |
| several resources | nested `try … finally`, the first acquired outermost | same |
| resource class | constructor function with `Dispose` on the prototype | `class` with a `Dispose` method |

No JavaScript level a profile targets has Explicit Resource Management, so `try`/`finally`
(ECMAScript 3) is the only form; the `const`/`var` choice is the row `const`.

- **C# frontend:** `using (T r = e) S`, `using (T a = e1, b = e2) S`, `using (e) S` (the value held in
  a hidden local, so assigning the variable `e` names inside `S` changes nothing), stacked statements,
  `using var r = e;` and `using T r = e;` (the scope is the rest of the block), over classes of the
  file that implement `System.IDisposable`, with `Dispose` public or an explicit implementation, and
  over references of the type `IDisposable`. `await using` ("'await using' is not supported yet"), a
  resource of a struct type, a `ref struct` with a pattern-based `Dispose`, a resource of a library
  class and a finalizer are refused with `PLY1002`.
- **JavaScript frontend:** a `try … finally` that disposes is an `IrTry` and stays one, also in C#
  (recovering `using` from it is O0257). A `using` or `await using` declaration is the parser's syntax
  error (`PLY1100`), a `[Symbol.dispose]` or `[Symbol.asyncDispose]` method of a class `PLY1102`.

## Rust

**Frontend.** A Rust panic has no type and no handler in the imported subset; it becomes the throw of the class of
its kind: an arithmetic overflow, a shift amount out of range, `MIN / -1` and `MIN % -1`
`OverflowException`; a zero divisor `DivideByZeroException`; an index or a range outside a slice, an
array or a `Vec` `IndexOutOfRangeException`; `unwrap()` of a `None` or an `Err`, `expect("msg")`,
`step_by(0)`, `assert!` and its relatives, `panic!`, `todo!`, `unimplemented!`, `unreachable!` and
unequal lengths of `copy_from_slice`, a byte range of text off a char boundary, past its end or reversed
and `to_digit` with a radix above 36 `InvalidOperationException` with Rust's message; indexing a map by
an absent key the IR's `KeyNotFound` trap; `clamp` with `min > max` the `Clamp` trap (`ArithmeticException`). The class is compared with rustc's panic, the message is not
([the Rust frontend](../architecture/RUST-FRONTEND.md)).

**Backend.** A trap is a panic whose message is the name of the IR trap, in every build mode, and a
crate has no handler for it. The verification classifies the panic by its first line (`RustTraps`),
which also reads the messages of Rust's own panics. An exception of a class of the module is an
object, `Rc<PlyError>` (its class as a number, its message, its inner exception), handed back as the
`Err` of a `Result` by every function between the throw and the handler; see
[the Rust backend](../architecture/RUST-BACKEND.md#exceptions).

| IR trap | Rust | Message |
| --- | --- | --- |
| `IntegerOverflow` | `checked_add(…).expect(…)`, `checked_neg`, `checked_abs`, `T::try_from(…).expect(…)`, `ply_f2i_trap_<t>`, `checked_div`/`checked_rem` of `MIN / -1` | `IntegerOverflow` (a `try_from` adds `: TryFromIntError(())`) |
| `DivideByZero` | `ply_div_<t>`, `ply_rem_<t>` | `DivideByZero` |
| any trap a library helper throws | `panic!("<trap>")` for the throw of a library exception | the trap's name; for a class several traps share, the first of them (`InvalidOperationException` is `MissingValue`, `ArgumentException` `DuplicateKey`) |

| IR | Rust |
| --- | --- |
| `throw new X(…)` of a class of the module | `break 't0 e` inside a `try` body, `return Err(e)` from a function that propagates, `ply_unhandled(&e)` - a panic with the class's name - from a public one |
| `catch (X e) when (f)` | `if ply_is(error.class, <X>) && f { … }`, the next clause in the `else`, the exception going on after the last |
| `throw;` | the caught exception going on, `Rc::clone(&error)` |
| `e.Message`, `e.InnerException` | `e.message.clone()`, `e.inner.clone()` |
| `throw new InvalidOperationException(…)` with a clause of its class around it in the function | an exception like the module's own, `ply_error(7, …)`; without one a trap |
| `try { … } finally { … }`, with or without catch clauses | the body a labelled block every way out of which leaves it first - `thrown = Some(e)`, `returned = Some(v)`, `jumped = 1`, or the block's value where it is left one way only - then the `finally`, then the way out made |
| `catch (X) when (f)` with a `finally` between it and the throw | the clause chosen at the throw, before the `finally`: `(e, selected)` leaves, the handler a chain over `selected` |

Under the default trap policy `panic` a catch clause of a class a trap belongs to stands only around
code that cannot trap, since a trap is a panic no clause sees; catching a trap (`exceptions/Traps`) is
refused with `PLY1560`. A filter with a `finally` between it and the throw runs first, as .NET runs it
(`exceptions/order` gives "W,F,C" under both policies). Under both policies a filter that may trap, one
behind a handler whose own body holds a `finally`, and one around a call of a function that may run a
`finally` while the exception leaves it are refused with `PLY1560`, each naming why. Rust's own messages (`attempt to divide by zero`,
`index out of bounds`, `called Option::unwrap() on a None value`, …) map to the traps their class
stands for.

**The trap policy `result`** (`--trap-policy result`, [ADR 0008](../adr/0008-rust-trap-policy.md)) makes
every trap an error value, so that nothing panics and a catch clause sees a trap. Every function a trap
can leave returns a `Result`; a function nothing can leave keeps its type.

| IR trap | Rust under `result`, module without exceptions | Rust under `result`, module with exceptions |
| --- | --- | --- |
| `IntegerOverflow` | `a.checked_add(b).ok_or(Error::IntegerOverflow)?`, `T::try_from(x).map_err(\|_\| Error::IntegerOverflow)?` | the same with `ok_or_else(\|\| ply_trap(3))?`, an `OverflowException` |
| `DivideByZero` | `if b == 0 { return Err(Error::DivideByZero); }` in `ply_div_<t>`, called with `?` | `ply_trap(2)`, a `DivideByZeroException` |
| `IndexOutOfRange` | `*a.get(i).ok_or(Error::IndexOutOfRange)?`, `get_mut` for a store | `ply_trap(4)`, an `IndexOutOfRangeException` |
| `NullReference`, `MissingValue`, `InvalidCast` | `x.as_ref().ok_or(Error::NullReference)?`, `o.ok_or(Error::MissingValue)?` | the exception of the trap's class |
| inside a `try` body | - | `match x { Some(value) => value, None => break 't0 ply_trap(4) }` |
| throw of a library exception | `return Err(Error::<its trap>)` | the exception with its message, as a throw of the module's own |

`Error` is `#[repr(u16)] pub enum Error` in `src/error.rs`, re-exported from `src/lib.rs`, with the codes
`DivideByZero = 1`, `IntegerOverflow = 2`, `IndexOutOfRange = 3` and the other traps from 4, and
`Display` printing the trap's name. A helper of strings and collections that traps (a list's index, an
enum's or a number's parse, a cast, a view) gives the trap as its value there and the call checks it
(`ply_list_index(i, n).ok_or(Error::ArgumentOutOfRange)?`). A call of a helper that panics and has no such
form, a trap inside a closure, a step of locked module state and a type named `Error` are refused with
`PLY1552`.

## Visual Basic

Classic Visual Basic (VB6 / VBA) has no exception objects: an error is a number in `Err.Number`, raised
by an operation or by `Err.Raise`, handled by the `On Error` statement in force
([classic backend](../architecture/VISUALBASIC-BACKEND.md), [classic frontend](../architecture/VISUALBASIC-FRONTEND.md)).
Both directions use one table:

| IR class (trap) | Visual Basic run-time error |
| --- | --- |
| `DivideByZeroException` (`DivideByZero`) | 11 Division by zero |
| `OverflowException` (`IntegerOverflow`) | 6 Overflow |
| `IndexOutOfRangeException` (`IndexOutOfRange`) | 9 Subscript out of range |
| `InvalidCastException` (`InvalidCast`) | 13 Type mismatch |
| `NullReferenceException` (`NullReference`) | 91 Object variable not set |
| `ArgumentException`, `ArgumentOutOfRangeException` (`ArgumentOutOfRange`) | 5 Invalid procedure call or argument |
| `ArithmeticException` (`InvalidOperation`) and any other class | 5 |

- **Backend.** A trap is the error Visual Basic's own operation raises, or `Err.Raise` of its number in
  a helper; `throw new E()` of a library class is `Err.Raise`; a try is `On Error GoTo` whose handler
  takes the number and resumes at a dispatch over the catch clauses (`ArithmeticException` selects 6 and
  11, `Exception` every number), raising the number again where none selects it; `throw;` raises it
  again. A catch clause that reads the exception or has a filter, a `finally`, and a class no number
  stands for are refused with `PLY2655`/`PLY2657`.
- **Frontend.** `On Error GoTo` with its handler at the end of the procedure is a try with one catch
  clause per class of the table, each setting `Err.Number`; `On Error Resume Next` and a handler that
  ends with `Resume Next` are a try around every following statement; `Err.Raise` and `Error` of 5, 6,
  9, 11, 13 or 91 throw the class of the number; any other number is refused with `PLY2646`.

## Known differences

- Visual Basic raises error 6 or 11 for a floating-point operation whose IEEE 754 result is an infinity
  or a NaN; the IR gives the value. The classic frontend checks every floating-point result (error 6, 11
  for a zero divisor, 6 for 0/0); the classic backend writes Visual Basic's own operations, so a module
  translated to Visual Basic traps there where the IR does not. VBA clears `Err` when a procedure ends;
  the frontend does not model that clearing.

- .NET names a class in a namespace with it in the default message ("Exception of type 'N.X' was
  thrown."); the IR's class has no namespace, and neither has the generated C#.
- In JavaScript a class derived from a class of the file that derives from `Exception` and calls a
  parameterless constructor chain is named in the default message after the nearest class of the
  file with that constructor, not after the class of the object.
- `InvalidOperation` stands for more than the sign of a NaN: also an integer helper's argument outside
its range (C#: the helper throws `ArithmeticException`), `Clamp` with its bounds the wrong way round
  and `ReplaceAll` with an empty needle. .NET throws `ArgumentException` for the last two, where the
  IR and JavaScript throw the class of `InvalidOperation`; a `catch (ArgumentException)` around them
  is not translated faithfully yet.
- The message of a trap the IR raises itself - `ZeroDivisionError` of `//` on integers, an
  `OverflowError` beyond 64 bits - is the IR's text in an imported Python module, not CPython's; a
  handler that reads `str(e)` of one sees the IR's text.
- ES5 constructor functions on `Object.create(Error.prototype)` are no exception classes of the
  JavaScript import yet; only `class … extends Error` is.
