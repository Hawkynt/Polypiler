# MS-DOS 6.22 COMMAND.COM oracle

The guest tests boot QEMU 11.1.3 and use GNU mtools 4.0.50 to stage two floppy images.
Set `POLYPILER_MSDOS622_BOOT_IMAGE` to the licensed English boot floppy image with
SHA-256 `b88030401122d234ea6aafba3cfed7de2b7b1782700a67be5498edca6f9fec5d`.
Set `POLYPILER_MSDOS622_QEMU_DATA` if your QEMU installation needs an explicit firmware search path.

```sh
dotnet test Polypiler.Tests.Acceptance -c Release --filter FullyQualifiedName~DosCommandOracleTests
```

The runner writes a bootstrap into a temporary boot image copy. The guest reads that
copy through drive A and writes the probe trace to a fresh drive B. The tests compare
the original COMMAND.COM hash, the complete trace, and the timeout trace.

The `tree` case stages its two data files under `DATA` in the order `TWO.TXT`, `ONE.TXT`.
The runner accepts explicit relative 8.3 destinations and creates parent directories on drive B.
The guest's wildcard trace tests both the paths and the order.

The `lift.bat` fixture tests the label prefix and a 127-byte SET line. `LIFTDRV.BAT`
boots a child interpreter and captures its hexadecimal result for comparison with generated C#.

`DosWrap.cs` is the pinned C# emitter fixture. `EMITDRV.BAT` runs the resulting
`KERNEL.BAT` in a fresh `COMMAND.COM /E:32768 /C` child and checks that the caller's
`PLYD` remains intact. The generated result is a single uppercase hexadecimal
`PLYRESULT` line. The emitter budgets 1,024 bytes of inherited entries within the
32,768-byte environment, including separately the generated entries and the
environment's final terminator.

The fixtures in this folder are LGPL-3.0-or-later. `PLYRC.COM` is five bytes
(`B8 2A 4C CD 21`) and exits through DOS interrupt 21h with status 42.
