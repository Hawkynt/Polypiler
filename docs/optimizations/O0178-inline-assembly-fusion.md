# O0178 — Fuse fully recognized inline assembly with the surrounding source into canonical SSA

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#978](https://github.com/Hawkynt/Polypiler/issues/978). Once implemented it is switched off with `--disable-opt O0178`.

## What it does

PowerBASIC, C and Pascal sources drop into assembly for the two instructions the language lacked:
PB-Compiler's `LOWLEVEL.BAS` loads a constant through `AX`, counts a loop in `CX` around a BASIC
statement, and zero-extends a byte through `AL`/`AH`. PRD §17.4 states the rule: inline assembly is
syntax, not an optimizer fence — when a block is provably portable, the optimizer recovers its
high-level semantics and erases the assembly; when it is not, the unknown part stays with its effects
and everything independent is still optimized. This rule performs the fusion: machine-level register and
flag values become ordinary SSA values joined to the source's variables, so that algebraic rules span
the boundary. PRD §17.4, §19.5 S5, §22 ("Recover inline assembly as ordinary program semantics"), §24 S5
("mixed ASM+source algebraic fusion … capability and side-effect diagnostics"). The original
instructions are kept as the fallback graph with a `RecoveredFrom` link, and the report says "proven
equivalent to", never "intended as" (PRD §19.1).

## Example

### Before

```
; DIM v AS WORD : !MOV AX, 1234 : !MOV v, AX : PRINT v
%ax1 = MachMov.reg16[AX] 1234
MachStore.mem16 @v, %ax1
%t = Load.u16 @v
Call @print_u16, %t
```

### After

```
Store.u16 @v, 1234
%t = Load.u16 @v                       ; forwarded to 1234 by later passes
Call @print_u16, %t
; function attribute: assemblyFree = true
```

## When it applies

(1) every instruction of the run has complete semantics in [#139](https://github.com/Hawkynt/Polypiler/issues/139) —
operands, results, flags, memory effects — and none is marked opaque; (2) all memory operands resolve
to source variables or to storage with known type and bounds; a store narrower than the variable
writes only the addressed bytes, a load narrower than the variable reads only those (byte order per
the ISA), and the fused IR says so explicitly; (3) register state at entry is either defined inside
the run or an explicit input; register and flag state at exit is dead by liveness and by the dialect
contract, or is materialized as explicit values for the consumer; (4) flags are values: an instruction
that reads a flag is fused together with its definition (`ADD` … `ADC` becomes `AddWithCarry` fed by
the carry of the first addition), and a flag-setting instruction between them is an error in the
source that is preserved, not repaired; (5) partial registers are bit ranges of one value: writing
`AL` keeps `AH`, `XOR AH, AH` clears bits 8–15; (6) the stack pointer is unchanged over the run
(pushes and pops paired) and no address of a stack slot escapes; (7) control flow: branches target
labels inside the function; a counted `DEC`/`JNZ` loop becomes an ordinary loop whose counter is an
SSA value; (8) instructions that fail any of these end the run; they keep their MachineIR form,
invalidate the alias and range facts their effect summary names (PRD §22), and are reported with
`PLY0450`.

## How it is proven

`smt-bitvector` translation validation per run: the fused SSA and the MachineIR are
compared on symbolic register, flag and variable state for all live-out values and stores (bounded:
runs are straight-line or have a recovered counted loop, for which the body is validated inductively);
`exhaustive-8bit` for the 8-bit sub-register fixtures; differential execution against the real
toolchain. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Width: `!MOV v, AX` with `v AS LONG` writes two bytes; the upper half of `v` keeps its old value.
  Treating it as `v = AX` clears or garbles 16 bits (`v = &H12345678`, `AX = &HFFFF` gives
  `&H1234FFFF`).
- Carry: `ADD AX, lo2` then `ADC DX, hi2` — dropping the flag turns a 32-bit sum into two independent
  16-bit sums: `0x0000FFFF + 0x00000001` must be `0x00010000`, not `0x00000000`.
- Device effect: `MOV AX, &H0013` / `INT &H10` switches the video mode; folding `AX` away because
  nothing reads it afterwards deletes the call's argument.
- Segment-qualified memory: `MOV AL, ES:[BX]` with `ES = &HA000` reads video memory, not a BASIC
  variable; it is a device read.
- Partial register: `MOV AL, b` without clearing `AH`, then `MOV px, AX` — the high byte is whatever
  `AH` held; zero-extension must not be assumed.
- Unknown instruction: an opcode without semantics may clobber anything its conservative summary says;
  facts about that state must not survive it, while arithmetic on unrelated variables still folds.

## Switching it off

`--disable-opt O0178`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recover` after the assembler frontend's lifting and flag-liveness
analysis, and after [#981](https://github.com/Hawkynt/Polypiler/issues/981) (O0173) has separated independent register chains; requests a
`recognize` sweep through `PassContext.RequestSweep` so that rotations, byte swaps and carry chains
spanning assembly and source are found; the backends consult `assemblyFree`.
