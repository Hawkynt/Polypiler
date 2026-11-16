# The Rust backend

**Status:** implemented for scalar code ([#461](https://github.com/Hawkynt/Polypiler/issues/461),
[#458](https://github.com/Hawkynt/Polypiler/issues/458), [#459](https://github.com/Hawkynt/Polypiler/issues/459));
arrays and slices, structs, enums, classes, interfaces and module state since
[#460](https://github.com/Hawkynt/Polypiler/issues/460) and [#462](https://github.com/Hawkynt/Polypiler/issues/462);
closures, function values, optional values, module state behind a `Mutex`, lists, maps and sets and
exceptions as `Result` since phase 2; lazy sequences follow. The runner that builds
and executes the output is [#468](https://github.com/Hawkynt/Polypiler/issues/468).
Related: [backends](BACKENDS.md) · [pipeline](PIPELINE.md) · [exceptions](../reference/exceptions.md) ·
[integer operations](../reference/integer-operations.md).

`polypiler translate <file> --from <csharp|javascript|python|rust> --to rust --output <dir>` writes a Cargo package:

| File | What it holds |
| --- | --- |
| `Cargo.toml` | `[package]` with `name` (the module's name in `snake_case`), `version = "0.1.0"`, the `edition` and, from Rust 1.56, `rust-version`; no dependencies |
| `src/lib.rs` | the header (`//!`: profile, helper licences, floating-point record, caveats, notices), `#![forbid(unsafe_code)]`, the lints the code needs allowed, `pub mod <module>;` |
| `src/<module>.rs` | the constants, the spelling functions and helpers, the functions; a `#[cfg(test)] mod tests` when tests are asked for |
| `src/<placed>.rs` | the functions `RustEmitOptions.Modules` places in a module of their own, `src/util/bits.rs` for `util::bits` (see [Modules](#modules)) |

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
| conversions to and from floating point | `a as f64`, `f64::from(a)` for `f32` to `f64`, `a as i32` saturating (Rust's `as`), `ply_f2i_trap_<t>(a)` trapping; ECMA-262's wrapping `ToInt32`/`ToUint32` (4294967301.0 to 5) the spelling function `ply_rs_f2i_wrap_i32(a)`, `(x.trunc() % 4294967296.0) as i64 as i32`, the IR library's `ToInt32` in Rust's casts (#461) |
| intrinsics with a method | `rotate_left`, `count_ones`, `leading_zeros`, `trailing_zeros`, `swap_bytes`, `reverse_bits`, `is_power_of_two`, `min`, `max`, `saturating_*`, `checked_abs`, `wrapping_abs`, `sqrt`, `floor`, `ceil`, `trunc`, `round`, `round_ties_even`, `abs`, `mul_add`, `copysign`, `is_nan`, `is_infinite`, `is_finite`, `is_sign_negative`, `is_normal`, `is_subnormal`, `to_bits`, `from_bits` |
| every other intrinsic | the helper of the IR library (`IntegerLibrary`, `MinimumDouble`, `SignOf`, `RoundHalfUp`, `Clamp`, …) or the portable floating-point implementation, copied into the module |
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
| a generic method or class | one function or struct per instantiation the module uses (`max_f64`, `RingI64`); `IEquatable`/`IComparable` at the instantiated type, a floating-point one by `ply_same_value_f64`, `ply_compare_f64` |
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
| a loop without a condition left only by `break`s of it, right before the `return` of a scalar (#458) | the value of that return, each `break` carrying it: `loop { if p >= x { break p; } p = p.wrapping_shl(1); }` as the function's tail |
| a counted loop `i <= c` to a constant below the type's maximum; a loop over an inclusive range as the Rust frontend imports it - a flag that the range is done, the counter moved on modulo its width after the flag is set (#458) | `for i in 0..=100i32`, `for _ in 0..=255u8`, `for i in start..=end` - Rust's `RangeInclusive`, which ends at the type's maximum without overflowing; `i <= n` of a variable stays a `while`, since at the maximum the source's loop never ends |
| a reduction: a binding made right before an element loop whose only statement combines it with a value of the element by `wrapping_add`, `wrapping_mul`, `^`, `\|` or `&` (#459) | an iterator fold, `data.iter().map(\|&b\| u32::from(b)).fold(0u32, \|acc, x\| acc.wrapping_add(x))`, the map left out for the element itself - never `sum` or `product`, whose overflow follows the build mode; not where the value names the binding, can leave the function (`?`) or the start is computed |
| `c ? true : x`, `c ? false : x`, `c ? x : true`, `c ? x : false` of truth values | `c \|\| x`, `!c && x`, `!c \|\| x`, `c && x`, which evaluate the same way; `!(a && b)` as `!a \|\| !b` |

## Modules

`RustEmitOptions.Modules` places functions, by IR name, in modules of the crate (#459), each given as
its path from the crate root: `util` is the file `src/util.rs`, `util::bits` the file
`src/util/bits.rs`, each declared by its parent (`src/lib.rs` for a module below the root; a parent
without functions of its own is a file of declarations), `pub mod` where it or a module below it has a
public item, else `mod`, sorted by name. A function named after its module's path, as the Rust
frontend names them (`util_bits_mix`), is called by the rest of its name there (`mix`), unless another
function of the crate has that name. Everything else - functions placed nowhere, helpers and spelling
functions, constants, globals, types, the tests - stays in the IR module's file, or in `src/lib.rs`
itself where the IR module is the crate root (`RustEmitOptions.CrateRoot`, and always for a module
named `lib` or `main`, whose file would be the root's or a program's); a function that names a type of
the crate stays there too, since a type's fields and methods are private to its module. Each file names
what it uses of another with one `use crate::<module>::<item>;` per item, sorted, and only what its code
uses; an item another file uses is `pub(crate)` at least. `RustEmitOptions.TestFunctions` makes
functions `#[test]` functions of the `#[cfg(test)] mod tests`, which has `use super::*;` where it uses
what its file declares or imports and `use` lines of its own for the rest. Emission is reproducible: the
same module and options give the same files. A module below the root named `lib` or `main` is refused
with `PLY1550`.

`polypiler translate <crate>/src/lib.rs --from rust --to rust` passes the source crate's layout through:
the Rust frontend reports the module of every free function outside the root (`ImportResult.Modules`)
and the crate's tests, and the crate is written back with one file per source module, its root in
`src/lib.rs`, its tests as `#[test]` functions, and the package named after the folder of
`src/lib.rs`. A Rust source of one file and every source of another language stay one module.

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

## Exceptions

A trap - an overflow, a division by zero, an index outside an array - is a panic, as it is wherever
no handler stands; no exception object is made for it. An exception of a class of the module, thrown
with `throw`, is a value instead, which the functions between the throw and the handler hand back as a
`Result`, so a catch clause sees it in safe Rust. The two never meet: a catch clause of a class a trap
belongs to (`Exception`, `InvalidOperationException`, …) stands only around a body that cannot trap,
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

## Refused

What the backend does not write yet is refused with `PLY1550`, naming the construct: pointers,
memory, a view cast to another element type or made on the stack, an optional value of a type that
is no value, an array that may be null and is written (`an array that may be null and is written`) or
is a borrowed result, a collection in the order of insertion (JavaScript's `Map` and `Set`), one of
objects or with floating-point keys, a collection changed in a loop over it, held by two variables or
passed on where no variable holds it, a lazy sequence (`yield`: Rust has no stable generators, and the
backend writes no state machine of an iterator function yet; making the sequence all at once would
change its effects and could not end on an infinite one), a variable that names a field's array where the function touches the field otherwise while it
lives or gives the array away whole, a function value that takes or gives more than values and other function values, two function values
compared with each other, a closure that captures the object of a method, an array it captures that is
written, or a variable other than a value that is assigned, collections and sequences (`Collection`, `Yield`), `using`, a global
array without a first value of a known length or used as a whole (passed on, iterated as a slice),
a global of another type, a record with a floating-point field,
a method that passes on the object of a hierarchy; an array safe Rust cannot own (written and shared by two variables, or a parameter and a new
array in one written variable), a struct with a field that is no value. One array passed to a call
twice where it is written and the callee swaps its parameters is `PLY1566`. Two exported functions
whose Rust names are the same are `PLY1574`; an exception region outside the form of
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
`clippy::unnecessary_literal_unwrap` (an optional value made of a value and then read, as the source has it),
`clippy::reversed_empty_ranges` (a loop over a range of constants the source wrote empty, `5..=3u8`).
No group is ever allowed. Every other lint is avoided by the shape the backend writes.

## Verification

- `RustScalarEmitTests`, `RustControlEmitTests`, `RustPackageEmitTests`, `RustSyntaxTests`,
  `RustProfileTests`: the emitted text per row, the printer's layout rules, the parse-back sweep over
  every prototype fixture and every Rust fixture in both overflow modes.
- `RustPrinterLayoutTests`, `RustEmitEdgeTests`, `RustTargetPassTests`, `RustReaderAndAnalysisTests`:
  the printer, the emitter, the target passes, the reader and the liveness analysis on the forms the
  fixtures do not reach, rule by rule.
- `MemoryEmitTests`, `TypeEmitTests`, `ObjectEmitTests`, `ClosureEmitTests`, `OptionalEmitTests`, `StateEmitTests`, `CollectionEmitTests`, `FieldAliasEmitTests`, `ExceptionEmitTests`: arrays, ownership and aliasing, structs, classes, traits, module state, function values, optional values and arrays that may be null, their forms and refusals.
- `RustDifferentialTests` (layer `rust`): every prototype fixture - all 24 since #460 - is built by the
  pinned toolchain and called on every case of `PrototypeCases`, with overflow checks on and off; every
  outcome, the final contents of every array argument included, equals the C# compiler's and the
  interpreter's.
- `RustFamilyDifferentialTests` (layer `rust`): the fixtures of the families the backend writes - bits,
  records, strings, enums, numbers as text, classes, dispatch, generics, function values and closures,
  optional values, a field's array named by a local (classes/Rc4), module state (each sequence of calls in one process, as one load of the class in
  .NET), collections and the LINQ shapes legalization writes as loops (collections/WordFrequency,
  sequences/ByteMedian, and the functions of collections/Collections the backend writes, which a test
  lists), exceptions of the module's classes (exceptions/Classes), the floating-point view of bytes -
  against .NET in both overflow modes.
- `RustRoundTripTests` (layer `rust`): every Rust fixture the backend writes (ten of fifteen; the
  others are listed in `RustFixture.BackendPending` and refused by name), imported by the Rust frontend
  in each overflow mode, comes back as a crate that is built with overflow checks on and off; every
  outcome is what rustc recorded for the original in the mode of the import.
- `RustBackendScenarioTests` (layer `rust`): the scenarios of the issues with their hand-checked
  values, clippy with `-D warnings` and `cargo fmt --check` on the crates, the generated tests passing
  and a mutated one failing; the loops with a value, the inclusive counted loops and the reductions
  against the C# compiler's build, the inclusive ranges of Rust (to 255u8, over all of i8, empty)
  against `rustc` in both import modes, the wrapping conversion from floating point against the IR
  library's `ToInt32`, the saturating addition, and the crate of the modules `util` and `hash`, each
  with overflow checks on and off.

The runner (`Polypiler.Core.Verification/Runners`): `RustRunner` builds (`cargo build`), tests
(`cargo test`), format-checks (`cargo fmt --check`), lints (`cargo clippy -- -D warnings`) and calls
exports through a generated driver (`RustDriverGenerator`) on input images - scalars, and `seq<T>` and
`utf16` as vectors borrowed by the call, an `inout` one exclusively and written back after the result,
`opt<T>` as an `Option` after its presence byte (`opt<seq<T>>` passed as `a.as_deref()`) - with `RustBuildOptions`
(`OverflowChecks`, `DebugAssertions`, `OptLevel`) that have no defaults. Diagnostics: `PLY1591` the
compiler rejected the input, `PLY1592` the driver died, `PLY1595` a protocol error such as a signature
that does not fit the declaration.
