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
└── ArgumentException
    └── ArgumentOutOfRangeException
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

## Known differences

`InvalidOperation` stands for more than the sign of a NaN: also an integer helper's argument outside
its range (C#: the helper throws `ArithmeticException`), `Clamp` with its bounds the wrong way round
and `ReplaceAll` with an empty needle. .NET throws `ArgumentException` for the last two, where the IR
and JavaScript throw the class of `InvalidOperation`; a `catch (ArgumentException)` around them is
not translated faithfully yet.
