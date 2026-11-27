# MS-DOS executable loader fixtures

The fixtures are original and licensed LGPL-3.0-or-later. The probe stages
[PLYRC.COM](../../../batch/dos622/PLYRC.COM) as `EXIT42.COM`. It uses the
licensed MS-DOS 6.22 guest configuration documented in the
[COMMAND.COM oracle](../../../batch/dos622/README.md).

`EXIT42.EXE` uses the same five-byte `mov ax, 0x4C2A; int 0x21` program.
The DOS-only header starts with the following 16 little-endian words.

```text
5A4D 0025 0001 0000 0002 0011 FFFF 0001
0100 0000 0000 0000 001C 0000 0000 0000
```

`RELOC.EXE` and `NORELOC.EXE` use the same payload and have the same
entry point. Both headers describe 51 bytes and set the initial relative
stack segment to 2. The first has a single relocation at segment 0,
offset 3. The second has no relocations.

```asm
push cs
pop dx
mov ax, 0
cmp ax, dx
jne failed
mov ax, 0x4C2A
int 0x21
failed:
mov ax, 0x4C01
int 0x21
```

The loader adds the actual load segment to the zero word in `mov ax, 0`
when the table declares its relocation. The program exits with status 42 if
the loaded word equals CS and with status 1 otherwise. `PROBE.BAT` checks
each exact status before writing the pinned `TRACE.TXT`. It establishes
status 1 before each MZ program that should return 42. COMMAND.COM preserves
the previous status when the executable is missing. The negative probe omits
`EXIT42.EXE` and requires a failure record. Two fresh complete boots
using QEMU 11.1.3 and GNU mtools 4.0.50 produced identical traces.
