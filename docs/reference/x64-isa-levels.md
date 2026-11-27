# x86-64 CPU feature levels

`X64FeatureProfile.Find` returns the cumulative requirements for an x86-64
microarchitecture level. Use the same level names for Linux ELF and Windows PE.
The executable format and function calling convention have their own contracts.
The names are case-sensitive and `x86-64` selects the baseline `x86-64-v1`.

| Level | Features added at this level | Required XCR0 bits |
| --- | --- | --- |
| `x86-64-v1` | CMOV, CX8, FPU, FXSR, MMX, OSFXSR, SCE, SSE, SSE2 | `0x00` |
| `x86-64-v2` | CX16, LAHF-SAHF, POPCNT, SSE3, SSSE3, SSE4.1, SSE4.2 | `0x00` |
| `x86-64-v3` | AVX, AVX2, BMI1, BMI2, F16C, FMA, LZCNT, MOVBE, OSXSAVE | `0x06` |
| `x86-64-v4` | AVX512F, AVX512BW, AVX512CD, AVX512DQ, AVX512VL | `0xE6` |

The baseline includes the OSFXSR and SCE enables. Level v3 requires XMM and YMM
state enabled by the operating system, and v4 also requires opmask and ZMM
state. Require separate evidence for optional features such as AES-NI,
PCLMULQDQ, SHA, ADX, AVX-512 VBMI and AVX-512 VNNI. A frontend or backend must
also establish which instructions it implements. [x86-64 psABI machine
interface](https://gitlab.com/x86-psABIs/x86-64-ABI/-/raw/e1ce098331da5dbd66e1ffc74162380bcc213236/x86-64-ABI/low-level-sys-info.tex),
[Intel Software Developer's Manual](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html)

The [GCC reference fixture](../../tests/fixtures/native/x64/features/gcc-levels.txt)
records the selected features exposed by compiler macros. It comes from
`gcc -march=<level> -dM -E -x c` on empty input, using the locked `gcc` toolchain.
GCC spells the baseline `x86-64`. [GCC x86
options](https://gcc.gnu.org/onlinedocs/gcc/x86-Options.html)
