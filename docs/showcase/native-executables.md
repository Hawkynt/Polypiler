# x86 executable examples

These examples show the files produced, how their headers and code can be inspected, and which results were observed at runtime.

| Width and platform | Build path | Observed evidence |
| --- | --- | --- |
| 16-bit DOS | Pinned 8086 code and DOS loader fixtures | MS-DOS 6.22 runs COM and MZ files and applies declared relocations |
| 32-bit Linux | Polypiler C++17 output, Clang and LLD | ELF32 for Intel 80386, exit status 42 |
| 32-bit Windows | Same Polypiler C++17 output, Clang and LLD | PE32 for i386, entry RVA `0x1000` and `ExitProcess` import inspected with LLVM |
| 64-bit Linux | Same Polypiler C++17 output, GCC | ELF64 for x86-64, exit status 42 |
| 64-bit Windows | Same Polypiler C++17 output, Clang and LLD | PE32+ for x86-64, entry RVA `0x1000` and `ExitProcess` import inspected with LLVM |

## 16-bit DOS

The loader test boots a licensed MS-DOS 6.22 image in QEMU and runs the same five-byte program as a COM file and as a DOS MZ executable. Both execute `mov ax, 0x4C2A` followed by `int 0x21`. Set `POLYPILER_MSDOS622_BOOT_IMAGE` to the boot image and put the locked `qemu-x86` toolchain on `PATH`. Run from the repository root. [DOS setup](../../tests/fixtures/batch/dos622/README.md)

```bash
dotnet test Polypiler.Tests.Acceptance -c Release \
  --filter 'FullyQualifiedName~VerifyNativeDOSExecutableLoadingFeature'
```

The guest records the process status for each file. The relocation pair uses identical instructions and changes only the declaration of the relocation. [Loader fixtures](../../tests/fixtures/retro/x86-16/loader/README.md) · [Full DOS trace](../../tests/fixtures/retro/x86-16/loader/TRACE.TXT)

```text
COM=42
MZ=42
RELOC=42
NORELOC=1
```

## C# input for Linux and Windows

Save the following as `NativeSample.cs`. This is the original fixture proposed for direct executable emission. [Fixture source and SHA-256](https://github.com/Hawkynt/Polypiler/issues/1520)

```csharp
public static class NativeSample {
  public static int Add(int a, int b) => unchecked(a + b);
  public static int Run() => Add(20, 22);
}
```

```bash
polypiler translate NativeSample.cs --from csharp --to cpp \
  --profile cpp/cpp17 --opt none --output generated
```

The command produces `generated/native_sample.cpp` and `generated/native_sample.hpp`. Both architectures use those files as generated.

## 32-bit Linux ELF and Windows PE

Use the locked LLVM tools from the `clang` toolchain. The sample can compile freestanding on a host without a 32-bit C++ standard library. Save the two fixed-width aliases used by this generated source as `freestanding/cstdint`.

```cpp
#pragma once
namespace std {
using int32_t = __INT32_TYPE__;
using uint32_t = __UINT32_TYPE__;
}
```

The Linux entry calls a C-linked bridge. Save these two files as `bridge-linux.cpp` and `entry-i386.s`, respectively.

```cpp
#include "native_sample.hpp"

extern "C" int cpp_run() {
  return native_sample::run();
}
```

```asm
.text
.globl entry
.type entry,@function
entry:
  call cpp_run
  movl %eax, %ebx
  movl $1, %eax
  int $0x80
.size entry, . - entry
```

```bash
common=(--driver-mode=g++ -std=c++17 -O0 -ffreestanding -fno-stack-protector \
  -fno-exceptions -fno-rtti -nostdinc++ -isystem freestanding -I generated)
linux=(--target=i386-unknown-linux-gnu -fno-pic -fno-pie)
clang "${common[@]}" "${linux[@]}" -c generated/native_sample.cpp -o add-linux.o
clang "${common[@]}" "${linux[@]}" -c bridge-linux.cpp -o bridge-linux.o
clang --target=i386-unknown-linux-gnu -c entry-i386.s -o entry-i386.o
ld.lld -m elf_i386 -static -nostdlib -e entry -o NativeSample.elf \
  entry-i386.o bridge-linux.o add-linux.o

llvm-readelf --file-header --program-headers NativeSample.elf
llvm-objdump -d --x86-asm-syntax=intel NativeSample.elf
status=0
./NativeSample.elf || status=$?
printf 'exit=%s\n' "$status"
test "$status" -eq 42
```

On a Linux host with i386 execution support, this prints `exit=42`. LLVM reports `ELF32`, `Intel 80386` and entry address `0x4010c0` for the captured build. Polypiler read back the same address in executable `.text` on the ELF reader branch. [ELF reader PR](https://github.com/Hawkynt/Polypiler/pull/1540)

For PE32, save the Windows entry as `entry-win32.cpp` and the import definition as `kernel32.def`.

```cpp
#include "native_sample.hpp"

extern "C" __declspec(dllimport) __declspec(noreturn) void __stdcall ExitProcess(unsigned code);

extern "C" void __cdecl fixture_entry() {
  ExitProcess(static_cast<unsigned>(native_sample::run()));
}
```

```text
LIBRARY KERNEL32.dll
EXPORTS
  ExitProcess@4
```

```bash
win32=(--target=i686-pc-windows-msvc)
llvm-dlltool -m i386 -k -d kernel32.def -l kernel32.lib
clang "${common[@]}" "${win32[@]}" -c generated/native_sample.cpp -o add-win32.obj
clang "${common[@]}" "${win32[@]}" -c entry-win32.cpp -o entry-win32.obj
lld -flavor link /machine:x86 /subsystem:console /nodefaultlib /timestamp:0 \
  /entry:fixture_entry /out:NativeSample.exe entry-win32.obj add-win32.obj kernel32.lib

llvm-readobj --file-headers --coff-imports NativeSample.exe
llvm-objdump -d --x86-asm-syntax=intel NativeSample.exe
```

LLVM identifies `IMAGE_FILE_MACHINE_I386`, optional header magic `0x10B`, entry RVA `0x1000` and the import `KERNEL32.dll!ExitProcess`. This example's Windows evidence is static inspection.

## 64-bit Linux ELF

Save this launcher as `launcher.cpp` and build with the locked `gcc` toolchain. The compiler driver supplies the standard C++ headers and links the generated code.

```cpp
#include "native_sample.hpp"

int main() {
  return native_sample::run();
}
```

```bash
g++ -std=c++17 -m64 -march=x86-64 -O0 -no-pie -Igenerated \
  generated/native_sample.cpp launcher.cpp -o native-sample-elf64

llvm-readelf --file-header --symbols native-sample-elf64
llvm-objdump -d --x86-asm-syntax=intel native-sample-elf64
status=0
./native-sample-elf64 || status=$?
printf 'exit=%s\n' "$status"
test "$status" -eq 42
```

LLVM reports `ELF64` and `Advanced Micro Devices X86-64`. The captured disassembly shows `native_sample::run` passing 20 and 22 in `edi` and `esi` to `native_sample::add`. The program exits 42, with empty stdout and stderr.

## 64-bit Windows PE

The Windows x64 ABI uses undecorated C import names for this entry. Save the launcher as `entry-win64.cpp` and the definition as `kernel64.def`.

```cpp
#include "native_sample.hpp"

extern "C" __declspec(dllimport) __declspec(noreturn) void ExitProcess(unsigned code);

extern "C" void fixture_entry() {
  ExitProcess(static_cast<unsigned>(native_sample::run()));
}
```

```text
LIBRARY KERNEL32.dll
EXPORTS
  ExitProcess
```

```bash
win64=(--target=x86_64-pc-windows-msvc)
llvm-dlltool -m i386:x86-64 -d kernel64.def -l kernel64.lib
clang "${common[@]}" "${win64[@]}" -c generated/native_sample.cpp -o add-win64.obj
clang "${common[@]}" "${win64[@]}" -c entry-win64.cpp -o entry-win64.obj
lld -flavor link /machine:x64 /subsystem:console /nodefaultlib /timestamp:0 \
  /entry:fixture_entry /out:NativeSample-x64.exe entry-win64.obj add-win64.obj kernel64.lib

llvm-readobj --file-headers --coff-imports NativeSample-x64.exe
llvm-objdump -d --x86-asm-syntax=intel NativeSample-x64.exe
```

LLVM identifies `IMAGE_FILE_MACHINE_AMD64`, optional header magic `0x20B`, entry RVA `0x1000` and the import `KERNEL32.dll!ExitProcess`. The observed result comes from static inspection.

## Direct emission and read-back

Direct Polypiler executable targets and typed recovery are tracked for [DOS COM and MZ](https://github.com/Hawkynt/Polypiler/issues/1047), [Linux ELF32 and ELF64](https://github.com/Hawkynt/Polypiler/issues/1520), [Windows PE32 and PE32+](https://github.com/Hawkynt/Polypiler/issues/1521), and [native x86 lifting](https://github.com/Hawkynt/Polypiler/issues/1522). CPU profile work is tracked for [80286, 80386 and Pentium](https://github.com/Hawkynt/Polypiler/issues/1523) and [x86-32 extensions](https://github.com/Hawkynt/Polypiler/issues/1528). The existing [x86-64 ISA level reference](../reference/x64-isa-levels.md) documents the cumulative v1 through v4 feature sets.
