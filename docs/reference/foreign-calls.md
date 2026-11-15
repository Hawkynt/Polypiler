# Foreign calls

C# calls native code through P/Invoke: `[DllImport("libc.so.6")] static extern …` and, from .NET 7 on,
`[LibraryImport(…)] static partial …` with the interop source generator. The IR carries such a declaration as a
*foreign function* (`IrForeignFunction`, #1332) with its full identity - library, symbol, calling convention,
character set - and the marshalling of every parameter and of the result; a call of one is a *foreign call*
(`IrForeignCall`). The C# backend keeps it P/Invoke in every profile, and the JavaScript backend calls it through
a binding module the user names ([ADR 0007](../adr/0007-javascript-native-calls.md)).
Related: [repository README](../../README.md) · [pointers](pointers.md) · [records](records.md) ·
[IR and analysis](../architecture/IR.md).

## Semantics

- **Identity.** The library is copied verbatim from the source (`libc.so.6`, `msvcrt`): no name is mapped between
  operating systems. The symbol is the `EntryPoint`, or else the method's name. Every argument the source gave to
  `[DllImport]` - `EntryPoint`, `CharSet`, `CallingConvention`, `SetLastError`, `ExactSpelling` - and to
  `[LibraryImport]` - `EntryPoint`, `StringMarshalling`, `SetLastError` - is kept.
- **Effect.** A foreign call has the unknown external effect of #73: it is evaluated exactly once where it stands,
  after its arguments left to right; no optimization moves, folds, merges or drops it. The interpreter never runs
  it: it traps `InvalidOperation` (`IrForeign.Trap`, "foreign call") - it is no oracle for native code.
- **In async functions.** A native call is synchronous: it runs to its end where it stands, so an
  [async function](async.md) may make one between its awaits - P/Invoke in C#, the call of the binding in JavaScript.
  Its effects are outside the module, so a function that makes one is not isolated, and a `Task.WhenAll` or
  `Promise.all` of it is refused as one whose order depends on timing.
- **Character sets.** `CharSet.Ansi` keeps its platform meaning - UTF-8 in .NET on Linux (`strlen("Grüße")` is 7),
  the system code page in .NET Framework on Windows; `Utf8` is UTF-8 everywhere. No character set is rewritten into
  another.
- **Native results** are what the native function returns, its undefined corners included: `abs(int.MinValue)` is
  -2147483648 on glibc and msvcrt, in the fixture and in every translation.
- **Missing native code** fails as .NET fails: `DllNotFoundException` for a library that is not there,
  `EntryPointNotFoundException` for a symbol it lacks, at the first call.
- **References.** An `out`, `ref` or `in` argument passes a one-element array; after the call its element goes back
  to the local of an `out` or `ref`. A discard (`out _`) passes a new array. A local is passed by reference only in a
  statement that is the call, assigns its result to a local or initializes one; elsewhere only discards.
- **Handles.** `nint` and `IntPtr` are an `i64` handle, `nuint` and `UIntPtr` a `u64` of the size of a pointer, at the
  boundary only: no local of the translated code has these types, so a handle is carried and never dereferenced.

## Marshalling

| Kind | C# | IR type of the parameter |
| --- | --- | --- |
| `Blittable` | the integers, `float`, `double`, a pointer `T*`; `nuint` and `UIntPtr` with `NativeSize` | the number, `u64`, the pointer |
| `Bool` | `bool`: the 4-byte `BOOL`, `[MarshalAs(UnmanagedType.Bool)]`, or one byte with `U1` or `I1` | `bool` |
| `String(Ansi \| Unicode \| Utf8)` | `string` handed in, by `CharSet`, `StringMarshalling` or `[MarshalAs(LPStr \| LPWStr \| LPUTF8Str)]` | `str` |
| `Array(In \| Out \| InOut)` | an array of numbers, `[In]`, `[Out]`, `[MarshalAs(UnmanagedType.LPArray)]` | `T[]` |
| `ByRef(In \| Out \| InOut)` | `in`, `out`, `ref` of a number, of a struct of numbers, of `IntPtr` | `T[]` of one element |
| `Struct` | a struct of numbers by value, with its layout | the struct |
| `Handle` | `nint`, `IntPtr` | `i64` |

A struct that crosses the boundary keeps its layout: `[StructLayout(LayoutKind.Sequential | Explicit, Pack = n,
Size = n)]` and `[FieldOffset(n)]` (`IrStructType.Layout`). `Marshal.GetLastWin32Error()` and
`Marshal.GetLastPInvokeError()` read the error the last call with `SetLastError = true` left (`IrLastError`).

Refused with `PLY1002`, by name: `StringBuilder` and every other class, `SafeHandle`, delegates and callbacks
(#640), `ICustomMarshaler` and `MarshalAs` with named arguments, a string marshalled out, `char`, enums, a `bool`
by reference, `params` and optional parameters, `CharSet.Auto`, `StringMarshalling.Custom`,
`StringMarshallingCustomType`, `PreserveSig`, `BestFitMapping`, `ThrowOnUnmappableChar`, `LayoutKind.Auto`,
`StructLayout` with `CharSet`, every other attribute on a native function or its parameters, a native function used
as a value, a named argument of a native call, and a local passed by reference inside an expression.

## C# profiles

| IR | net2.0, net3.5, net4.6 | net4.8 | netstandard2.0 | netstandard2.1, netcoreapp3.1 | net8.0, net10.0 |
| --- | --- | --- | --- | --- | --- |
| from `DllImport` | `[DllImport]` with every argument the source gave | same | same | same | same |
| from `LibraryImport` | `[DllImport]` | `[DllImport]` | `[DllImport]` | `[DllImport]` | `[LibraryImport]` on a `static partial` method of a `partial` class |
| `String(Utf8)` | a method of the source's signature and visibility that passes `Encoding.UTF8.GetBytes(s + "\0")` to a private extern taking `byte[]` | `[MarshalAs(UnmanagedType.LPUTF8Str)]` | as net2.0 | `[MarshalAs(UnmanagedType.LPUTF8Str)]` | `StringMarshalling.Utf8`, or `LPUTF8Str` from `DllImport` |
| `String(Ansi)`, `String(Unicode)` | `CharSet.Ansi` / `CharSet.Unicode` | same | same | same | same; `StringMarshalling.Utf16` from `LibraryImport` |
| `ByRef(In)` | `[In] ref T` (C# before 7.2) | `in T` | `in T` | `in T` | `in T` |
| `ByRef` of `IntPtr` | the extern as the source wrote it, called through a private adapter that passes a local and stores it back into the one-element `long[]` | same | same | same | same |
| struct layout | `[StructLayout]`, `[FieldOffset]` | same | same | same | same |
| last error | `Marshal.GetLastWin32Error()` | same | same | same | `Marshal.GetLastPInvokeError()` where the source called it |

There is no upgrade of `DllImport` to `LibraryImport`: `LibraryImport` needs a `partial` class, the interop source
generator and `AllowUnsafeBlocks`, which the header of such a file names. The capability rows
`DllImportAttribute`, `LibraryImportAttribute` (net8.0), `UnmanagedType.LPUTF8Str` (net4.8, netstandard2.1 and
later; not in the `netstandard.dll` of .NET Standard 2.0) and `Marshal.GetLastPInvokeError` (net8.0) follow the
"Applies to" tables of the .NET API reference, checked against the reference assemblies of every profile on
2026-11-14. The profile tests compile `[LibraryImport]` with the generator of the profile's reference pack,
`analyzers/dotnet/cs/Microsoft.Interop.LibraryImportGenerator.dll`.

## JavaScript

Without `--bind` for its library, a foreign function is refused with `PLY1152` in every profile and module format:
`'strlen' (libc.so.6) is a native function, which JavaScript profile 'es2022' cannot call`. With
`--bind libc.so.6=./libc-bindings.mjs` an ES module imports the binding, CommonJS requires it, and a script reads
it from the global `--bind libc.so.6=globalThis.LibC` names; every foreign function is a function of the module that
calls the function of its symbol in the binding. A pointer argument and the last error have no form in JavaScript
and are refused with `PLY1152`.

### The binding contract

| Value | Passed as |
| --- | --- |
| integer of up to 32 bits, floating-point number | `Number` |
| integer of 64 bits, handle, `nuint` | `BigInt` from ES2020; below, its halves `{ hi, lo }`, each unsigned |
| truth value | `boolean` |
| string | a JavaScript string |
| array, span | the module's typed array - or plain array where the profile has none - by reference: what the binding writes, the caller sees |
| `out`, `ref`, `in` | a one-element array of the referenced type, by reference |
| struct | a plain object with the struct's field names |

A result comes back the same way; a 64-bit integer may also come back as a `Number`, which the module converts. A
binding meant for every level converts what it receives with `typeof`, as
`tests/fixtures/prototype/interop/libc-bindings.mjs` does. Nothing verifies that the binding is the native function:
the manifest records each such function as the caveat `unverified-external` and each library under
`nativeDependencies`.

## Not supported yet

Callbacks into translated code (#640), varargs (#638), dynamic loading with `NativeLibrary` (#642), COM (#641),
ownership and pointers retained across calls (#335, #329), `StringBuilder` buffers, `SafeHandle` and custom
marshallers (#337), C headers as input (#319).
