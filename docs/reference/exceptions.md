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
└── FormatException
```

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

The **backend** raises the built-in exception of the trap's class; a trap is compared by its class,
as everywhere, and the message is the trap's own text where the backend raises it (Python's own
`ZeroDivisionError` of `//` and `%` keeps Python's message). Handling (`try`) is not written yet: a
module with a handler is refused with `PLY1851` naming it.

| IR class (trap) | Python raises |
| --- | --- |
| `DivideByZeroException` (`DivideByZero`) | `ZeroDivisionError` |
| `OverflowException` (`IntegerOverflow`) | `OverflowError` |
| `ArithmeticException` (`InvalidOperation`) | `ArithmeticError` |
| `IndexOutOfRangeException` (`IndexOutOfRange`) | `IndexError` (a negative index is checked first: Python would count it from the end) |
| `ArgumentOutOfRangeException` (`ArgumentOutOfRange`) | `ValueError` |
| `NullReferenceException` (`NullReference`) | `AttributeError` |
| `InvalidCastException` (`InvalidCast`) | `TypeError` |
| `InvalidOperationException` (`MissingValue`, `CollectionModified`) | `RuntimeError` |
| `KeyNotFoundException` (`KeyNotFound`) | `KeyError` |
| `ArgumentException` (`DuplicateKey`, `UnknownName`) | `ValueError` |
| `FormatException` (`InvalidFormat`) | `ValueError` |
| `Exception` | `Exception` |

The **frontend** does not import `try`, `raise` and `except` yet (#453). An operation that fails
raises the exception CPython raises, as the class of the IR that stands for it, with CPython's message:

| Python raises | IR class | where |
| --- | --- | --- |
| `ZeroDivisionError` | `DivideByZeroException` | `/`, `//`, `%`, `divmod` by zero, `0.0` to a negative power |
| `OverflowError` | `OverflowException` | an `int` beyond 64 bits (`py-int-64`), `int()`/`round()` of an infinity, a float power that overflows |
| `ValueError` | `ArgumentException` | a negative shift count, `int()`/`round()` of a NaN, a zero step of `range`, a negative power of an `int` whose sign was not known |
| `AssertionError` | `InvalidOperationException` | a failing `assert` |
| a `complex` result | `ArithmeticException` | a negative float to a fractional power (`py-complex-power`) |

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
  ("a thrown value that is not an Error reaches {place}, which observes it").
- **Python:** see [Python](#python) under Traps: the exceptions of the frontend and of the backend are
  one table each there.

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

## Known differences

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
- ES5 constructor functions on `Object.create(Error.prototype)` are no exception classes of the
  JavaScript import yet; only `class … extends Error` is.
