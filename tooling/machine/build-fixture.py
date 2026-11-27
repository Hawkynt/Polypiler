#!/usr/bin/env python3
"""Build a freestanding ELF fixture without depending on the host C library."""

import argparse
from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--triple", required=True)
    parser.add_argument("--entry", default="entry")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--clang", default="clang")
    parser.add_argument("--linker", default="ld.lld")
    outputs = parser.add_mutually_exclusive_group()
    outputs.add_argument("--object", action="store_true")
    outputs.add_argument("--shared", action="store_true")
    options = parser.parse_args()
    language = ["-x", "assembler-with-cpp"] if options.source.suffix.lower() == ".s" else []
    position = ["-fPIC"] if options.shared else ["-fno-pic", "-fno-pie"]
    compile_command = [options.clang, f"--target={options.triple}", "-ffreestanding",
                       "-fno-stack-protector", *position, *language, "-c", str(options.source), "-o"]

    if options.object:
        subprocess.run([*compile_command, str(options.output)], check=True)
        return

    with TemporaryDirectory(prefix="polypiler-machine-") as scratch:
        object_path = Path(scratch) / "input.o"
        subprocess.run([*compile_command, str(object_path)], check=True)
        link = ["-shared"] if options.shared else ["-static"]
        # Windows only completes a bare name with .exe, so ld.lld would never find ld.lld.exe.
        linker = shutil.which(options.linker) or options.linker
        subprocess.run([linker, *link, "-nostdlib", "-e", options.entry,
                        str(object_path), "-o", str(options.output)], check=True)


if __name__ == "__main__":
    main()
