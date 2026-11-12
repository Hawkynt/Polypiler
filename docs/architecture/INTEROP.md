# Foreign interoperability, ABI layouts and marshalling

**Status:** architecture contract, not a deployed FFI generator. See [PRD §§17, 29, 31–32](../PRD.md).

Foreign calls are first-class program operations. An imported C DLL function, JVM runtime call, PB runtime entry point or opaque ASM call must never disappear merely because its implementation is unavailable to the compiler.

## Binding pipeline

    Source declaration + dependency evidence
                      │
               Foreign symbol identity
          (library, export, ordinal, version)
                      │
          Signature + calling convention + effects
                      │
             Foreign layout constraints
            (size, offset, padding, alignment)
                      │
          Target binding / accessor / marshaler
                      │
     compatible native call OR verified ABI bridge
                      │
             Same observable external effects

The source declaration does **not always specify the library file**. For example, a C dllimport declaration may require an import library, linker metadata, binary import table or explicit manifest to recover its DLL identity.

## Native bindings, not copied libraries

If source C calls a compatible Win32 DLL, a PowerBASIC-for-Windows backend can emit a native \`DECLARE ... LIB ... ALIAS\` declaration with matching calling convention and types. It should **not** recompile the DLL into BASIC.

Potential target mechanisms include C/C++ extern declarations, Rust extern blocks, C# native imports, Delphi external declarations, PowerBASIC declarations, and Perl/Python FFI adapters.

If no target-compatible library/bridge exists, the compiler must report a dependency/capability failure. An unknown call must not be replaced with an arbitrary default value.

## Layout equivalence is distinct from call equivalence

| Property | Example obligation |
|---|---|
| Field representation | Fixed-width integer, Float80, BCD, ANSI bytes |
| Field offset | Unaligned 32-bit member beginning at byte 1 |
| Overall size | 5-byte packed foreign record |
| Alignment | 1-byte packed versus 4-byte natural |
| Array stride | Packed records every 5 bytes, not 8 |
| Padding and unions | Unobservable? overlapping? required exact bytes? |
| Pointer | Size, provenance, address space, lifetime |
| By-value classification | Registers/stack and ABI-specific aggregate rules |
| Callback | Argument convention, lifetime, thread and errors |

If the target UDT inserts unwanted padding, adding padding **cannot remove it**. The legalizer should choose native packing/explicit offsets when verified, else a byte-backed record, generated accessors or a native ABI bridge.

### Five-byte example

A C packed record has \`type:u8\` at offset 0 and \`value:u32\` at offset 1. A naturally aligned target UDT could instead place \`value\` at offset 4 and use eight bytes.

For pointer parameters, the backend may allocate an exact five-byte buffer and perform unaligned reads/writes correctly. For **by-value** parameters, matching bytes is not sufficient: register and stack classification must match too, sometimes requiring a native stub.

## Marshalling plans are optimization IR

An example conceptual sequence:

    AbiMaterialize(record, foreign_layout)
    AbiBorrow(exact_buffer)
    AbiCall(library_symbol, pointer, effect_summary)
    AbiCopyBack(fields_written_by_callee)
    AbiRelease(if_owned)

Perl may need generated \`pack\`/\`unpack\` around the FFI buffer, but those functions do not themselves provide an FFI calling convention. If several calls consume the same layout, Polypiler may pack once and unpack only when needed **if** alias/effect/lifetime analysis proves it safe.

Calls that retain pointers need stable storage and an explicit ownership contract. Calls that modify memory invalidate cached values; callback re-entry and thread state can prevent naive fusion.

## Float80 and legacy interfaces

The existing PB-Compiler \`Extended80\` and FrameworkExtensions numeric types can inform exact storage and conversion semantics.

- A Rust 10-byte struct can hold x87 bits, but may **not** be passed by value as an ABI-native \`long double\`.
- A native bridge may be required for register/stack conventions or mismatched compiler layouts.
- VB6 Automation interfaces use COM/Automation types; they do not inherently expose an 80-bit float parameter.
- Cross-bitness calls may require out-of-process adapters rather than in-process imports.
- Decimal/float conversions must preserve explicitly required rounding and exceptional values or be rejected as lossy.

## Effect rules and proof obligations

Before normalizing a foreign API into an intrinsic (e.g. a copy operation), check signature, version, overlap behavior, errors, locale, encoding, side effects and threading. An unrecognized call remains an external operation with conservative effects.

Only externally **unobservable** internal record layouts may be freely narrowed or reordered. An exported ABI layout cannot be optimized into a different public structure.

## Acceptance fixtures

Use real foreign libraries compiled with the originating toolchain:

- size/alignment/offset/stride oracles and packed, nested, union and bitfield cases;
- by-reference, by-value and returned aggregates;
- cdecl/stdcall-style call cases on compatible systems;
- in/out buffers, callbacks, retained pointers and ownership;
- ANSI/UTF conversion, unaligned access, errors and Float80 bit patterns;
- negative tests for incompatible architectures and layouts.

Related: [IR](IR.md) · [targets](TARGETS.md) · [verification](../quality/VERIFICATION.md).
