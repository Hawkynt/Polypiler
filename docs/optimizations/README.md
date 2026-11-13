# Optimizations

Every optimization, recognition rule and lowering rewrite Polypiler plans or has, one page each:
what it does, an example before and after, when it applies, when it must **not** fire, and how
to switch it off (`--disable-opt O####`). A ticked box means it is implemented and verified;
the page then describes what the code does rather than what is planned.

**0 of 146 implemented.**

## General passes

- [ ] [O0001](O0001-constant-folding.md) — Fold operations on constants and integer neutral/absorbing elements (P1)
- [ ] [O0002](O0002-partial-evaluation.md) — Evaluate pure calls with constant arguments at compile time (P1)
- [ ] [O0003](O0003-sccp.md) — Sparse conditional constant propagation (P1)
- [ ] [O0004](O0004-global-value-numbering.md) — Global value numbering and common-subexpression elimination (P1)
- [ ] [O0005](O0005-dead-code-elimination.md) — Remove instructions whose results and effects are unobservable (P1)
- [ ] [O0006](O0006-inlining.md) — Inline direct calls under the cost model (P1)
- [ ] [O0007](O0007-interprocedural-effects.md) — Replace conservative call effects by proven callee summaries (P1)
- [ ] [O0008](O0008-range-and-known-bits-simplification.md) — Simplify operations decided by range and known-bit facts (P1)
- [ ] [O0009](O0009-strength-reduction.md) — Replace multiplications, divisions and remainders by constants with cheaper exact forms (P1)
- [ ] [O0010](O0010-copy-elision.md) — Elide aggregate and buffer copies whose source stays unchanged (P3)
- [ ] [O0011](O0011-dead-store-elimination.md) — Remove stores that are overwritten or never read (P3)
- [ ] [O0012](O0012-loop-invariant-code-motion.md) — Hoist loop-invariant computations that cannot trap or be invalidated (P3)
- [ ] [O0013](O0013-iteration-fusion.md) — Fuse map/filter/reduce pipelines into one loop without intermediate collections (P3)
- [ ] [O0014](O0014-redundant-allocation-removal.md) — Remove allocations that never escape and replace small ones by scalars (P3)
- [ ] [O0015](O0015-bounds-check-elimination.md) — Remove bounds checks proven redundant and attach a reusable bounds proof (P3)
- [ ] [O0016](O0016-operation-narrowing.md) — Execute operations at the narrowest width that preserves the result (P1)
- [ ] [O0017](O0017-field-packing-by-range.md) — Narrow and pack record fields to their proven value range (P3)
- [ ] [O0018](O0018-array-byte-and-bit-packing.md) — Pack array elements into bytes or bit fields by proven range (P3)
- [ ] [O0019](O0019-swar.md) — Execute small logical vectors as SWAR operations in one integer register (P3)
- [ ] [O0020](O0020-loop-vectorization.md) — Vectorize element-wise loops into logical vector operations (P3)
- [ ] [O0021](O0021-scalarization.md) — Scalarize logical vector operations lane by lane (P3)
- [ ] [O0022](O0022-exact-fma.md) — Contract multiply-add to FMA only when the result is provably identical (P3)
- [ ] [O0023](O0023-loop-unrolling.md) — Unroll counted loops under the cost model (P3)
- [ ] [O0024](O0024-loop-unswitching.md) — Hoist loop-invariant branches by versioning the loop (P3)
- [ ] [O0025](O0025-tail-recursion-elimination.md) — Turn self tail calls and accumulating recursion into loops (P3)
- [ ] [O0026](O0026-tensor-operation-fusion.md) — Fuse transposes and element-wise tensor operations without materializing intermediates (P5)
- [ ] [O0027](O0027-parallel-reduction.md) — Split reductions across threads when the combining operation is proven associative (P5)
- [ ] [O0028](O0028-loop-parallelization.md) — Run provably independent loop iterations on multiple threads (P5)
- [ ] [O0029](O0029-device-placement.md) — Place proven-pure tensor kernels on GPU or NPU when available, with a CPU fallback (P5)
- [ ] [O0030](O0030-integral-float-narrowing.md) — Narrow f64 values proven integral to integer types (P2)

## De-dynamicization

- [ ] [O0060](O0060-js-numeric-specialization.md) — Specialize JavaScript Number arithmetic to typed integer and float operations (P2)
- [ ] [O0061](O0061-js-array-specialization.md) — Specialize JavaScript arrays and typed arrays to typed sequences (P2)
- [ ] [O0062](O0062-python-int-specialization.md) — Specialize Python arbitrary-precision int to fixed-width integers or keep it exact (P3)
- [ ] [O0063](O0063-python-protocol-specialization.md) — Resolve Python protocols, attributes, slices and iterators statically where proven (P3)
- [ ] [O0064](O0064-perl-context-resolution.md) — Resolve Perl scalar, list and void context explicitly (P3)
- [ ] [O0065](O0065-perl-scalar-specialization.md) — Specialize Perl scalars: undef, numeric/string duality, references and tied variables (P3)
- [ ] [O0066](O0066-guarded-specialization.md) — Generate guarded specializations with a valid fallback path (P4)

## Legalization

- [ ] [O0070](O0070-pointer-emulation.md) — Emulate pointers on pointerless targets with indices, byte buffers and fat pointers (P3)
- [ ] [O0071](O0071-union-overlay-strategy.md) — Select and apply the union/overlay strategy each target documents (P3)
- [ ] [O0072](O0072-bitcast-and-packed-fields.md) — Legalize BitCast, Reinterpret, subvector access and PackedField (P3)
- [ ] [O0073](O0073-endian-load-store-legalization.md) — Legalize LoadLE/LoadBE and StoreLE/StoreBE per target byte order and alignment (P2)
- [ ] [O0074](O0074-logical-vector-legalization.md) — Legalize logical vectors of 16 to 4096 bits to native SIMD, polyfill, SWAR or scalar code (P3)
- [ ] [O0075](O0075-wide-integer-legalization.md) — Legalize integers wider than the target's native width into limbs (P2)
- [ ] [O0076](O0076-segmented-16-bit-indexing.md) — Legalize object sizes, indices and pointers to 16-bit segment constraints (P2)
- [ ] [O0077](O0077-target-semantics-lowering.md) — Lower division, float and bounds semantics that differ on the target (P2)
- [ ] [O0078](O0078-exception-lowering.md) — Lower exceptions and traps for targets without exception handling (P3)
- [ ] [O0079](O0079-base-operation-lowering.md) — Lower base operations a target lacks through one shared rule set (P2)

## Recognition and recovery

- [ ] [O0100](O0100-rotate-recognition.md) — Recognize rotate-left and rotate-right from shift pairs (P2)
- [ ] [O0101](O0101-popcount-loop-recognition.md) — Recognize population count from bit-counting loops (P2)
- [ ] [O0102](O0102-popcount-swar-recognition.md) — Recognize population count from the SWAR mask-and-add sequence (P3)
- [ ] [O0103](O0103-zero-count-loop-recognition.md) — Recognize leading and trailing zero counts from bit-scanning loops (P3)
- [ ] [O0104](O0104-zero-count-debruijn-recognition.md) — Recognize trailing zero count and floor-log2 from de Bruijn multiply-and-lookup (P3)
- [ ] [O0105](O0105-bit-reverse-recognition.md) — Recognize bit reversal from swap cascades and shift loops (P3)
- [ ] [O0106](O0106-byte-swap-recognition.md) — Recognize byte swap from shift-and-mask expressions and byte loops (P3)
- [ ] [O0107](O0107-pext-pdep-loop-recognition.md) — Recognize parallel bit extract and deposit from mask-driven loops (P3)
- [ ] [O0108](O0108-single-bit-recognition.md) — Recognize single-bit test, set, clear and flip with a variable index (P3)
- [ ] [O0109](O0109-sign-extension-recognition.md) — Recognize sign extension from shift pairs, xor-subtract and compare-select idioms (P3)
- [ ] [O0110](O0110-parity-recognition.md) — Recognize parity from xor folding, nibble lookup and toggling loops (P3)
- [ ] [O0120](O0120-min-max-recognition.md) — Recognize minimum and maximum from branchless mask selection and compare-select (P3)
- [ ] [O0121](O0121-abs-recognition.md) — Recognize absolute value from sign-mask idioms and compare-select (P3)
- [ ] [O0122](O0122-signum-recognition.md) — Recognize signum from comparison-difference, shift-or and select idioms (P3)
- [ ] [O0123](O0123-clamp-recognition.md) — Recognize clamp from nested minimum/maximum and compare-select chains (P2)
- [ ] [O0124](O0124-xor-swap-recognition.md) — Recognize an exchange of two memory cells from the three-xor sequence (P3)
- [ ] [O0125](O0125-shift-add-multiply-recognition.md) — Recognize multiplication by a constant from shift-add and shift-subtract chains (P3)
- [ ] [O0126](O0126-reciprocal-division-recognition.md) — Recognize division and remainder by a constant from multiply-high sequences (P3)
- [ ] [O0127](O0127-divmod-fusion.md) — Fuse a division and a remainder of the same operands into DivMod (P2)
- [ ] [O0128](O0128-saturating-arithmetic-recognition.md) — Recognize saturating addition and subtraction from overflow-test and widen-clamp idioms (P3)
- [ ] [O0129](O0129-average-recognition.md) — Recognize overflow-free average from and-xor-shift and widen-add-shift idioms (P3)
- [ ] [O0130](O0130-carry-chain-recognition.md) — Recognize multi-precision addition and subtraction with carry from limb idioms (P3)
- [ ] [O0131](O0131-widening-multiply-recognition.md) — Recognize widening multiplication and multiply-high from half-width partial products (P3)
- [ ] [O0140](O0140-endian-load-recognition.md) — Recognize little- and big-endian loads from byte loads combined by shifts (P2)
- [ ] [O0141](O0141-endian-store-recognition.md) — Recognize little- and big-endian stores from shifted byte stores (P3)
- [ ] [O0142](O0142-byte-xor-loop-recognition.md) — Recognize byte-wise XOR loops over two buffers (P2)
- [ ] [O0143](O0143-copy-loop-recognition.md) — Recognize element copy loops as Memory.Copy, Memory.Move or Memory.CopyForward (P2)
- [ ] [O0144](O0144-fill-loop-recognition.md) — Recognize element fill loops as Memory.Fill (P2)
- [ ] [O0145](O0145-compare-loop-recognition.md) — Recognize early-exit comparison loops as Memory.Equal or Memory.Compare (P3)
- [ ] [O0146](O0146-crc-bitwise-recognition.md) — Recognize bitwise CRC loops as a parameterized CRC update (P3)
- [ ] [O0147](O0147-crc-table-recognition.md) — Recognize table-driven CRC loops as a parameterized CRC update (P3)
- [ ] [O0150](O0150-index-loop-map-recognition.md) — Recognize element-wise index loops as Map (P2)
- [ ] [O0151](O0151-conditional-append-filter-recognition.md) — Recognize conditional-append loops as Filter (P2)
- [ ] [O0152](O0152-accumulator-loop-reduce-recognition.md) — Recognize accumulator loops as Reduce with a recorded associativity (P2)
- [ ] [O0153](O0153-pointer-loop-index-recognition.md) — Rewrite pointer-increment loops as index loops over an iteration domain (P3)
- [ ] [O0154](O0154-linq-normalization.md) — Normalize C# LINQ query operators into the shared iteration IR (P3)
- [ ] [O0155](O0155-js-array-method-normalization.md) — Normalize JavaScript array methods into the shared iteration IR (P4)
- [ ] [O0156](O0156-perl-map-grep-normalization.md) — Normalize Perl map and grep into the shared iteration IR (P4)
- [ ] [O0157](O0157-python-comprehension-normalization.md) — Normalize Python comprehensions and generator pipelines into the shared iteration IR (P4)
- [ ] [O0158](O0158-rust-iterator-normalization.md) — Normalize Rust iterator adaptor chains into the shared iteration IR (P4)
- [ ] [O0159](O0159-java-stream-normalization.md) — Normalize Java stream pipelines into the shared iteration IR (P4)
- [ ] [O0160](O0160-dot-product-recognition.md) — Recognize dot products from multiply-accumulate reductions (P3)
- [ ] [O0161](O0161-matrix-multiply-recognition.md) — Recognize matrix multiplication from triple-nested multiply-accumulate loops (P3)
- [ ] [O0162](O0162-convolution-recognition.md) — Recognize one- and two-dimensional convolution and image kernels from tap-accumulation loop nests (P3)
- [ ] [O0163](O0163-lerp-recognition.md) — Recognize linear interpolation idioms with their exact evaluation form (P3)
- [ ] [O0164](O0164-exact-fma-recognition.md) — Recognize fused multiply-add only where it is exactly equivalent or the source semantics are fused (P3)
- [ ] [O0165](O0165-sincos-fusion.md) — Fuse sine and cosine of the same argument into SinCos (P4)
- [ ] [O0170](O0170-full-unroll-recovery.md) — Recover a counted iteration from 2-, 4- and 8-way fully unrolled straight-line statements (P5)
- [ ] [O0171](O0171-partial-unroll-recovery.md) — Recover unit-step loops from partially unrolled loops (P5)
- [ ] [O0172](O0172-remainder-peel-recovery.md) — Merge remainder, epilogue and peeled iterations into the main iteration domain (P5)
- [ ] [O0173](O0173-interleaved-chain-slicing.md) — Slice interleaved arithmetic into independent dependence chains (P5)
- [ ] [O0174](O0174-isomorphic-chain-vector-candidates.md) — Form lane-isolated vector candidates from isomorphic independent chains (P5)
- [ ] [O0175](O0175-software-pipeline-recovery.md) — Recover the logical loop from two-stage software-pipelined loops (P5)
- [ ] [O0176](O0176-duffs-device-recovery.md) — Recover a counted loop from Duff's device (P5)
- [ ] [O0177](O0177-packed-layout-recovery.md) — Recover packed-field layouts of private storage under memory-ordering proofs (P5)
- [ ] [O0178](O0178-inline-assembly-fusion.md) — Fuse fully recognized inline assembly with the surrounding source into canonical SSA (P5)
- [ ] [O0179](O0179-cross-function-iteration-synthesis.md) — Synthesize iterations across function boundaries under call-effect summaries (P5)

## Bit permutations

- [ ] [O0200](O0200-shift-mask-mapping-recovery.md) — Recover a bit mapping from masked, shifted and rotated copies of one value (P3)
- [ ] [O0201](O0201-rotation-naming.md) — Name a recovered bit mapping that is a rotation (P3)
- [ ] [O0202](O0202-byte-swap-naming.md) — Name a recovered bit mapping that is a byte reversal (P3)
- [ ] [O0203](O0203-bit-reverse-naming.md) — Name a recovered bit mapping that is a bit reversal (P3)
- [ ] [O0204](O0204-compress-expand-naming.md) — Name a recovered bit routing that is a compress or an expand (P3)
- [ ] [O0205](O0205-multiply-routing-recovery.md) — Recover a bit routing from a multiplication after an exact carry proof (P3)
- [ ] [O0206](O0206-layout-composition.md) — Compose consecutive layout steps into one bit mapping (P3)
- [ ] [O0207](O0207-identity-mapping-elimination.md) — Remove a bit mapping that is the identity, so that inverse pairs cancel (P3)
- [ ] [O0208](O0208-non-bijective-routing-recovery.md) — Recover routings that duplicate, invert or set bits as BitRoute, never as a permutation (P3)
- [ ] [O0210](O0210-bit-mapping-lowering.md) — Lower BitPermute and BitRoute to a proven network chosen for the target (P3)

## Language-construct lowering and recovery

- [ ] [O0230](O0230-overload-resolution.md) — Resolve overloaded calls to direct calls with explicit conversions and mangled names (P3)
- [ ] [O0231](O0231-generic-monomorphization.md) — Monomorphize closed generic types and functions (P3)
- [ ] [O0232](O0232-operator-binding.md) — Bind overloaded operators and user-defined conversions to named functions (P3)
- [ ] [O0233](O0233-default-named-arguments.md) — Expand default and named arguments into positional calls (P3)
- [ ] [O0234](O0234-variadic-lowering.md) — Lower variadic parameters to arrays, fixed-arity clones or promoted argument slots (P3)
- [ ] [O0235](O0235-structural-record-lowering.md) — Lower tuples, anonymous and structural records to named records with generated equality (P3)
- [ ] [O0236](O0236-tagged-union-lowering.md) — Lower tagged unions and payload-carrying enums to tag-plus-payload records with checked accessors (P3)
- [ ] [O0237](O0237-class-lowering.md) — Lower classes with fields, methods and constructors to records, procedures and type tags (P3)
- [ ] [O0238](O0238-static-member-lowering.md) — Lower static members to module storage with ordered or guarded initialization (P3)
- [ ] [O0239](O0239-interface-dispatch.md) — Lower interface calls to tag dispatch trees or per-type dispatch tables (P3)
- [ ] [O0240](O0240-virtual-dispatch.md) — Lower single inheritance and virtual calls to prefix records and per-type dispatch (P4)
- [ ] [O0241](O0241-devirtualization.md) — Replace virtual and interface calls by direct calls when the receiver type is proven (P4)
- [ ] [O0242](O0242-multiple-inheritance.md) — Lower non-virtual multiple inheritance to embedded base records with reference adjustment (P4)
- [ ] [O0243](O0243-virtual-base.md) — Lower virtual base classes to one shared subobject located through the complete type (P4)
- [ ] [O0244](O0244-mro-resolution.md) — Resolve members and cooperative super calls through a linearized method resolution order (P4)
- [ ] [O0245](O0245-closure-conversion.md) — Lower closures to environment records and lifted functions (P4)
- [ ] [O0246](O0246-pattern-decision-tree.md) — Lower type-switch and pattern-matching expressions to ordered decision trees (P3)
- [ ] [O0247](O0247-typed-exception-lowering.md) — Lower typed handlers, filters, exception data and rethrow to status-code form (P4)
- [ ] [O0248](O0248-handler-frame-jump.md) — Lower exception propagation to handler frames with a non-local jump (P4)
- [ ] [O0249](O0249-on-error-recovery-edges.md) — Lower On Error modes and Resume to explicit per-statement recovery edges (P4)
- [ ] [O0250](O0250-resource-scope-lowering.md) — Lower policy-carrying resource scopes to native constructs or explicit cleanup regions (P4)
- [ ] [O0251](O0251-cleanup-on-exit-edges.md) — Run cleanup regions on every non-exceptional exit edge in source order (P4)
- [ ] [O0252](O0252-generator-state-machine.md) — Lower generators to resumable state records with an entry function (P4)
- [ ] [O0253](O0253-coroutine-state-machine.md) — Lower coroutines with sent values, failure and cancellation to resumable state machines (P4)
- [ ] [O0254](O0254-async-state-machine.md) — Lower async functions to state machines over an explicit scheduler contract (P4)
- [ ] [O0255](O0255-state-machine-recovery.md) — Recover generators from hand-compiled state machines (P5)
- [ ] [O0256](O0256-dispatch-table-recovery.md) — Recover interface calls from constant function-pointer dispatch tables (P5)
- [ ] [O0257](O0257-resource-scope-recovery.md) — Recover resource scopes from acquire/cleanup paths and try/finally disposal (P5)
- [ ] [O0258](O0258-error-code-recovery.md) — Recover exceptions from manual error-code plumbing (P5)
- [ ] [O0259](O0259-slice-adapter-recovery.md) — Recover slice parameters and adapter functions from pointer-plus-length interop shapes (P5)

## Interop and layouts

- [ ] [O0260](O0260-marshalling-fusion.md) — Fuse marshalling across adjacent foreign calls into one packed buffer (P3)
- [ ] [O0261](O0261-demanded-field-unpack.md) — Unpack only the fields that are demanded after a foreign call (P3)

## Candidates to borrow

These 144 entries are **not planned** and are not counted above. They are the language-agnostic
optimizations of [PB-Compiler](https://github.com/Hawkynt/PB-Compiler) that no page in this directory covers,
taken from the [PB-Compiler optimization catalog](pb-compiler-catalog.md), which classifies all 424 upstream
documents. A candidate gets an `O####` number, a page and an issue when it is scheduled; until then the link
leads to its catalog row and the identifier shown is PB-Compiler's.

### Loops

- *candidate* — [PB O0020](pb-compiler-catalog.md#pb-o0020) Algorithmic idiom replacement: Delete a counted loop with a known finite trip count whose body has no observable effect.
- *candidate* — [PB O0030](pb-compiler-catalog.md#pb-o0030) Induction-variable strength reduction: Carry an element address as its own induction variable advanced by scale times step.
- *candidate* — [PB O0037](pb-compiler-catalog.md#pb-o0037) Fixed-point FOR counters: Run a float loop counter whose bounds and step share a power-of-two grid as a scaled integer, materializing the float only where the body reads it.
- *candidate* — [PB O0062](pb-compiler-catalog.md#pb-o0062) Loop rotation, IV simplification and fusion: Rotate pre-test loops into guarded post-test form, rewrite derived induction variables, and fuse adjacent loops with equal bounds.
- *candidate* — [PB O0073](pb-compiler-catalog.md#pb-o0073) Wider algorithmic idiom catalog: Recognize sort and linear-search loops as the corresponding library operation.
- *candidate* — [PB O0110](pb-compiler-catalog.md#pb-o0110) General induction-variable strength reduction: Replace every affine function of an induction variable by an incrementally updated value, not only named access shapes.
- *candidate* — [PB O0111](pb-compiler-catalog.md#pb-o0111) Redundant induction-variable elimination: Express one of two induction variables advancing in lockstep as an affine function of the other and drop it.
- *candidate* — [PB O0112](pb-compiler-catalog.md#pb-o0112) Countdown loop with `DEC`/`JNZ`: Count a fixed-trip loop down to zero when the body never reads the counter, so the exit test is a zero test.
- *candidate* — [PB O0115](pb-compiler-catalog.md#pb-o0115) Loop peeling: Peel the first iteration so a first-iteration-only branch is decided in both the peeled copy and the remaining loop.
- *candidate* — [PB O0116](pb-compiler-catalog.md#pb-o0116) Loop guard hoisting: Test the zero-trip case once in a guard and run the loop bottom-tested.
- *candidate* — [PB O0118](pb-compiler-catalog.md#pb-o0118) Loop dead-store elimination: Sink a store that the next iteration overwrites unread out of the loop, so it runs once with the final value.
- *candidate* — [PB O0120](pb-compiler-catalog.md#pb-o0120) Multiple accumulators break the dependency chain: Split a reduction with a proven associative operation into several independent accumulators combined after the loop.
- *candidate* — [PB O0122](pb-compiler-catalog.md#pb-o0122) Loop interchange: Swap the nesting order of two loops when dependences allow and the traversal becomes contiguous.
- *candidate* — [PB O0123](pb-compiler-catalog.md#pb-o0123) Loop distribution and fission: Split a loop into several over the same range so that a vectorizable part is separated from the rest.
- *candidate* — [PB O0124](pb-compiler-catalog.md#pb-o0124) Loop tiling (blocking): Iterate a loop nest in blocks sized to the cache so loaded values are reused before eviction.
- *candidate* — [PB O0125](pb-compiler-catalog.md#pb-o0125) Loop skewing: Skew the iteration space so a diagonal dependence becomes rectangular and the inner loop dependence-free.
- *candidate* — [PB O0126](pb-compiler-catalog.md#pb-o0126) Unroll and jam: Unroll the outer loop and fuse the copies of the inner loop into one body.
- *candidate* — [PB O0130](pb-compiler-catalog.md#pb-o0130) Runtime trip-count versioning: Emit several versions of a loop (scalar, unrolled, vectorized) and select one by a run-time test of the trip count.
- *candidate* — [PB O0133](pb-compiler-catalog.md#pb-o0133) Loop prefix evaluation: Evaluate the first N iterations of a loop at compile time and start the run-time loop from iteration N.
- *candidate* — [PB O0134](pb-compiler-catalog.md#pb-o0134) Loop-carried dependency shortening and closed forms: Replace a recurrence by its closed form where one exists (for example an arithmetic series) and shorten the remaining carried dependence.
- *candidate* — [PB O0228](pb-compiler-catalog.md#pb-o0228) Arithmetic-series folding: Replace a loop that sums its own counter by the closed-form total.
- *candidate* — [PB O0306](pb-compiler-catalog.md#pb-o0306) Loop versioning: Keep the general loop and add a clone without its per-iteration checks, selected by one preheader guard.
- *candidate* — [PB O0313](pb-compiler-catalog.md#pb-o0313) Parallel prefix scan: Parallelize a prefix scan in two passes: per-slice reduction, then per-slice scan with the slice offset.
- *candidate* — [PB O0315](pb-compiler-catalog.md#pb-o0315) Pipeline parallelization: Run the stages of a loop concurrently with buffers between them.
- *candidate* — [PB O0316](pb-compiler-catalog.md#pb-o0316) Parallel loop collapse: Flatten a loop nest into one iteration space so it can be divided among workers.
- *candidate* — [PB O0334](pb-compiler-catalog.md#pb-o0334) Binary-search recognition: Replace a linear scan over data the compiler knows to be sorted by a binary search.
- *candidate* — [PB O0335](pb-compiler-catalog.md#pb-o0335) Perfect-hash generation for static key sets: Canonicalize a search loop over a fixed unique key set into a multi-way dispatch.
- *candidate* — [PB O0407](pb-compiler-catalog.md#pb-o0407) Dead loop elimination: Delete a loop that has no remaining observable effect once its carried values are replaced by closed forms.

### Redundancy

- *candidate* — [PB O0061](pb-compiler-catalog.md#pb-o0061) Reassociation: Reorder integer add/multiply chains into a canonical shape so equal subtrees meet in value numbering.
- *candidate* — [PB O0121](pb-compiler-catalog.md#pb-o0121) Reduction tree balancing: Rebalance a chain of an associative operation into a tree of logarithmic depth.
- *candidate* — [PB O0338](pb-compiler-catalog.md#pb-o0338) Reciprocal reuse across repeated divisions: Compute one reciprocal of an invariant divisor and multiply, where the operation's numeric contract permits it.
- *candidate* — [PB O0344](pb-compiler-catalog.md#pb-o0344) Floating-point reassociation: Rebalance float add and multiply trees, only under an explicit source permission to reassociate.
- *candidate* — [PB O0345](pb-compiler-catalog.md#pb-o0345) Common-denominator factoring: Divide once and multiply several times by the reciprocal of a shared divisor, where the numeric contract permits it.

### Propagation

- *candidate* — [PB O0075](pb-compiler-catalog.md#pb-o0075) Silent fixed-point arithmetic: Run float computations with a proven constant power-of-two scale in scaled integers, converting only where the value is observed.
- *candidate* — [PB O0077](pb-compiler-catalog.md#pb-o0077) Negation idioms: Canonicalize `0 - x` and `x * -1` to negation and cancel double negation.
- *candidate* — [PB O0078](pb-compiler-catalog.md#pb-o0078) General multiply decomposition: Decompose a multiplication by any small constant into shifts and adds chosen by the target cost model.
- *candidate* — [PB O0107](pb-compiler-catalog.md#pb-o0107) Branch folding through phi: Specialize a join that branches on a phi per incoming edge, so each predecessor jumps straight to the decided successor.
- *candidate* — [PB O0132](pb-compiler-catalog.md#pb-o0132) Whole-loop compile-time evaluation: Execute a loop whose every input is known and whose effects stay in known variables at compile time and emit its final state as data.
- *candidate* — [PB O0156](pb-compiler-catalog.md#pb-o0156) Path-sensitive value propagation: Keep value facts per incoming path up to a budget and decide later tests per path.
- *candidate* — [PB O0189](pb-compiler-catalog.md#pb-o0189) Multiply by `2^a ± 2^b`: Multiply by a constant with few set bits, or a run of them, as a sum or difference of shifts.
- *candidate* — [PB O0192](pb-compiler-catalog.md#pb-o0192) Parity / zero-test modulo mask: Test `(x rem 2^n) = 0` as `(x and (2^n - 1)) = 0` for either signedness.
- *candidate* — [PB O0305](pb-compiler-catalog.md#pb-o0305) Basic-block versioning: Duplicate a CFG region per fact set and route execution into the copy where the fact holds.
- *candidate* — [PB O0308](pb-compiler-catalog.md#pb-o0308) Speculative overflow elimination: Establish by one guard on the inputs that a region cannot overflow and run it unchecked, keeping the checked version as fallback.
- *candidate* — [PB O0309](pb-compiler-catalog.md#pb-o0309) Speculative integer narrowing: Guard loop-invariant inputs by a range test and run the loop's wide arithmetic at a narrower width under it.
- *candidate* — [PB O0310](pb-compiler-catalog.md#pb-o0310) Side exits and deoptimization: Leave specialized code through a side exit that reconstructs the general state at the failing point.
- *candidate* — [PB O0337](pb-compiler-catalog.md#pb-o0337) Horner / Estrin polynomial evaluation: Evaluate an integer polynomial in Horner or Estrin form.
- *candidate* — [PB O0343](pb-compiler-catalog.md#pb-o0343) Transcendental function specialization: Specialize a transcendental call by its proven argument domain, down to a table for a finite discrete domain.
- *candidate* — [PB O0346](pb-compiler-catalog.md#pb-o0346) Floating-point classification simplification: Decide ordered float comparisons from proven sign, finiteness and NaN-freedom facts.
- *candidate* — [PB O0347](pb-compiler-catalog.md#pb-o0347) Mixed-precision computation: Execute `fptrunc(op(fpext a, fpext b))` for add, subtract, multiply and divide directly in the narrow format.
- *candidate* — [PB O0350](pb-compiler-catalog.md#pb-o0350) Overflow-check coalescing: Replace the overflow checks of a chain of operations by one range guard on its inputs.

### Memory and data layout

- *candidate* — [PB O0011](pb-compiler-catalog.md#pb-o0011) Literal overlap pooling: Give a literal whose bytes occur inside an already placed literal no storage of its own (needs length-addressed literals).
- *candidate* — [PB O0015](pb-compiler-catalog.md#pb-o0015) UDT zero-cost copy and compare: Expand a small constant-size block copy into scalar load/store pairs within the target's store budget.
- *candidate* — [PB O0023](pb-compiler-catalog.md#pb-o0023) Dead global / data tree-shaking: Remove globals that are never read, together with the pure stores into them.
- *candidate* — [PB O0138](pb-compiler-catalog.md#pb-o0138) Overlapping load combining: Serve loads from overlapping addresses (a sliding window) by one wider load plus shift or mask.
- *candidate* — [PB O0163](pb-compiler-catalog.md#pb-o0163) Dead field and component elimination: Remove a record field that no reachable code reads, together with its stores, from every instance.
- *candidate* — [PB O0173](pb-compiler-catalog.md#pb-o0173) Speculative load hoisting: Hoist a load above a store or out of a conditional under a run-time alias test or a proof that it cannot fault.
- *candidate* — [PB O0215](pb-compiler-catalog.md#pb-o0215) UDT self-copy elision: Drop a block copy whose source and destination are the same location.
- *candidate* — [PB O0216](pb-compiler-catalog.md#pb-o0216) UDT self-compare folding: Fold an equality comparison of a memory block with itself to its constant result.
- *candidate* — [PB O0253](pb-compiler-catalog.md#pb-o0253) Store sinking: Sink a store out of a conditional (merging the arms' stores at the join) or out of a loop.
- *candidate* — [PB O0285](pb-compiler-catalog.md#pb-o0285) Program-wide constant data merging: Share identical read-only data blocks and place a block contained in another inside it.
- *candidate* — [PB O0287](pb-compiler-catalog.md#pb-o0287) Stack promotion: Place a non-escaping allocation of bounded size in the frame instead of the heap.
- *candidate* — [PB O0288](pb-compiler-catalog.md#pb-o0288) Allocation sinking: Move an allocation that is used only on a rare path onto that path.
- *candidate* — [PB O0290](pb-compiler-catalog.md#pb-o0290) Temporary reuse across loop iterations: Allocate a loop-body temporary of stable size once before the loop and reuse it.
- *candidate* — [PB O0320](pb-compiler-catalog.md#pb-o0320) Array of structs → struct of arrays: Store an array of records as one array per field when loops touch single fields.
- *candidate* — [PB O0321](pb-compiler-catalog.md#pb-o0321) Field reordering: Order record fields so frequently accessed ones are adjacent and padding is minimal.
- *candidate* — [PB O0322](pb-compiler-catalog.md#pb-o0322) Hot/cold field splitting: Split rarely used fields of a record into a separate cold record.
- *candidate* — [PB O0324](pb-compiler-catalog.md#pb-o0324) Pointer compression: Store a pointer whose targets lie in one bounded region as a narrower offset or index.
- *candidate* — [PB O0325](pb-compiler-catalog.md#pb-o0325) Array padding for alignment: Align an array's base to the vector width and round its length up to whole vectors, so no peel or tail is needed.
- *candidate* — [PB O0327](pb-compiler-catalog.md#pb-o0327) Data transposition: Store multidimensional data in the order the fixed loop nest traverses it.
- *candidate* — [PB O0329](pb-compiler-catalog.md#pb-o0329) Array contraction: Contract an array of which later iterations read only the last few elements to that many scalars.
- *candidate* — [PB O0332](pb-compiler-catalog.md#pb-o0332) Lookup-table generation: Evaluate a pure function over its whole small domain at compile time and replace calls by an indexed load.
- *candidate* — [PB O0333](pb-compiler-catalog.md#pb-o0333) Lookup-table elimination: Replace a table whose contents are a simple function of the index by that computation.
- *candidate* — [PB O0339](pb-compiler-catalog.md#pb-o0339) Memory routine specialization by size: Choose straight-line accesses, a block move or the runtime routine for a copy by its known size.

### Vectorization

- *candidate* — [PB O0137](pb-compiler-catalog.md#pb-o0137) Load widening across unrolled iterations: Load a wide word once and consume its lanes across unrolled iterations when the lanes can stay packed.
- *candidate* — [PB O0139](pb-compiler-catalog.md#pb-o0139) Alignment peeling and access versioning: Peel scalar iterations until the access is aligned, or version the loop on a run-time alignment test.
- *candidate* — [PB O0144](pb-compiler-catalog.md#pb-o0144) Interleaved-access vectorization: Vectorize strided channel loops by de-interleaving loaded vectors, operating per channel and re-interleaving.
- *candidate* — [PB O0148](pb-compiler-catalog.md#pb-o0148) Packed narrow vs widening vector arithmetic: Keep vector lanes at the element width when value ranges prove no lane overflows, widening only otherwise.
- *candidate* — [PB O0150](pb-compiler-catalog.md#pb-o0150) Vector compare, select, min/max and absolute value: Vectorize a loop body with a per-element conditional as a lane mask and select.
- *candidate* — [PB O0152](pb-compiler-catalog.md#pb-o0152) Runtime dependence-check versioning: Guard a vector loop by a run-time overlap test of the accessed ranges, with the scalar loop as fallback.
- *candidate* — [PB O0154](pb-compiler-catalog.md#pb-o0154) SWAR search idioms: Search for a byte several positions at a time with the zero-byte and broadcast-compare word tricks.
- *candidate* — [PB O0251](pb-compiler-catalog.md#pb-o0251) Misaligned access versioning: Emit an aligned fast path and an unaligned fallback selected by a run-time test of the address.
- *candidate* — [PB O0252](pb-compiler-catalog.md#pb-o0252) Safe over-read versioning: Permit a widened load past the last element only where accessible padding is proven, and run a scalar tail otherwise.
- *candidate* — [PB O0254](pb-compiler-catalog.md#pb-o0254) Masked vector tail: Run the remainder of a vector loop in the vector unit with out-of-range lanes masked off.
- *candidate* — [PB O0255](pb-compiler-catalog.md#pb-o0255) Overlapping final vector: Cover the remainder of a vector loop by one final vector that overlaps elements already processed, where recomputation is idempotent.

### Interprocedural

- *candidate* — [PB O0018](pb-compiler-catalog.md#pb-o0018) Interprocedural constant propagation: Replace a parameter that receives the same constant at every call site by that literal in the callee.
- *candidate* — [PB O0022](pb-compiler-catalog.md#pb-o0022) Dead procedure elimination: Remove functions that are neither called nor address-taken after the middle end has run.
- *candidate* — [PB O0040](pb-compiler-catalog.md#pb-o0040) Identical-code folding (link-time tail merge): Merge functions whose optimized bodies are identical, rebinding the duplicates' entry points to one copy.
- *candidate* — [PB O0054](pb-compiler-catalog.md#pb-o0054) IR: global dead-code elimination: Remove unreferenced functions and globals of a module, repeating until nothing more becomes unreferenced.
- *candidate* — [PB O0069](pb-compiler-catalog.md#pb-o0069) Dead parameters and call-shape cloning: Remove parameters no path in the callee reads from the signature and every call site, and clone a callee per constant argument shape.
- *candidate* — [PB O0159](pb-compiler-catalog.md#pb-o0159) Return-value propagation: Replace a call to a function that returns the same constant on every path by that constant, and carry the result's range to call sites.
- *candidate* — [PB O0160](pb-compiler-catalog.md#pb-o0160) Call-site cloning by range, alignment and aliasing: Clone a procedure per materially different call shape so each clone keeps sharp facts.
- *candidate* — [PB O0165](pb-compiler-catalog.md#pb-o0165) Read-only global propagation: Fold every read of a never-written global with a constant initializer to the literal.
- *candidate* — [PB O0201](pb-compiler-catalog.md#pb-o0201) Fully-inlined procedure purge: Remove a function once inlining has left it without callers or taken address.
- *candidate* — [PB O0278](pb-compiler-catalog.md#pb-o0278) Global variable localization: Turn a global that only one function touches into a local of that function.
- *candidate* — [PB O0279](pb-compiler-catalog.md#pb-o0279) Whole-program devirtualization: Resolve an indirect call whose complete target set has exactly one member to a direct call.
- *candidate* — [PB O0280](pb-compiler-catalog.md#pb-o0280) Argument structure reduction: Pass only the fields a callee reads instead of the whole record, at every call site.
- *candidate* — [PB O0281](pb-compiler-catalog.md#pb-o0281) Return structure reduction: Return only the fields callers read and drop the stores and computations for the rest.
- *candidate* — [PB O0283](pb-compiler-catalog.md#pb-o0283) Context-sensitive cloning: Clone a procedure for an important caller so that caller's facts are not joined with the others'.
- *candidate* — [PB O0284](pb-compiler-catalog.md#pb-o0284) Semantic function merging: Merge functions that differ in one constant, field offset or call target into one body with an extra parameter.
- *candidate* — [PB O0307](pb-compiler-catalog.md#pb-o0307) Speculative devirtualization: Version an indirect call into a guarded direct call to a statically likely target plus the indirect fallback.
- *candidate* — [PB O0314](pb-compiler-catalog.md#pb-o0314) Task-graph extraction: Run calls proven independent by their effect summaries concurrently.

### Code layout

- *candidate* — [PB O0029](pb-compiler-catalog.md#pb-o0029) `SELECT CASE` → jump table: Dispatch a dense integer multi-way branch through an index table under case-count and span thresholds.
- *candidate* — [PB O0032](pb-compiler-catalog.md#pb-o0032) Short-circuit `AND` / `OR` / `NOT` conditions: Lower a branch on an AND/OR/NOT tree of pure comparisons to a chain of conditional branches instead of materialized truth values.
- *candidate* — [PB O0051](pb-compiler-catalog.md#pb-o0051) IR: if-conversion: Turn a diamond with empty arms into `select` values at the merge.
- *candidate* — [PB O0052](pb-compiler-catalog.md#pb-o0052) IR: CFG simplification: Canonicalize the CFG: remove trivial phis and forwarding blocks, merge straight-line block chains.
- *candidate* — [PB O0067](pb-compiler-catalog.md#pb-o0067) `IF`-chain → jump table: Recognize a chain of mutually exclusive equality tests on one value as a multi-way branch, so it can take the table dispatch.
- *candidate* — [PB O0093](pb-compiler-catalog.md#pb-o0093) Jump threading: Retarget a branch whose destination is an unconditional branch to the final destination, transitively.
- *candidate* — [PB O0094](pb-compiler-catalog.md#pb-o0094) Conditional branch inversion: Invert a condition and swap the arms where that removes the jump over the else arm on the common path.
- *candidate* — [PB O0095](pb-compiler-catalog.md#pb-o0095) Common branch-tail merging: Keep one copy of an instruction suffix shared by sibling arms and branch into it.
- *candidate* — [PB O0096](pb-compiler-catalog.md#pb-o0096) Adjacent and nested condition combining: Merge directly nested conditionals into one branch chain.
- *candidate* — [PB O0098](pb-compiler-catalog.md#pb-o0098) Sparse `SELECT` → balanced decision tree: Dispatch a sparse multi-way branch through a balanced binary decision tree over the sorted case values.
- *candidate* — [PB O0099](pb-compiler-catalog.md#pb-o0099) Bit-test dispatch for small constant sets: Test membership in a small constant set with one shift-and-mask against a compile-time bit mask.
- *candidate* — [PB O0100](pb-compiler-catalog.md#pb-o0100) Perfect-hash dispatch: Index a compact table through a collision-free arithmetic hash of the case value.
- *candidate* — [PB O0101](pb-compiler-catalog.md#pb-o0101) Jump-table sharing and compression: Drop the range guard of a dispatch dominated by another dispatch on the same subject, and store table entries at the narrowest width that reaches every target.
- *candidate* — [PB O0104](pb-compiler-catalog.md#pb-o0104) Branch probability inference and block placement: Infer likely edges (back edges, early exits, error arms) and place the common path as one fall-through trace.
- *candidate* — [PB O0105](pb-compiler-catalog.md#pb-o0105) Hot/cold block splitting: Move error and rarely taken arms out of the hot block sequence to the end of the function or a cold region.
- *candidate* — [PB O0106](pb-compiler-catalog.md#pb-o0106) Trace formation and superblock tail duplication: Form traces along likely paths and duplicate small join blocks into their predecessors to obtain single-entry superblocks.
- *candidate* — [PB O0230](pb-compiler-catalog.md#pb-o0230) Jump-to-next removal: Remove a jump whose target is the immediately following block.
- *candidate* — [PB O0247](pb-compiler-catalog.md#pb-o0247) Jump-table entry compression: Store dispatch-table entries as byte offsets from a base, or as indices into a smaller address table when cases share targets.
- *candidate* — [PB O0275](pb-compiler-catalog.md#pb-o0275) Cold-code outlining: Extract rare paths of a hot function into a separate cold function so the hot body shrinks.
- *candidate* — [PB O0336](pb-compiler-catalog.md#pb-o0336) Finite-state-machine compilation: Classify a byte through a 256-entry class table followed by a compact dispatch over class ids.
- *candidate* — [PB O0390](pb-compiler-catalog.md#pb-o0390) Superblock formation by side-entry duplication: Duplicate the blocks a trace is entered through from outside, giving the trace a single entry.

### Profile-guided

- *candidate* — [PB O0269](pb-compiler-catalog.md#pb-o0269) Profile-guided inlining: Raise the inlining budget for hot call sites and withhold it from cold ones using call counts.
- *candidate* — [PB O0270](pb-compiler-catalog.md#pb-o0270) Value-profile specialization: Clone a procedure for an argument value that dominates at run time and guard the clone by a test.
- *candidate* — [PB O0271](pb-compiler-catalog.md#pb-o0271) Indirect call promotion: Split an indirect call into a guarded direct call to the hottest target plus the indirect fallback.
- *candidate* — [PB O0272](pb-compiler-catalog.md#pb-o0272) Profile-guided loop optimization: Choose unroll factors, peeling and versioning from the observed trip-count distribution.
- *candidate* — [PB O0364](pb-compiler-catalog.md#pb-o0364) Hot-path block chaining: Order blocks by chaining along the highest-frequency edge so the common path falls through.
- *candidate* — [PB O0365](pb-compiler-catalog.md#pb-o0365) Maximum weighted fall-through: Choose the block order that maximizes the execution-weighted number of fall-through edges.
- *candidate* — [PB O0391](pb-compiler-catalog.md#pb-o0391) Cold-code deduplication: Merge identical sequences aggressively where they are cold.
- *candidate* — [PB O0392](pb-compiler-catalog.md#pb-o0392) Hot-code duplication: Duplicate a small shared hot block into its predecessors to remove the jump.
- *candidate* — [PB O0399](pb-compiler-catalog.md#pb-o0399) Profile-weighted tail merging: Merge tails where they are cold and keep or duplicate them where they are hot.

### Strings

- *candidate* — [PB O0009](pb-compiler-catalog.md#pb-o0009) String-temp economy: Fold a concatenation of string literals into one pooled literal at compile time.
- *candidate* — [PB O0024](pb-compiler-catalog.md#pb-o0024) Multi-concat single-allocation builder: Build a concatenation tree of three or more operands with one allocation sized by the summed lengths.
- *candidate* — [PB O0178](pb-compiler-catalog.md#pb-o0178) Empty-string operation simplification: Fold string operations with an empty operand or zero length to the other operand or the empty string.
- *candidate* — [PB O0181](pb-compiler-catalog.md#pb-o0181) Empty-string comparison via length: Compare a string with the empty string by testing its length instead of calling a string comparison.
- *candidate* — [PB O0266](pb-compiler-catalog.md#pb-o0266) Zero-length string intrinsic folding: Fold a substring or fill operation whose length is proven zero to the empty string.
- *candidate* — [PB O0294](pb-compiler-catalog.md#pb-o0294) String-builder recognition: Recognize a loop that appends to one string accumulator and grow that accumulator in place.
- *candidate* — [PB O0295](pb-compiler-catalog.md#pb-o0295) String result-buffer forwarding: Let the caller pass the destination so a string-returning function writes its result in place.
- *candidate* — [PB O0296](pb-compiler-catalog.md#pb-o0296) String move instead of copy: Transfer the buffer instead of copying it when the source is not read after the assignment.
- *candidate* — [PB O0298](pb-compiler-catalog.md#pb-o0298) String comparison length guard: Decide string inequality from the lengths before comparing bytes.
- *candidate* — [PB O0299](pb-compiler-catalog.md#pb-o0299) Interned literal identity comparison: Compare two values known to refer to pooled literals by address and length.
- *candidate* — [PB O0300](pb-compiler-catalog.md#pb-o0300) ASCII string specialization: Fold case conversion and case-insensitive comparison of a literal whose bytes are all 7-bit ASCII.
- *candidate* — [PB O0301](pb-compiler-catalog.md#pb-o0301) Encoding-conversion elimination: Cancel a number-to-text-to-number round trip whose intermediate text is unobservable.
- *candidate* — [PB O0302](pb-compiler-catalog.md#pb-o0302) Search algorithm selection by pattern: Select the substring search by what is known about the needle: byte scan, short candidate scan, or a compile-time skip table.
