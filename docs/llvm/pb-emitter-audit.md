# Audit of the PB-Compiler LLVM emitter

What `Hawkynt/PB-Compiler` emits when it writes textual LLVM IR, read at the pinned commit and checked
with the real LLVM tools. It is the inventory PRD §26.5 L0 asks for before
[FMT-LLVM-SPEC (#69)](https://github.com/Hawkynt/Polypiler/issues/69) specifies the LLVM contracts and
[FMT-LLVM-EXPORT-LL (#852)](https://github.com/Hawkynt/Polypiler/issues/852) carries the emitter over.
It describes; it changes nothing in PB-Compiler.

## Pin and tools

| What | Value |
| --- | --- |
| Repository | `Hawkynt/PB-Compiler@0d73acbea185a2597357d53f7ce9acb44758ba76` (LGPL-3.0-or-later) |
| Audited file | `PowerBasic.Compiler/Ir/LlvmEmitter.cs`, 330 lines; cited below as `LlvmEmitter.cs:<line>` |
| Other files cited | `PowerBasic.Compiler/Ir/EmitDeclinedException.cs`, `PowerBasic.Compiler/Ir/IrType.cs`, `PowerBasic.Compiler/Ir/IrConstant.cs`, `pbc/Driver.cs`, `PowerBasic.Compiler.Tests/Ir/LlvmEmitterTests.cs`, `PowerBasic.Compiler.Tests/Cli/EmitLlvmTests.cs`, `PowerBasic.Compiler.Tests/Ir/EmitterNeverThrowsTests.cs` |
| `pbc` | built from the pin with .NET SDK 10.0.112 (`dotnet build pbc/pbc.csproj -c Release`) |
| LLVM | 23.1.3 (`llvm-as`, `opt`, `llc`, `lli` and `llvm-config` all report 23.1.3; `clang` 23.1.3), the `llvm` and `clang` keys of `tooling/toolchains.lock` |
| Characterization | `tests/fixtures/formats/llvm/pb/characterization.yaml`, checked by `Polypiler.Tests.Acceptance/Formats/Llvm/PbEmitterCharacterizationTests.cs` |

Two kinds of evidence back the rows. **Code** is the cited line at the pin. **Run** is the output of
`pbc --emit-llvm` over the 213 `.BAS` files under `tests/` at the pin, each fed to `llvm-as` and
`opt -passes=verify -disable-output`, and the accepted ones to `llc -O2`:

| Outcome | Modules |
| --- | --- |
| `pbc --emit-llvm` exits 0 and prints a module | 202 |
| `pbc` declines (exit 1): binder errors 4, far pointer 5, inline assembly the lowering rejects 2 | 11 |
| `llvm-as` and `opt -passes=verify` accept the module | 171 of 202 |
| `llvm-as` and `opt` reject the module (findings F1 and F2) | 31 of 202 |
| `llc -O2` lowers an accepted module to x86-64 | 171 of 171 |

`lli` was not run: 200 of the 202 modules declare `rt_*` entries of PB-Compiler's runtime and define
none of them, so they cannot run on their own. A construct marked "not in the corpus" was not emitted by any of the 202 modules, so only
its code is cited.

## 1. IR construct to emitted LLVM text

| Construct | Emitted text | Source | Evidence |
| --- | --- | --- | --- |
| Module header | `; ModuleID = '<name>'`, then `target triple = "<triple>"` only when a triple is passed; never `target datalayout` | `LlvmEmitter.cs:49-53` | Run: every module; `pbc` passes `x86_64-unknown-linux-gnu` (`Driver.cs:387`) |
| Byte global (`Bytes`) | `@name = private constant [N x i8] c"..."` | `LlvmEmitter.cs:55-57` | Run: `MINI.BAS`, `DATAREAD.BAS` |
| Floating global (`FloatingValues`) | `@name = private constant [N x float\|double] [float 0x..., ...]`; anything but binary32/binary64 with a matching count is a `BackendInvariantException` | `LlvmEmitter.cs:58-59`, `LlvmEmitter.cs:74-88` | Code; not in the corpus |
| Typed global | `@name = global T zeroinitializer`, `[N x T]` when `Count > 1`; default (external) linkage | `LlvmEmitter.cs:60-63` | Run: `MINI.BAS` (`@g.tCount = global i16 zeroinitializer`) |
| Function | `define`/`declare`, return type, `@name`, typed parameters; unnamed parameters get `%<slot>` | `LlvmEmitter.cs:96-106`, `LlvmEmitter.cs:121-122` | Run: `ARITH.BAS` (`declare void @rt_print_i16(i16 %0)`) |
| Variadic function | `, ...` or `...` after the parameters | `LlvmEmitter.cs:102-103` | Run: `DIFF02.BAS` (`@rt_str_concat_n`) |
| `"reciprocal-estimates"` function attribute | `"divf"`, `"divd"` or `"divf,divd"` when an `fdiv` carries both `arcp` and `ninf` | `LlvmEmitter.cs:108-109`, `LlvmEmitter.cs:196-214` | Code; not in the corpus |
| Basic block | `<label>:` with the label verbatim | `LlvmEmitter.cs:111-115` | Run: every module; unquoted labels are finding F2 |
| Value names | every parameter and non-void instruction named before emission: its own `Name`, else `%<slot>` | `LlvmEmitter.cs:120-127` | Run: duplicate names are finding F1 |
| `IrCast` with `FPToSIRound`/`FPToUIRound` | two lines: `%rint.N = call T @llvm.rint.fB(T x)`, then `fptosi`/`fptoui T %rint.N to iM`; no `declare` for the intrinsic | `LlvmEmitter.cs:136-142` | Run: `MINI.BAS` (`@llvm.rint.f80`), `DIFF117.BAS` (`fptoui double`) |
| `IrBinary` on floats | `fadd`/`fsub`/`fmul`/`fdiv` with fast-math flags, then type and operands | `LlvmEmitter.cs:145` | Run: `DIFF110.BAS` (`reassoc nnan ninf nsz`) |
| `IrBinary` on integers | `add sub mul sdiv udiv srem urem and or xor shl lshr ashr`, type, operands; no `nsw`/`nuw`/`exact` | `LlvmEmitter.cs:146`, `LlvmEmitter.cs:305-312` | Run: every module with arithmetic |
| `IrCmp` on floats | `fcmp` with fast-math flags and `oeq one olt ole ogt oge` | `LlvmEmitter.cs:147`, `LlvmEmitter.cs:318-319` | Run: `DIFF35.BAS` (`fcmp nnan ninf nsz ole`) |
| `IrCmp` on integers | `icmp eq ne slt sle sgt sge ult ule ugt uge` | `LlvmEmitter.cs:148`, `LlvmEmitter.cs:315-317` | Run: `NARROW.BAS` (`icmp sle i16`) |
| `IrCast` | `trunc zext sext fptosi fptoui sitofp uitofp fptrunc fpext inttoptr ptrtoint bitcast`, `<op> T x to U` | `LlvmEmitter.cs:149`, `LlvmEmitter.cs:323-329` | Run: `DIFF56.BAS` (`ptrtoint ptr %g to i16`) |
| `IrAlloca` | `alloca T`, or `alloca T, i32 N` when `Count > 1`; no `align` | `LlvmEmitter.cs:150` | Run: `ARRAY.BAS` (`alloca i16, i32 12`) |
| `IrLoad` | `load T, ptr p, align 1` | `LlvmEmitter.cs:154` | Run: all 27 loads and stores of the four fixtures end in `align 1` |
| `IrStore` | `store T v, ptr p, align 1` | `LlvmEmitter.cs:155` | Run: as `IrLoad` |
| `IrGep` | `getelementptr E, ptr base, iN offset`, `E` the element type or `i8` when it has none | `LlvmEmitter.cs:156` | Run: `ARRAY.BAS` (`getelementptr i8, ptr %grid, i32 6` and `getelementptr ptr, ptr %names, i32 1`) |
| `IrPhi` | `phi T [ v, %label ], ...` | `LlvmEmitter.cs:157`, `LlvmEmitter.cs:216-217` | Run: `DIFF03.BAS` |
| `IrSelect` | `select i1 c, T a, T b` | `LlvmEmitter.cs:158` | Run: `DIFF08.BAS` |
| `IrCall` | `call` with fast-math flags, the result type, or the whole function type for a variadic callee, then typed arguments | `LlvmEmitter.cs:161`, `LlvmEmitter.cs:219-232` | Run: `DIFF02.BAS` (`call ptr (i32, ...) @rt_str_concat_n`) |
| `IrRet` | `ret T v` or `ret void` | `LlvmEmitter.cs:162` | Run: every module |
| `IrBr` | `br label %L` | `LlvmEmitter.cs:163` | Run: every module with a loop |
| `IrCondBr` | `br i1 c, label %T, label %F` | `LlvmEmitter.cs:164` | Run: `NARROW.BAS` |
| `IrSwitch` | `switch iN c, label %D [ iN v, label %L ... ]` | `LlvmEmitter.cs:165`, `LlvmEmitter.cs:234-237` | Run: `DIFF19.BAS` |
| `IrIndirectBr` | `indirectbr ptr a, [ label %L, ... ]` | `LlvmEmitter.cs:166` | Run: only `DIFF11.BAS`, whose module is rejected for F1 first; acceptance of the `indirectbr` line itself not reproduced |
| `IrUnreachable` | `unreachable` | `LlvmEmitter.cs:167` | Run: `DATAREAD.BAS` |
| Any other instruction (`IrInlineAsm`, `IrFarPtr`) | declined, see table 2 | `LlvmEmitter.cs:168` | Run: `DIFF114.BAS` (far pointer) |
| `Ref`: `IrConstantInt` | the decimal value | `LlvmEmitter.cs:240` | Run: every module |
| `Ref`: `IrConstantFloat` of x87 80-bit type | `0xK` and 20 hex digits | `LlvmEmitter.cs:241` | Run: `ARITH.BAS` (`x86_fp80 0xK4000C000000000000000`) |
| `Ref`: `IrConstantFloat` of 32 or 64 bits | `0x` and the 16 hex digits of the binary64 pattern; a binary32 constant is rounded to binary32 when it is built (`IrConstant.cs:44`), so the pattern is exact | `LlvmEmitter.cs:242`, `LlvmEmitter.cs:300-301` | Run: `DIFF35.BAS` (`fcmp ... float %x, 0x3FF0000000000000`) |
| `Ref`: `IrNullPtr` | `null` | `LlvmEmitter.cs:243` | Code |
| `Ref`: `IrUndef` | `undef` | `LlvmEmitter.cs:244` | Code; not in the corpus |
| `Ref`: `IrBlockAddress` | `blockaddress(@function, %label)` | `LlvmEmitter.cs:247-249` | Run: `DIFF105.BAS` (`rt_resume_mark`), `DIFF121.BAS` (`rt_onerr_arm`) |
| `Ref`: `IrFarEntry` | declined, see table 2 | `LlvmEmitter.cs:254-256` | Run: `GOTODW.BAS` of the characterization |
| `Ref`: `IrGlobalValue` | `@name` | `LlvmEmitter.cs:257` | Run: every module |
| `Ref`: any other value | the name `AssignNames` gave it, else a `BackendInvariantException` | `LlvmEmitter.cs:263-265` | Code |
| Mnemonic fallbacks | the enum member's name in lower case; unreachable for `IrBinaryOp` and `IrCmpPred`, whose members all have a case | `LlvmEmitter.cs:311`, `LlvmEmitter.cs:320`, `LlvmEmitter.cs:328` | Code |
| Byte escapes | printable ASCII except `"` and `\` verbatim, everything else `\XX` in upper-case hex | `LlvmEmitter.cs:289-297` | Run: `DATAREAD.BAS` (`c"\02\0042..."`) |

## 2. Declined constructs

A decline is an `EmitDeclinedException`; `TryEmit` turns it into a `null` module and the message
(`LlvmEmitter.cs:32-40`), and `pbc` prints `pbc: --emit-llvm: <message> - outside what this back end
renders (see docs/BACKENDS.md)` and exits 1 (`Driver.cs:388-392`).

| Construct | Message | Source | Evidence |
| --- | --- | --- | --- |
| An instruction without a case | `LLVM emission: ` and the description of `EmitDeclinedException.For` | `LlvmEmitter.cs:168`, `EmitDeclinedException.cs:30-39` | Run: see the next two rows |
| `IrInlineAsm` (a `!` statement the lifter left as assembly) | `an inline assembly block (a `!` statement), which is x86-16 machine code by definition` | `EmitDeclinedException.cs:34` | Run: the `inline-asm` case of `EmitterNeverThrowsTests.cs:301` |
| `IrFarPtr` (segmented PEEK/POKE, `DIM ... AT`) | `a far (segment:offset) pointer - ... - which has no portable pointer form` | `EmitDeclinedException.cs:35-36` | Run: `DIFF114.BAS`, `DIFF125.BAS`, `DIFF126.BAS`, `DIFF17.BAS`, `idioms/MEMORY.BAS` |
| Any other instruction type | `the IR instruction <Type>, which this back end has no case for` | `EmitDeclinedException.cs:38` | Code |
| `IrIndirectBr` description | `a computed branch (GOTO DWORD / GOSUB DWORD, through a CODEPTR32 address)` is never produced by this emitter, which has a case for `IrIndirectBr` | `EmitDeclinedException.cs:37`, `LlvmEmitter.cs:166` | Code |
| Far entry thunk (`IrFarEntry`) | `LLVM emission: the far entry thunk of '<target>' - a real-mode adapter between a far call and a near return, which has no body to emit here` | `LlvmEmitter.cs:254-256` | Run: `GOTODW.BAS` (`GOTO DWORD` to `CODEPTR32(S6)`), exit 1, empty standard output |
| Microsoft Binary Format value | `LLVM emission: a value in Microsoft Binary Format (<type>), which has no LLVM type - it has to be converted to IEEE with MbfToFP before emission` | `LlvmEmitter.cs:280-282` | Code; not in the corpus |
| Not a decline: floating initializer of the wrong shape | `BackendInvariantException` (a compiler bug, not a refusal) | `LlvmEmitter.cs:75-77` | Code |
| Not a decline: block address of a block without a function | `BackendInvariantException` | `LlvmEmitter.cs:247-249` | Code |
| Not a decline: operand named by no one | `BackendInvariantException` | `LlvmEmitter.cs:263-265` | Code |

The `GOTO DWORD` program of `EmitLlvmTests.cs:51-57` is declined by the emitter's `IrFarEntry` case,
not by the lowering its comment names (`EmitLlvmTests.cs:44-47`): the run prints the far-entry message.

## 3. Type and ABI coverage

| Type or property | Emitted | Source | Evidence |
| --- | --- | --- | --- |
| `IrTypeKind.Void` | `void` | `LlvmEmitter.cs:278` | Run: every module |
| `IrTypeKind.Int` | `i<bits>` whatever the signedness; the IR has `i1` to `i512` (`IrType.cs:66-82`) | `LlvmEmitter.cs:279` | Run: `i1`, `i8`, `i16`, `i32` in the corpus |
| `IrTypeKind.Float`, IEEE 32 | `float` | `LlvmEmitter.cs:283` | Run: `DIFF120.BAS` |
| `IrTypeKind.Float`, IEEE 64 | `double` | `LlvmEmitter.cs:283` | Run: `DIFF120.BAS` |
| `IrTypeKind.Float`, x87 80 | `x86_fp80` | `LlvmEmitter.cs:283` | Run: `ARITH.BAS` |
| `IrTypeKind.Float`, other widths | `fp<bits>`, which is no LLVM type (finding F9) | `LlvmEmitter.cs:283` | Run: `llvm-as` rejects `define fp16 @f()` with "expected type"; not in the corpus |
| `IrTypeKind.Float`, MBF | declined (table 2) | `LlvmEmitter.cs:280-282` | Code |
| `IrTypeKind.Ptr` | `ptr`; the address space is dropped, although `IrType.ToString` prints `ptr addrspace(N)` (`IrType.cs:180`) | `LlvmEmitter.cs:284` | Run: no `addrspace` in any module |
| Any other kind | `void`; unreachable, `IrTypeKind` has four members | `LlvmEmitter.cs:285` | Code |
| Intrinsic suffix | `f<bits>` (`llvm.rint.f80`) | `LlvmEmitter.cs:275` | Run: `MINI.BAS` |
| Not emitted: `target datalayout` | never written | `LlvmEmitter.cs:49-53` | Run: no module has one |
| Not emitted: calling conventions | no `ccc`/`fastcc`/... on any `define`, `declare` or `call` | `LlvmEmitter.cs:96`, `LlvmEmitter.cs:161` | Run: none in any module |
| Not emitted: parameter and function attributes | no `nounwind`, `noundef`, `nonnull`, `#N` groups; the only attribute is `"reciprocal-estimates"` | `LlvmEmitter.cs:100`, `LlvmEmitter.cs:108-109` | Run: no `#` attribute group in any module |
| Not emitted: visibility and preemption | no `dso_local`, `hidden`, `protected` | `LlvmEmitter.cs:56`, `LlvmEmitter.cs:61`, `LlvmEmitter.cs:96` | Run: none in any module |
| Not emitted: linkage other than `private constant` and default | functions and typed globals take LLVM's default `external` | `LlvmEmitter.cs:56`, `LlvmEmitter.cs:61`, `LlvmEmitter.cs:96` | Run: `MINI.BAS` |
| Not emitted: alignment other than 1 | loads and stores `align 1`, allocas and globals none | `LlvmEmitter.cs:150-155` | Run: four fixtures |

## 4. Semantics notes

| Rule | What the code does | Source | Evidence |
| --- | --- | --- | --- |
| `align 1` | every load and store claims byte alignment, because packed UDT members may start at any byte and the IR carries no alignment fact | `LlvmEmitter.cs:151-155` | Run: 27 of 27 loads and stores of `MINI`, `ARITH`, `CTRL`, `ARRAY` |
| Rounding conversion | `FPToSIRound`/`FPToUIRound` call `llvm.rint` (round by the current mode, ties to even) before `fptosi`/`fptoui`, which truncate | `LlvmEmitter.cs:130-142` | Run: `MINI.BAS`, `DIFF05.BAS` (`fptoui x86_fp80 %rint.3 to i8`) |
| Undeclared intrinsic | `llvm.rint.*` is called without a `declare`; LLVM 23.1.3 accepts it | `LlvmEmitter.cs:140` | Run: `MINI.BAS` has no `declare` of `llvm.rint.f80` and passes `llvm-as`; a hand-written module with only `call double @llvm.rint.f64` passes too |
| Fast-math flags | all seven flags set is spelled `fast`, else `reassoc nnan ninf nsz arcp contract afn` in that order | `LlvmEmitter.cs:173-189` | Run: `reassoc nnan ninf nsz` (`DIFF110.BAS`) and `nnan ninf nsz` (`DIFF35.BAS`) accepted; `fast` not in the corpus |
| Fast-math on calls | `call` carries the flags of the instruction | `LlvmEmitter.cs:161` | Code |
| `"reciprocal-estimates"` | asked for per function only where an `fdiv` already allows `arcp` and `ninf`; the flags stay the legality gate | `LlvmEmitter.cs:191-214` | Code; not in the corpus |
| Variadic call typing | a call to a variadic callee spells the callee's function type | `LlvmEmitter.cs:159-161`, `LlvmEmitter.cs:227-232` | Run: `DIFF02.BAS` |
| `blockaddress` | the address of an `ON ERROR` handler; its function is never optimized, so the block stays intact | `LlvmEmitter.cs:245-249` | Run: 14 accepted modules |
| `indirectbr` | the targets are all labels of the same function | `LlvmEmitter.cs:166` | Run: `DIFF11.BAS` only, rejected for F1 |
| `undef` | rendered for `IrUndef`; an unnamed operand is never rendered as `undef`, it raises a `BackendInvariantException` | `LlvmEmitter.cs:244`, `LlvmEmitter.cs:258-265` | Code |
| Float constants | binary32/64 as the 64-bit pattern of the value, x87 as `0xK`; bit-exact | `LlvmEmitter.cs:241-242`, `LlvmEmitter.cs:299-301` | Run: `ARITH.BAS` |
| Signedness | on the instruction (`sdiv`/`udiv`, `slt`/`ult`, `sext`/`zext`, `fptosi`/`fptoui`), never on the type | `LlvmEmitter.cs:268-273`, `LlvmEmitter.cs:279` | Run: every module |
| Module name | `; ModuleID` is the source path exactly as given to `pbc` | `LlvmEmitter.cs:49` | Run: an absolute path on the command line appears verbatim |

## 5. Test inventory

Run at the pin: `dotnet test PowerBasic.Compiler.Tests -c Release --filter
"FullyQualifiedName~LlvmEmitterTests|FullyQualifiedName~EmitLlvmTests|FullyQualifiedName~EmitterNeverThrowsTests"`,
13 tests, 13 passed (16 s). The two tool tests call `Assert.Ignore` when the tool cannot be started
(`LlvmEmitterTests.cs:107-116`); here both tools were present and both tests ran.

| Test | Asserts | LLVM tool | Result | Source |
| --- | --- | --- | --- | --- |
| `Emit_AddFunction_UsesLlvmSpelling` | the exact text of a two-argument `add` function | none | passed | `LlvmEmitterTests.cs:27` |
| `Emit_FloatTypes_UseLlvmNames` | `float`, `double` and `fpext` spelling | none | passed | `LlvmEmitterTests.cs:45` |
| `Emit_Gep_UsesGetElementPtrI8Form` | `getelementptr i8, ptr %p, i32 4` | none | passed | `LlvmEmitterTests.cs:57` |
| `Emit_LoadAndStoreWithoutAlignmentFacts_UseConservativeByteAlignment` | `align 1` on a load and a store | none | passed | `LlvmEmitterTests.cs:67` |
| `LlvmAs_AcceptsTheEmittedModule` | `llvm-as` exits 0 for a lowered and optimized loop | `llvm-as` | passed | `LlvmEmitterTests.cs:83` |
| `Llc_LowersTheEmittedModuleToNativeX8664` | `llc -filetype=asm` exits 0 | `llc` | passed | `LlvmEmitterTests.cs:95` |
| `EmitLlvm_ForASupportedProgram_PrintsOptimizedLlvmModule` | exit 0, `define void @main()`, `target triple`, a `phi i16`, `sq` inlined away | none | passed | `EmitLlvmTests.cs:23` |
| `EmitLlvm_ForAnUnsupportedProgram_FailsWithADiagnostic` | `GOTO DWORD` program: exit 1, stderr names `--emit-llvm` | none | passed | `EmitLlvmTests.cs:51` |
| `Corpus_WhenEmittedAsCAndLlvm_ThenNothingThrows` | no `tests/*.BAS` makes either emitter throw | none | passed | `EmitterNeverThrowsTests.cs:70` |
| `Corpus_WhenEmittedAsCAndLlvm_ThenTheEmittersRenderedRealPrograms` | more than 50 corpus programs render per emitter | none | passed | `EmitterNeverThrowsTests.cs:95` |
| `GeneratedVariations_WhenEmittedAsCAndLlvm_ThenNothingThrows` | generated bodies in pb36, pb35, qb45 never throw | none | passed | `EmitterNeverThrowsTests.cs:211` |
| `GeneratedVariations_WhenEmitted_ThenEveryBodyBindsAndMostReachTheEmitters` | every generated body binds, more than half render | none | passed | `EmitterNeverThrowsTests.cs:244` |
| `FormerlyRaisingShapes_WhenEmitted_ThenTheyDeclineNamingTheConstruct` | far pointer and inline assembly decline by name under `--emit-llvm` | none | passed | `EmitterNeverThrowsTests.cs:311` |

No test of the three files runs `opt -passes=verify` or executes a module, and none passes an emitted
corpus module to `llvm-as`: `Corpus_WhenEmittedAsCAndLlvm_ThenTheEmittersRenderedRealPrograms` counts
exit codes only (`EmitterNeverThrowsTests.cs:100-104`), which is how F1 and F2 stayed unseen.

## 6. Findings

Gaps against PRD §26.3 and defects the run showed. Upstream defects are for `Hawkynt/PB-Compiler`;
the Polypiler issue named is the one whose copy or specification must not inherit them.

| # | Finding | Source | Evidence | Closed by |
| --- | --- | --- | --- | --- |
| F1 | Value names are not made unique within a function: two values with the same `Name` (two `FOR i%` loops, two `GOSUB` stacks) both become `%i`, and the module does not assemble | `LlvmEmitter.cs:120-127` | Run: 30 of 202 corpus modules, `llvm-as`: "multiple definition of local value named 'i'" (`DIFF36.BAS` in the characterization) | [#852](https://github.com/Hawkynt/Polypiler/issues/852); upstream |
| F2 | Block labels are written verbatim; a label with a space (`division by zero.ok7`) needs `%"..."` quoting | `LlvmEmitter.cs:112`, `LlvmEmitter.cs:163-164`, `LlvmEmitter.cs:217` | Run: `NARROW.BAS`, `llvm-as`: "expected ']' in phi value list" | [#852](https://github.com/Hawkynt/Polypiler/issues/852); upstream |
| F3 | No `target datalayout`; PRD §26.3 requires one, and the doc comment says a datalayout header is written | `LlvmEmitter.cs:43`, `LlvmEmitter.cs:49-53` | Run: no module has one | [#69](https://github.com/Hawkynt/Polypiler/issues/69), [#852](https://github.com/Hawkynt/Polypiler/issues/852) |
| F4 | The target triple is fixed to `x86_64-unknown-linux-gnu` by the driver; the CLI has no option to choose one | `Driver.cs:387` | Run: every module; `pbc --help` lists no triple option (`Driver.cs:795-817`) | [#69](https://github.com/Hawkynt/Polypiler/issues/69), [#852](https://github.com/Hawkynt/Polypiler/issues/852) |
| F5 | No calling convention, visibility (`dso_local`), parameter or function attributes; PRD §26.3 requires calling convention, symbol visibility and ABI attributes | `LlvmEmitter.cs:96-106`, `LlvmEmitter.cs:161` | Run: none in any module | [#69](https://github.com/Hawkynt/Polypiler/issues/69) |
| F6 | The pointer address space is dropped (`ptr` for every space); PRD §26.3 requires it | `LlvmEmitter.cs:284` | Code: `IrType.cs:180` prints `addrspace(N)`, the emitter does not | [#69](https://github.com/Hawkynt/Polypiler/issues/69) |
| F7 | MBF values are declined, not legalized; PRD §26.3 asks for explicit legalization or a capability diagnostic | `LlvmEmitter.cs:280-282` | Code | [#69](https://github.com/Hawkynt/Polypiler/issues/69) |
| F8 | A typed global that is not zero-initialized is written `@g = global T` with no initializer, which is no valid LLVM | `LlvmEmitter.cs:60-63` | Run: `llvm-as` rejects `@x = global i16` ("expected value token"); not in the corpus | [#852](https://github.com/Hawkynt/Polypiler/issues/852) |
| F9 | A float width other than 32, 64 or 80 is spelled `fp<bits>`, which is no LLVM type | `LlvmEmitter.cs:283` | Run: `llvm-as` rejects `fp16` ("expected type"); not in the corpus | [#852](https://github.com/Hawkynt/Polypiler/issues/852) |
| F10 | Intrinsics are called without a `declare`; accepted by LLVM 23.1.3, so the module depends on the reader declaring intrinsics implicitly | `LlvmEmitter.cs:140` | Run: `MINI.BAS` passes `llvm-as` 23.1.3 | [#69](https://github.com/Hawkynt/Polypiler/issues/69) (version policy) |
| F11 | `pbc --version` prints no version (`pbc: no source file`, exit 1), so the `pb-compiler` probe of `tooling/toolchains.lock` can never match the pin and every test that requires it stays inconclusive | `Driver.cs:105-106`, `Driver.cs:116-118` | Run: `pbc --version` at the pin | [#47](https://github.com/Hawkynt/Polypiler/issues/47) follow-up; upstream |
| F12 | The emitter tests run `llvm-as` and `llc` on two small programs only; PRD §26.3 asks for `opt`, native execution and a `.bc` round trip on every emitted module | `LlvmEmitterTests.cs:83-103` | Run: table 5 | [#851](https://github.com/Hawkynt/Polypiler/issues/851), [#859](https://github.com/Hawkynt/Polypiler/issues/859) |
| F13 | The comment of the `GOTO DWORD` test names the lowering as the stage that declines; the emitter declines it, on the far entry thunk | `EmitLlvmTests.cs:44-47`, `LlvmEmitter.cs:254-256` | Run: `GOTODW.BAS` | upstream (documentation) |
