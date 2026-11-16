# The Rust backend

**Status:** implemented for scalar code ([#461](https://github.com/Hawkynt/Polypiler/issues/461),
[#458](https://github.com/Hawkynt/Polypiler/issues/458), [#459](https://github.com/Hawkynt/Polypiler/issues/459));
arrays and slices, structs, enums, classes, interfaces and module state since
[#460](https://github.com/Hawkynt/Polypiler/issues/460) and [#462](https://github.com/Hawkynt/Polypiler/issues/462);
closures, function values, optional values, module state behind a `Mutex`, lists, maps and sets and
exceptions as `Result` since phase 2; lazy sequences as `Iterator` state machines since
[#1391](https://github.com/Hawkynt/Polypiler/issues/1391). The runner that builds
and executes the output is [#468](https://github.com/Hawkynt/Polypiler/issues/468).
Related: [backends](BACKENDS.md) · [pipeline](PIPELINE.md) · [exceptions](../reference/exceptions.md) ·
[integer operations](../reference/integer-operations.md).

`polypiler translate <file> --from <csharp|javascript|python|rust> --to rust --output <dir>` writes a Cargo package:

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

```mermaid
flowchart LR
  IR["Legalized IR<br/>(library helpers bound in)"] --> Build["RustEmitter<br/>tree of the crate"]
  Build --> Passes["O0500 inlining<br/>O0501 removal"]
  Passes --> Lints["allowed lints<br/>of the shapes found"]
  Lints --> Print["RustPrinter<br/>rustfmt layout"]
  Print --> Crate["Cargo.toml, src/lib.rs,<br/>src/&lt;module&gt;.rs"]
  Crate --> Runner["RustRunner<br/>cargo build, test, fmt, clippy, driver"]
```

## Profiles

`--profile` names the edition, `--rust-version` raises the minimum supported Rust version (MSRV)
above the release that stabilized it, and `--trap-policy panic|result` says what a trap is
([the trap policy](#the-trap-policy-result)):

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
| `Mutex::new` in a `static` (`const_mutex_new`) | 1.63 | a global that has no atomic form is refused (`PLY1550`) |
| labelled blocks (`label_break_value`) | 1.65 | a `continue` in a `do`-`while` and a `break` inside a switch section are refused (`PLY1550`) |
| `round_ties_even` | 1.77 | the helper `RoundEven` of the library |
| `LazyLock` | 1.80 | a global whose first value Rust cannot make in a constant (a string that is not empty) is refused |

## Lowering

| IR | Rust |
| --- | --- |
| `Add`, `Subtract`, `Multiply` of integers | `a.wrapping_add(b)`, `wrapping_sub`, `wrapping_mul` - never a bare `+`, whose meaning depends on `overflow-checks` |
| `AddChecked`, `SubtractChecked`, `MultiplyChecked`; `NegateChecked` | `a.checked_add(b).expect("IntegerOverflow")`; `a.checked_neg().expect("IntegerOverflow")` |
| `AddOverflows`, `SubOverflows`, `MulOverflows` | `a.checked_add(b).is_none()`, `checked_sub`, `checked_mul` at every width |
| `Divide`, `Remainder` of integers | `ply_div_<t>(a, b)`, `ply_rem_<t>(a, b)`: `panic!("DivideByZero")` on 0, then `checked_div`/`checked_rem` with `IntegerOverflow`; `a / 7` where the divisor is a constant other than 0 and -1 |
| `ShiftLeft`, `ShiftRight`, `ShiftRightLogical` | `a.wrapping_shl(n as u32)`, `wrapping_shr`, which mask the count as the IR does; a logical shift of a signed value `(a as u32).wrapping_shr(n as u32) as i32` |
| `Negate`, `BitNot`, `LogicalNot` | `a.wrapping_neg()` (`-a` for floating point), `!a`, `!c` - a negated comparison of integers turned around (`a >= b`) |
| arithmetic and comparisons of floating point | the operators; a compound assignment where clippy's `assign_op_pattern` asks |
| conversions between integers | `i64::from(a)` where every value fits (a literal in it, also in the arms of an `if`, carries its type: `u64::from(if c { 1u32 } else { 0u32 })`), `a as u8` wrapping, `u8::try_from(a).expect("IntegerOverflow")` trapping |
| conversions to and from floating point | `a as f64`, `f64::from(a)` for `f32` to `f64`, `a as i32` saturating (Rust's `as`), `ply_f2i_trap_<t>(a)` trapping |
| intrinsics with a method | `rotate_left`, `count_ones`, `leading_zeros`, `trailing_zeros`, `swap_bytes`, `reverse_bits`, `is_power_of_two`, `min`, `max`, `saturating_*`, `checked_abs`, `wrapping_abs`, `sqrt`, `floor`, `ceil`, `trunc`, `round`, `round_ties_even`, `abs`, `mul_add`, `copysign`, `is_nan`, `is_infinite`, `is_finite`, `is_sign_negative`, `is_normal`, `is_subnormal`, `to_bits`, `from_bits` |
| every other intrinsic | the helper of the IR library (`IntegerLibrary`, `MinimumDouble`, `SignOf`, `RoundHalfUp`, `ToInt32`, `Clamp`, …) or the portable floating-point implementation, copied into the module |
| `if`, `while`, `do`-`while`, `for` | `if`/`else if`, `while c` (`loop` for `while true`), `loop { …; if !c { break; } }`, a `while` whose step precedes every `continue`; a counted loop `for i in a..b` where its counter and bound allow |
| `switch` | `match` with or-patterns and ranges of three or more consecutive labels, `_` last; one section an `if`; a `break` inside a section leaves `'s<n>: { match … }` |
| `return` | the tail of the function, `return v;` elsewhere |
| a value assigned in every branch | `let x = if c { a } else { b };` (clippy's `needless_late_init`) |
| a throw of a library exception | `panic!("<IrTrap>")` |
| an exception of a class of the module | `Rc<PlyError>` - `pub struct PlyError { pub class: usize, pub message: Vec<u16>, pub inner: Option<Rc<PlyError>> }`, the class a number whose bases `PLY_ERROR_BASES` lists; made by `ply_error(class, message, inner)` from what its constructors hand down to `Exception`'s; `Message`, `InnerException` its fields; see [Exceptions](#exceptions) |
| `try`/`catch` | the body a labelled block that gives the exception - `let error: Rc<PlyError> = 't0: { … break 't0 e … };`, or `let outcome: Result<(), Rc<PlyError>>` where the body may complete - the clauses an `if` chain, `ply_is(error.class, 12) && filter`, the exception going on where none takes it |
| a function that lets an exception out | `Result<T, Rc<PlyError>>`: `return Err(e)`, `return Ok(v)`, its calls `f(x)?`, inside a `try` body `match f(x) { Ok(value) => value, Err(error) => break 't0 error }`; a public one panics with the class's name, `ply_unhandled(&e)` |
| `try`/`finally` around a body that always returns and throws nothing | `let value = 'f0: { … break 'f0 v; };`, the `finally`, then `value` |
| a read-only table | `static NAME: [u32; N] = […];`, read as `NAME[i as usize]`, borrowed as `&NAME` |
| an array parameter | `&[T]`, or `&mut [T]` where the function or a callee writes it (#460) |
| an array the function makes | `Vec<T>`: `vec![0; n]` (`usize::try_from(n).expect("IntegerOverflow")` for a signed length), `vec![a, b]`, `Vec::new()`; a temporary one only read is an array `[a, b]` |
| an array result | `Vec<T>`, or the borrow of its parameters, `fn f<'a>(a: &'a [T], b: &'a [T]) -> &'a [T]`, exclusive where a caller writes through it |
| `a[i]`, `a[i] = v`, `a.Length` | `a[i as usize]` with Rust's own bounds panic as the trap, `a[i as usize] = v` (an index with an effect bound first, since Rust evaluates the value before the place), `a.len() as i32` |
| a counted loop over an array that only reads its elements | `for &x in data` (`&v` for a vector, `.iter()` for an exclusive borrow) |
| `Array.Copy`, `Fill`, `Clear` | `ply_copy`, `ply_copy_within` (one array), each range checked by `ply_range` first; `a.fill(v)` |
| a swap of two elements through a variable | `a.swap(i, j)` where the indices can neither panic nor change anything |
| `LoadLittle`/`LoadBig`, `StoreLittle`/`StoreBig` | `u32::from_le_bytes(d[ply_range(i, 4, d.len())].try_into().expect("4 bytes"))`; `let at = ply_range(…); d[at].copy_from_slice(&v.to_le_bytes());` |
| one array passed twice to a call that writes one of them | a copy of the callee with the two parameters merged, `<callee>_aliased<i><j>`, where the callee assigns neither; otherwise `PLY1566` |
| a struct of numbers | `#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)] pub struct Name { pub field: T }`; with a floating-point field `impl PartialEq` of the IR's equality (a NaN equals a NaN) instead of the derived one (#462) |
| `StructNew`, `StructDefault`, a field read or written | `Name { a, b: x }`, `Name::default()`, `p.x`, `p.x = v;` (`let mut` for the variable, a parameter rebound) |
| a string | its UTF-16 code units: `Vec<u16>` owned, `&[u16]` as a parameter; a literal `ply_utf16("text")`, a constant `static NAME: &str` |
| string operations | `len`, indexing, `==`, `starts_with`, `ends_with`, `repeat`, `cmp`, `[a, b].concat()`, `parts.join(sep)`, `push`/`extend_from_slice` for `s += …`; the helpers of `Helpers/Strings.cs` for searching, replacing, splitting, trimming and padding |
| an enum | a newtype over its underlying integer, `#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)] pub struct Color(pub u8);` with a constant per member and `From` both ways - open as a C# enum is, so any value of the integer is one of it; the bitwise operators on `.0`; its text and parse through `ply_enum_text`/`ply_enum_parse` over the statics `COLOR_VALUES`/`COLOR_NAMES` |
| numbers as text | integers by `to_string`; floating-point numbers by the shortest digits of `{:e}` laid out as .NET or ECMA-262 does (`ply_layout`); `F<n>` by `{:.n}`; radix digits by `{:x}`, `{:o}`, `{:b}`; .NET's `Parse` by `ply_parse_integer`, `ply_parse_f64` |
| a field of a class nothing reads or writes | left out of the struct: it keeps its default for good, which no one can tell from no field, and Rust would warn about it as dead code (a field only written goes through its cell, which Rust counts as a use) |
| a class (#462) | `#[derive(Default)] pub struct Name { count: Cell<i32>, name: RefCell<Vec<u16>>, items: RefCell<Vec<T>>, next: RefCell<Option<Rc<Other>>> }` - a value in a `Cell`, anything else in a `RefCell` - and its objects `Rc<Name>`; see [Objects](#objects) |
| `new T(..)`, a constructor | `Name::new(..) -> Rc<Self>`: `Rc::new(Self::default())` (`Rc::new(Self)` without fields), then the constructor's body, `init`, on it |
| a method, a property accessor | a method of the `impl`: `&self`, or `self: &Rc<Self>` where it passes the object on; called `o.next()` |
| a field of an object | `o.count.get()`, `o.count.set(v)`; `o.name.borrow().clone()`; `*o.next.borrow_mut() = v` after the value is bound; an array field `o.items.borrow()[i]`, `o.items.borrow_mut()[i] = v` (index and value bound first) |
| a variable that names a field's array, `var s = this._s;` | the field's cell borrowed for as long as the variable lives, `let mut s = self.s.borrow_mut();` where the function writes through it, `self.s.borrow()` where it only reads; indexed, measured and iterated through the guard, passed on as `&mut s[..]`; only where nothing in the function touches the field after it - no other read or write, no call that may reach the field (a fixed point over the calls), no other array given to the variable - since a second borrow would panic where .NET runs on |
| a reference that may be null | `Option<Rc<Name>>` where the nullness analysis cannot rule null out; a member through it `ply_non_null(&o)`, which panics with `NullReference` |
| `ReferenceEquals`, `==` of a record, `is null` | `Rc::ptr_eq(&a, &b)` (the addresses `a.as_ref().map(Rc::as_ptr)` where one may be null); `a == b` of the derived `PartialEq`; `o.is_none()`, `false` of a reference that never is null |
| an interface, a class something derives from | a trait, its values `Rc<dyn Trait>`; see [Objects](#objects) |
| a virtual or interface call | `Trait::method(&*o, ..)` of the trait of the method's slot |
| `is T`, `as T`, `(T)` | `o.is_t()`, `Rc::clone(&o).into_t()`, `….into_t().expect("InvalidCast")` of the trait `PlyObject`; `ply_cast` keeps a null |
| a generic method (#1394) | one Rust generic function, `pub fn max<T: Ord + Copy>(a: T, b: T) -> T`, where its template does nothing with a value of a parameter but hold, pass, store and load it in an array of it (`Copy`), make its default (`T::default()`, `Default`), compare it with `Equals` (`a == b`, `Eq`) and test the sign of `CompareTo` (`a >= b`, `Ord`); bounds inline for one parameter with at most two, else a `where` clause; a call names the type arguments where Rust cannot infer them, `def::<i32>()`. See [Generic methods](#generic-methods) |
| an instantiation the generic function does not take, a generic class | one function or struct per instantiation the module uses (`max_f64`, `RingI64`); `IEquatable`/`IComparable` at the instantiated type, a floating-point one by `ply_same_value_f64`, `ply_compare_f64` |
| a global (static field) | `static NAME: AtomicI32 = AtomicI32::new(0);` read and written with `Ordering::Relaxed`, `++` as `fetch_add` - process-wide as .NET's static fields are |
| a global that has no atomic form: an array, a string, a floating-point number | a static behind a `Mutex`: `static BUCKETS: Mutex<[u32; 4]> = Mutex::new([0; 4]);` (an array is the fixed array it always is), `static TEXT: Mutex<Vec<u16>> = Mutex::new(Vec::new());`, a first value made at run time in a `LazyLock`; accessed through `ply_with(&BUCKETS, \|buckets\| buckets[i])`, which holds the lock for that one access and takes a poisoned one over, every operand that may take a lock (a call, another such global) evaluated into a binding first; the length of an array is its constant. Not `thread_local!`, which would give each thread a global of its own |
| a view (`Span<T>`, `ReadOnlySpan<T>`) | a slice, `&[T]`, `&mut [T]` where the function writes it; `AsSpan(start, length)` `ply_view(a, start, length)`, which panics with `ArgumentOutOfRange` outside the source; `Length` `s.len() as i32` |
| a function type `fn(A, B) -> R` | `Rc<dyn Fn(A, B) -> R>`; `Option<Rc<dyn Fn(A, B) -> R>>` for a signature the module writes `null` of or makes an array of; see [Function values](#function-values) |
| a lambda | `Rc::new(move \|a: A, b: B\| body)`, what it captures bound first in a block around it (`{ let f = Rc::clone(&f); move \|v: i32\| f(v) }`), `move` only where it captures |
| a local function that captures nothing but such functions | a `fn` at the start of the function's body, called by its name, recursion included; `Rc::new(f)` where it is a value |
| a variable a closure captures and someone assigns after its declaration | `let c = Rc::new(Cell::new(0i32));`, read `c.get()`, written `c.set(v)`, the closure's own `Rc::clone(&c)` |
| `f(a, b)` of a function value | `f(a, b)`; `ply_non_null(&f)(a, b)` of a nullable one, the arguments bound first where they are more than variables and constants |
| a method group, a bound method | `Rc::new(square)`, `Rc::new(Name::twice)`; `{ let receiver = s.clone().expect("NullReference"); Rc::new(move \|v1: i32\| receiver.apply(v1)) }`, the object evaluated when the value is made |
| `f == null`, `f != null` | `f.is_none()`, `f.is_some()`; `false`, `true` of a type that never holds null |
| an optional value `T?` of a value type | `Option<T>`: `Some(v)`, `None`, `x.is_none()`/`x.is_some()`; the value a lifted operator has tested `x.unwrap_or_default()`, which cannot differ from it, an untested `.Value` `ply_value(&x)`, which panics with `MissingValue` (a function, which clippy's `unnecessary_unwrap` does not flag after a test) |
| `a ?? b` | `a.unwrap_or(b)` of a variable or a constant, `a.unwrap_or_else(\|\| f(c))` of a fallback that does something, evaluated only when needed; `or`, `or_else` where the result stays optional |
| an assignment inside an expression (`IrBind`: an operand evaluated once, `x ??= v`) | an assignment before the statement where everything evaluated before it has no effect, `let t0: Option<i32> = next(c);`, also when both branches of a choice begin with it; otherwise a block `{ q = Some(4); q.unwrap_or_default() }` in place |
| an array the module tests for null or gives null | `Option<&[T]>` as a parameter (read only), `Option<Vec<T>>` as a local or a result; `None`, `Some(&v)`, `v.as_deref()`, `a.map(<[T]>::to_vec)` where a place takes it; used as an array `ply_non_null(&a).len()`, `*ply_non_null(&a)` |
| a list | `Vec<T>` of values and strings: `push`, `insert`, `remove`, `pop`, `clear`, `contains`, `len() as i32`; an index checked by `ply_list_index` (`ply_list_insert_index` for an insertion), which panics with `ArgumentOutOfRange` as .NET throws; `IndexOf` `iter().position(…).map_or(-1, …)`, a NaN found by `ply_same_value`; `ToArray` and `new List<T>(array)` copies |
| a map, a set (.NET's `Dictionary`, `HashSet`) | a vector of slots, `Vec<Result<(K, V), u32>>`, `Vec<Result<T, u32>>`: an entry `Ok`, a slot a removal freed `Err(n)`, the n-th freed, which the next new key takes - so that a loop meets the keys in the order of .NET's slots (`ply_slot_insert`, `ply_slot_stamp`); `ply_map_put`, `ply_map_add` (`DuplicateKey`), `ply_map_try_add`, `ply_map_get` (`KeyNotFound` where it is no `TryGetValue`), `ply_map_find`, `ply_map_remove`, `ply_set_add`, `ply_set_find`, `ply_set_remove`; a key of an integer, a truth value, an enum or a string compared by `==` - linear, which keeps the order exact |
| a loop over a list, a map or a set | `for entry in list.iter()`, `map.iter().flatten()` (which skips the freed slots), the element, key or value copied into the loop's variable |
| a list, a map or a set passed on | `&[T]` where the callee only reads it, `&mut Vec<T>` where it changes it (a fixed point over the calls); a new one is moved where a variable or a result takes it |
| a loop whose first pass always returns | an `if` (clippy's `never_loop`) |
| an iterator function (`yield`) | `#[derive(Clone)] pub struct Up { state: u32, n: i32, i: i32 }`, `impl Iterator for Up { type Item = i32; fn next(&mut self) -> Option<i32> { … } }`, `fn up(n: i32) -> Up { Up { state: 0, n, i: 0 } }` - the struct in its first state is the sequence, a clone a new enumeration; see [Lazy sequences](#lazy-sequences) |
| `yield v`, `yield break`, the end of the body | `self.state = 2; return Some(v);`; `self.state = 3; return None;` - `Some(v)`, `None` as the value of an arm where no arm goes on (clippy's `never_loop`) |
| a loop over a sequence or `yield from` inside an iterator, where it suspends | a field `cursor0: Option<Up>`, `self.cursor0 = Some(up(self.n));`, then `let v = match self.cursor0.as_mut().and_then(Iterator::next) { Some(value) => value, None => { self.state = 4; continue; } };` |
| `try`/`finally` around a `yield` | the `finally` where the body leaves the `try`; `impl Drop` runs it at the states where it is pending, innermost first |
| a sequence parameter | `impl Iterator<Item = T> + Clone`; of an iterator a generic of its struct, `fn take<S: Iterator<Item = i32> + Clone>(source: S, count: i32) -> Take<S>` |
| an array or a string an iterator takes | borrowed for the struct's lifetime: `fn evens(a: &[i32]) -> Evens<'_>`, `fn pairs<'a>(a: &'a [i32], b: &'a [i32]) -> Pairs<'a>` |
| an iterator method of a class | the struct keeps the object, `counter: Rc<Counter>`; the method takes `self: &Rc<Self>` and makes it with `Rc::clone(self)` |
| a loop over a sequence, a sequence passed or held | `for v in up(3)`, `if let Some(v) = up(3).next()` where the first pass always leaves; `sum(s.clone())`, `let s = up(2);`; an array as a sequence `a.iter().copied()` |
| `c ? true : x`, `c ? false : x`, `c ? x : true`, `c ? x : false` of truth values | `c \|\| x`, `!c && x`, `!c \|\| x`, `c && x`, which evaluate the same way; `!(a && b)` as `!a \|\| !b` |
| the arbitrary-precision integer `bigint` (#1390) | `PlyBigInt`, a struct of the crate (see [Arbitrary-precision integers](#arbitrary-precision-integers)); a parameter or a result by value, a variable whose value a place takes `x.clone()`, a returned one moved |
| a `bigint` constant | `ply_big(5)`, an `i128` literal, up to 2^127 - 1 either side; beyond, its digits `ply_big_parse("…")` |
| `Add`, `Subtract`, `Multiply`, `Divide`, `Remainder`, `And`, `Or`, `Xor` of `bigint` | `ply_big_add(&a, &b)`, `ply_big_sub`, `ply_big_mul`, `ply_big_div`, `ply_big_rem` (truncating, `panic!("DivideByZero")` on 0), `ply_big_and`, `ply_big_or`, `ply_big_xor` (on the infinite two's complement); an operand that is no variable borrowed in place, `&ply_big_add(&a, &b)` |
| `Negate`, `BitNot` of `bigint` | `ply_big_neg(&a)`, `ply_big_not(&a)` (`-a - 1`) |
| `ShiftLeft`, `ShiftRight` of `bigint` by an `i32` | `ply_big_shift(&a, i64::from(n))`, `ply_big_shift(&a, -i64::from(n))`, a constant count its literal: never masked, a right shift rounding down, a negative count shifting the other way |
| comparisons of `bigint` | `a < b`, `a == b`: the derived `PartialEq` and the `Ord` of the struct, on the variables in place |
| a conversion to `bigint` | `ply_big(i128::from(x))` of an integer, a constant its literal; `ply_big_of_f64(x)` of `f64` (`f64::from(x)` of `f32`), truncating, `IntegerOverflow` on a NaN or an infinity |
| a conversion of `bigint` to a fixed width, to `f64` | wrapping `ply_big_low(&a) as i32` (the low 64 bits of the two's complement), trapping `ply_big_fit(&a, -2147483648, 2147483647) as i32`; `ply_big_to_f64(&a)`, the nearest number, an infinity beyond the largest |

## Generic methods

The IR holds no type parameter: a generic method is one function per list of type arguments the
module uses, and, from the C# frontend, a template - the body with the parameters standing as types
of their own (`IrGenericTemplate`). The backend writes a template as one generic function where every
operation on a value of a parameter has a Rust bound of the meaning .NET gives it, and lets that
function take the place of every instantiation whose type arguments the bounds admit
(`Emit/GenericDefinitions.cs`):

| Operation on a value of `T` | Rust | Bound | Admitted type arguments |
| --- | --- | --- | --- |
| held, passed, returned, stored in or loaded from `T[]` | `a: T`, `&[T]`, `&mut [T]` | `Copy` | every scalar |
| `default(T)`, `new T()` | `T::default()` | `Default` | every scalar |
| `a.Equals(b)` | `a == b` | `Eq` | integers, `bool` |
| `a.CompareTo(b)` against `0` | `a >= b`; `0 < a.CompareTo(b)` as `a > b` | `Ord` | integers, `bool` |

`Ord` and `Eq` rather than `PartialOrd` and `PartialEq` keep a float out of the generic function, also
for a caller of the crate: .NET orders a NaN below every number and lets `Equals` find a NaN, which
`<` and `==` of `f64` do not. An instantiation stays a function of its own, written as before, where:

- a type argument is not admitted: a floating-point number under `Ord`/`Eq` (`max_f64` beside `max`),
  a class, a struct, a string;
- the template does anything else with a value of a parameter: uses the value of `CompareTo` itself
  (the difference of two narrow integers in .NET), compares it with another number than zero, calls
  another generic method with it, captures it in a lambda, holds a string or a collection - and also
  fixed-width arithmetic on `T`, which the C# frontend does not import (generic math);
- an array that may be null is passed to it (`Option<&[i32]>`).

The template is written through the ordinary emitter, instantiated with one integer type per
parameter that no value of the body has (`u64`, `i64`, … the first free ones), so that the analyses of
arrays, mutability and names run on it unchanged; the stand-in is spelled as the parameter, its
constant - a default - as `T::default()`. A template whose body uses all eight integer types stays per
instantiation. An instantiation is replaced only where it is the template instantiated with its own
arguments, as the C# backend checks before it writes a definition back. Generic classes have no
template in the IR and stay one struct per instantiation (`RingI64`); the Rust frontend records no
template of the generics it monomorphises, so a Rust source round-trips per instantiation too.

## Objects

A class instance has an identity: every variable that holds it reads and writes one object. Safe Rust
has two forms for that, and the backend picks by what the module does with the class:

- **One owner at a time** - an object created, passed on by move or borrowed, never stored twice -
  is a plain struct with an `impl` block. (The ownership analysis that proves this for a class is the
  next step; until it lands every class takes the shared form, which is correct for one owner too.)
- **Shared** - an object two variables may hold, or that may be null - is an `Rc<Name>`, its fields
  each in a `Cell` (a value) or a `RefCell` (a string, an array, another object). A cell per field
  rather than one `RefCell<Name>` around the whole object: a `RefCell` borrow lives to the end of the
  statement that takes it, so a method that writes the object while an expression of its caller still
  holds a borrow would panic where .NET runs on; a `Cell` is never borrowed, and a `RefCell` field is
  borrowed for one access, the value of a store bound first. No `unsafe`.

A hierarchy is flattened into traits. Every interface and every class something derives from is a
trait; a value of it is an `Rc<dyn Trait>`. A class with objects of its own is a struct with every field
of its lineage (`AnimalObject` where `Animal` names the trait). A trait of a class has an accessor per
field the class declares (`field_x`) and a method per slot it opens, with the class's body as its
default; each struct implements every trait of its lineage and interfaces, overriding the slots whose
method for it is another. The methods themselves are functions of the module that take the object as a
reference, generic over the trait of their class (`fn shape_twice<T: Shape + ?Sized>(this: &T)`), so that
a base call (`base.M()`) calls the base's function. A test or a conversion to a type goes through the
trait `PlyObject` every trait extends: `is_t(&self)` and `into_t(self: Rc<Self>) -> Option<…>` per type
the module tests for or converts to, each struct answering for itself - no `Any`, no trait upcasting,
which Rust 1.85 has not.

## Function values

A function value is shared: two variables, an array element and a closure may all hold one, so its
form is an `Rc<dyn Fn(..)>`, cloned where a place takes it (`Rc::clone(&f)`, `f.clone()` of an
`Option`), passed and returned by value. Its signature decides whether it may be `null`: the module
writes `null` of a function type or makes an array of it, whose elements start as `null`, and every
value of that type is then an `Option`, called through `ply_non_null`.

A closure captures variables, not values (`IrLambda`). The backend sorts the variables the closures of
a function capture:

- a variable assigned after its declaration - by the function, by a closure, by a step, a loop's
  counter - is one place: `Rc<Cell<T>>`, the function and every closure holding the same `Rc`. Only a
  value (a number, a truth value, an enum, a struct of numbers) is shared so; another type assigned
  and captured is refused;
- every other variable has one value from its declaration on, which the closure copies: a value by
  `move`, a function value or an object by cloning its `Rc`, a string by a copy, an array nothing
  writes by a copy of its own - only where the closure is the argument of a call whose callee does
  nothing with it but call it, so that no one can write the array while the copy lives (any other
  captured array is refused). A variable declared in a loop's body is a new one each pass, as `let`
  in Rust is;
- a local function that captures nothing but such functions is a `fn` item at the start of the body.

A closure is `'static`: it borrows nothing, so it may be stored and returned. The object of a method
(`self`), which a closure would borrow, is refused as a capture.

## Lazy sequences

An iterator function produces its values on demand, and so does its Rust form: a struct holding a
state number, the function's parameters and the variables it keeps from one `yield` to the next,
implementing `Iterator`. `next` resumes in the state the struct stopped in - a `loop` over
`match self.state` - and runs on to the next `yield`, which sets the state it resumes in and returns
`Some(v)`, or to the end, which returns `None`. The body is cut into states as the JavaScript
backend's ES5 state machine cuts it (`docs/reference/sequences.md`): a statement that suspends, ends
the sequence or jumps out of itself becomes jumps between states (`self.state = n; continue;`), every
other statement is written as it is inside the arm of its state. A state that only goes on to another
is merged into the one before it, a jump through an empty state goes to its target, and a state that
only ends the sequence is left out.

A variable named in more than one state is a field of the struct, starting at the zero of its type
(`0`, `false`, `Vec::new()`, `None`, `T::default()`); any other one is a `let` inside its arm. A
loop over a sequence that suspends keeps a cursor field, `Option<S>`, set when the loop starts and
dropped when it ends. A sequence is restartable: the struct in its first state is the sequence, so
every enumeration of a variable is a clone of it (`s.clone()`), and a single-pass sequence (a
JavaScript generator object) named once is moved.

A `finally` block around a `yield` runs where the body leaves the `try` - at its end, before a
`yield break` or a jump out of it - and, when the consumer drops the iterator while it is suspended
inside, from the struct's `impl Drop`, which runs the blocks pending at that state innermost first
and drops the cursors inside them before. A `break` out of a loop over the sequence drops the
iterator at the end of the loop, as .NET's `foreach` disposes it.

What stays refused is named with `PLY1558`: an iterator of a class with a hierarchy, one that takes a
collection, writes an array it takes or assigns a string it takes, a variable kept across a `yield`
that is an array, a collection or a sequence or has no first value (an object that is never null),
a closure capturing a variable kept across a `yield`, a `switch` or a loop over a collection that
suspends, a jump out of a `try` with a catch clause or out of a `finally` inside an iterator, a
recursive iterator (its struct would hold itself), a sequence an iterator keeps that borrows an
array of its own, an array or a string a sequence borrows that is written while a variable holds the
sequence or used in the loop over it, a sequence variable assigned again, a single-pass sequence
enumerated twice, and an iterator that lets an exception of the module out.

## Arbitrary-precision integers

The IR's `bigint` (`IrBigInts`: Python's `int` where no fixed width is proven) has no type in Rust's
standard library. The representation follows rung 1 of the code-integration ladder of
[AGENTS.md](../../AGENTS.md#sourcing-an-implementation), **convert**: `PlyBigInt`, a sign and the
magnitude's 32-bit digits, least significant first, in safe Rust, its algorithms converted from
[num-bigint](https://github.com/rust-num/num-bigint) 0.5.1 (MIT OR Apache-2.0, used under MIT;
`Helpers/THIRD-PARTY-NOTICE.num-bigint.txt`): the addition and subtraction with carry and borrow, the
schoolbook multiplication, Knuth's algorithm D for the division, the shifts, the two's complement of
the bitwise operators digit by digit, the decimal digits of a constant. `Helpers/BigInts.cs` holds each
function as Rust source, read into the syntax tree when a crate calls it, with the functions it calls -
a crate carries only those - and `src/lib.rs` names num-bigint, its authors and its licence in its header.

num-bigint is not a dependency of the crate, nor vendored as a crate:

- the runner and CI build offline (`CARGO_NET_OFFLINE`, `cargo --offline`) with no registry cache, and a
  generated crate has no dependencies, so that it builds wherever its toolchain is;
- num-bigint 0.5 needs Rust 1.60 and its dependencies more; `rust2018` promises Rust 1.31 and `rust2021`
  1.56. The converted helpers name nothing of the standard library newer than 1.31 (`i128`, `to_bits`,
  `trim_start_matches`, `then_with`), which `RustBigIntTests` checks with clippy's `incompatible_msrv` at
  `msrv = "1.31.0"`, so no profile needs a capability row for them;
- the IR needs a fraction of the crate: no radix conversion but decimal digits, no Karatsuba or
  Burnikel-Ziegler, no `num-traits`. The conversion reads as Rust of the crate's own and passes
  `cargo fmt --check` and clippy with `-D warnings`.

Each operation is a function that borrows its operands and gives a new value, so a variable is never
moved by an operation; a value a place takes - an argument, a binding, an assignment - is the
variable's clone, a returned variable is moved. A comparison is the operator of the struct's order. The
traps are the IR's: a division or remainder by zero panics with `DivideByZero`, a trapping narrowing and
a NaN or an infinity made an integer with `IntegerOverflow`, in every build mode, since no helper uses
an operator whose meaning depends on `overflow-checks`.

## Exceptions

A trap - an overflow, a division by zero, an index outside an array - is a panic, as it is wherever
no handler stands; no exception object is made for it. An exception of a class of the module, thrown
with `throw`, is a value instead, which the functions between the throw and the handler hand back as a
`Result`, so a catch clause sees it in safe Rust. Under the default trap policy `panic` the two never
meet: a catch clause of a class a trap belongs to (`Exception`, `InvalidOperationException`, …) stands only around a body that cannot trap,
which the backend proves of every statement and call in it, and a throw of a class of the library
remains a trap.

.NET runs the filters of the handlers before the `finally` blocks between the throw and the handler
(two passes); a `Result` runs those blocks first. A filter is therefore written only where no `finally`
lies between - no `try`/`finally` in the body and no call that propagates - and only where it cannot trap,
since .NET takes an exception in a filter for `false`. A `try` with both catch clauses and a `finally`, a
`finally` around a body that may throw or complete, a `break` or `continue` out of a `try`, an exception in
a closure and a public function that lets an exception out and is called inside the module are refused
with `PLY1560`, which names why.

A trap is a panic whose message is the name of the IR trap - `IntegerOverflow`, `DivideByZero`,
`ArgumentOutOfRange`, … - in every build mode; the verification reads it back with `RustTraps`
([exceptions](../reference/exceptions.md#rust)).

### The trap policy `result`

`--trap-policy result` ([ADR 0008](../adr/0008-rust-trap-policy.md); `panic` is the default and writes
what the paragraphs above say) makes a trap an error value instead. Every function a trap, a throw or a
call can leave returns `Result<T, E>`, found as a fixed point: the crate is built again with the
functions that let an error out declared fallible until no new one does, so a function nothing can
leave keeps its plain type, exports included. Every trap is an explicit check: `checked_add(b).ok_or(Error::IntegerOverflow)?`,
`T::try_from(x).map_err(|_| Error::IntegerOverflow)?`, `*a.get(i).ok_or(Error::IndexOutOfRange)?` (and
`get_mut` for a store), `x.as_ref().ok_or(Error::NullReference)?`; the spelling functions of the
division and the checked conversions return a `Result` themselves (`if b == 0 { return Err(Error::DivideByZero); }`).
Inside a `try` body the check is a `match` whose empty arm leaves the body, `None => break 't0 ply_trap(4)`.

A module without exception classes and catch clauses gets `src/error.rs`, `pub use error::Error;` in
`src/lib.rs`: `#[repr(u16)] pub enum Error` with a variant per trap the crate produces, named after the
IR trap, with the stable codes `DivideByZero = 1`, `IntegerOverflow = 2`, `IndexOutOfRange = 3` and the
other traps from 4, `Display` printing the name, `std::error::Error` implemented. A module with
exceptions keeps `Rc<PlyError>`: a trap is the exception of its class with the default message
(`ply_trap(class)`), so a `catch (OverflowException)` or `catch (ArithmeticException)` sees it - which lifts
the `PLY1560` refusal of catching a trap - and a throw of a library exception is an exception too.
`Display` of an exception prints its trap's name, or its class's name. A generated test of a fallible
function asserts `assert_eq!(f(1, 0), Err(Error::DivideByZero));`.

What would still panic is refused with `PLY1552`, naming it: a call of a string or collection helper
that panics (`ply_list_index`, `ply_parse_i32`, …), a trap inside a closure, a step of an element of
module state behind a lock, and a type of the module named `Error`. The exception regions of `PLY1560`
other than catching a trap stay refused under both policies.

## Refused

What the backend does not write yet is refused with `PLY1550`, naming the construct: pointers,
memory, a view cast to another element type or made on the stack, an optional value of a type that
is no value, an array that may be null and is written (`an array that may be null and is written`) or
is a borrowed result, a collection in the order of insertion (JavaScript's `Map` and `Set`), one of
objects or with floating-point keys, a collection changed in a loop over it, held by two variables or
passed on where no variable holds it, a variable that names a field's array where the function touches the field otherwise while it
lives or gives the array away whole, a function value that takes or gives more than values and other function values, two function values
compared with each other, a closure that captures the object of a method, an array it captures that is
written, or a variable other than a value that is assigned, collections (`Collection`), `using`, a global
array without a first value of a known length or used as a whole (passed on, iterated as a slice),
a global of another type, a record with a floating-point field,
a method that passes on the object of a hierarchy; an array safe Rust cannot own (written and shared by two variables, or a parameter and a new
array in one written variable), a struct with a field that is no value. One array passed to a call
twice where it is written and the callee swaps its parameters is `PLY1566`. A lazy sequence outside the form of
[Lazy sequences](#lazy-sequences) is `PLY1558`, naming the construct. Two exported functions
whose Rust names are the same are `PLY1574`; a `bigint` anywhere but a parameter, a result, a variable or
an operand - an element of a list, a map or a set, a field, a global, a variable a closure captures - is
refused by that place's own rule (`a list of bigint`, `a closure that captures a variable of the type bigint`); an exception region outside the form of
[Exceptions](#exceptions) `PLY1560`, naming why.

## Lints

`src/lib.rs` allows exactly the lints the module has a shape for, from a closed list:
`unused_assignments` (a value of the source stored and never read, found by `RustLiveness`),
`unused_comparisons`, `clippy::absurd_extreme_comparisons`, `clippy::approx_constant`,
`clippy::almost_swapped` (the source's `let a = b; b = a;`), `clippy::empty_loop`, `clippy::eq_op`, `clippy::erasing_op`, `clippy::identity_op`,
`clippy::manual_clamp` and `clippy::min_max` (a minimum and a maximum whose bounds may be out of
order, where `clamp` would panic), `clippy::manual_range_contains`, `clippy::neg_cmp_op_on_partial_ord`
and `clippy::nonminimal_bool` (a negated ordering of floating-point numbers), `clippy::too_many_arguments`,
`clippy::type_complexity` (a function value that takes or gives another one, a type the source named in one word),
`clippy::manual_find` (a search the source wrote as a loop that returns `Some(i)`, then `None`),
`clippy::unnecessary_literal_unwrap` (an optional value made of a value and then read, as the source has it).
No group is ever allowed. Every other lint is avoided by the shape the backend writes.

## Verification

- `RustScalarEmitTests`, `RustControlEmitTests`, `RustPackageEmitTests`, `RustSyntaxTests`,
  `RustProfileTests`: the emitted text per row, the printer's layout rules, the parse-back sweep over
  every prototype fixture and every Rust fixture in both overflow modes.
- `RustPrinterLayoutTests`, `RustEmitEdgeTests`, `RustTargetPassTests`, `RustReaderAndAnalysisTests`:
  the printer, the emitter, the target passes, the reader and the liveness analysis on the forms the
  fixtures do not reach, rule by rule.
- `MemoryEmitTests`, `TypeEmitTests`, `ObjectEmitTests`, `ClosureEmitTests`, `OptionalEmitTests`, `StateEmitTests`, `CollectionEmitTests`, `FieldAliasEmitTests`, `ExceptionEmitTests`, `IteratorEmitTests`: arrays, ownership and aliasing, structs, classes, traits, module state, function values, optional values and arrays that may be null, lazy sequences, their forms and refusals.
- `BigIntEmitTests`: the form of every operation and conversion of `bigint`, the boundaries of a
  constant's `i128` literal, the helpers a crate carries and their origin, the refusals.
- `RustBigIntTests` (layer `rust`): every operation and conversion of `BigIntCases` - zero, the signs,
  2^63 and 2^64 on both sides, 2^1100 + 1, every shift count from -64 to 200 - against .NET's
  `BigInteger`, every public function of the Python fixtures the backend writes against CPython's
  recording, Python's `2**100 + 1`, each with overflow checks on and off through a driver that builds a
  `PlyBigInt` from its fields; the crate formatted and clippy-clean in `rust2018` and `rust2024`, and at
  `msrv = "1.31.0"`.
- `RustDifferentialTests` (layer `rust`): every prototype fixture - all 24 since #460 - is built by the
  pinned toolchain and called on every case of `PrototypeCases`, with overflow checks on and off; every
  outcome, the final contents of every array argument included, equals the C# compiler's and the
  interpreter's.
- `RustFamilyDifferentialTests` (layer `rust`): the fixtures of the families the backend writes - bits,
  records, strings, enums, numbers as text, classes, dispatch, generics, function values and closures,
  optional values, a field's array named by a local (classes/Rc4), module state (each sequence of calls in one process, as one load of the class in
  .NET), collections and the LINQ shapes legalization writes as loops (collections/WordFrequency,
  sequences/ByteMedian, and the functions of collections/Collections the backend writes, which a test
  lists), the iterator functions and LINQ chains of sequences/Iterators and sequences/Pipelines the
  backend writes (a test lists them), exceptions of the module's classes (exceptions/Classes), the floating-point view of bytes -
  against .NET in both overflow modes.
- `RustRoundTripTests` (layer `rust`): every Rust fixture the backend writes (ten of fifteen; the
  others are listed in `RustFixture.BackendPending` and refused by name), imported by the Rust frontend
  in each overflow mode, comes back as a crate that is built with overflow checks on and off; every
  outcome is what rustc recorded for the original in the mode of the import.
- `RustBackendScenarioTests` (layer `rust`): the scenarios of the issues with their hand-checked
  values, clippy with `-D warnings` and `cargo fmt --check` on the crates, the generated tests passing
  and a mutated one failing.
- `RustIteratorScenarioTests` (layer `rust`): an infinite counter taken 5 items runs its body 5 times;
  finally blocks around yields run as in .NET on every way out (traced through a global), iterators
  over borrowed arrays and strings, generic sequence parameters and loops that suspend give the C#
  compiler's values in both overflow modes; the crates and the written part of the `sequences`
  family pass clippy with `-D warnings` and `cargo fmt --check`.

The runner (`Polypiler.Core.Verification/Runners`): `RustRunner` builds (`cargo build`), tests
(`cargo test`), format-checks (`cargo fmt --check`), lints (`cargo clippy -- -D warnings`) and calls
exports through a generated driver (`RustDriverGenerator`) on input images - scalars, and `seq<T>` and
`utf16` as vectors borrowed by the call, an `inout` one exclusively and written back after the result,
`opt<T>` as an `Option` after its presence byte (`opt<seq<T>>` passed as `a.as_deref()`) - with `RustBuildOptions`
(`OverflowChecks`, `DebugAssertions`, `OptLevel`) that have no defaults. Diagnostics: `PLY1591` the
compiler rejected the input, `PLY1592` the driver died, `PLY1595` a protocol error such as a signature
that does not fit the declaration.

## Cipher known-answer tests

`polypiler-tooling cipher kat --targets rust` ([#1365](https://github.com/Hawkynt/Polypiler/issues/1365))
translates a Cipher algorithm file with `--to rust` into `crate/`, writes a test crate beside it
(`Cargo.toml` depending on the translation, `tests/kat.rs` with one `#[test]` per checkable vector)
and runs `cargo test --offline`. Rust has no reflection, so `KatRustCrate` reads the translated files
back with `RustSyntaxReader` and writes every call and every input as a literal of the type the
translation declares, through the syntax tree and `RustPrinter`. A translation is reached through:

| What the vector does | What the crate must have |
| --- | --- |
| look the algorithm up by name | a `pub fn algorithms()` without parameters giving `Vec<Rc<dyn Algorithm>>` (or `Vec<Rc<Struct>>`), each with an accessor `field_name` giving `&RefCell<Vec<u16>>`, `Vec<u16>` or `&[u16]` |
| `CreateInstance(isInverse)` | a method `create_instance` of the algorithm's type or of a trait it has, giving `Rc<dyn Instance>`, also in an `Option` or a `Result` |
| call a method, `Feed`, `Result` | a method of the instance's type or of a trait it has, named in `snake_case`, with as many parameters; `result` giving a vector, slice or array of numbers |
| set a property | an accessor `field_<name>` giving `&Cell<T>` or `&RefCell<T>` |

Where the crate lacks the registry or the name, the file counts as translated and nothing runs; where
it lacks what one vector needs, that vector's test fails with the reason. A failing comparison names
the first differing byte, a panic of the translation its message, a crate that does not compile the
compiler's first error. Today every Cipher file is still refused before that (the registry is a
global list of objects, `PLY1550`).
