@Layer.native @EndToEnd
Feature: ELF reader

  Scenario: The tables of an i386 executable agree with LLVM
    Given the ELF calls fixture built for i386
    When the ELF reader opens the fixture
    Then the i386 ELF code and tables agree with llvm-readelf

  Scenario: An incomplete ELF64 header is refused
    Given the corrupt ELF fixture "truncated64.elf"
    When the ELF reader opens the fixture
    Then ELF reading fails with PLY4060 mentioning "63 of 64 bytes"

  Scenario: A machine outside the admitted set is refused
    Given the corrupt ELF fixture "unsupported-machine.elf"
    When the ELF reader opens the fixture
    Then ELF reading fails with PLY4061 mentioning "8"

  Scenario: A section past the end of the input is corrupt
    Given the corrupt ELF fixture "out-of-file-section.elf"
    When the ELF reader opens the fixture
    Then ELF reading fails with PLY4062 mentioning "offset 4000 and size 200 outside file size 4096"
