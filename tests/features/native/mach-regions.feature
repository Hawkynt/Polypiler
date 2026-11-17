@Layer.core
Feature: Memory-region classes for machine accesses

  Scenario Outline: A constant address is classified by the memory map
    Given the address map "pc-vga.map.json"
    And the instruction "Store.u8 <address>, 77"
    When RegionClassifier.Classify runs
    Then the class is <class> and the channel is "<channel>"

    Examples:
      | address | class          | channel         |
      | 0x9FFFF | ProvenShared   | Memory:ram      |
      | 0xA0000 | MemoryMappedIO | Device:mmio.vga |
      | 0xBFFFF | MemoryMappedIO | Device:mmio.vga |
      | 0xC0000 | UnknownMemory  | Memory.Unknown  |

  Scenario: A store through a pointer without provenance is unknown memory
    Given no address map
    And "Store.u32 %p, %v" where %p has no provenance fact
    When RegionClassifier.Classify runs
    Then the class is UnknownMemory with reason "no provenance"

  Scenario: A four-byte store crossing into the video buffer is a device access
    Given the address map "pc-vga.map.json"
    And the instruction "Store.u32 0x9FFFE, %v"
    When RegionClassifier.Classify runs
    Then the class is MemoryMappedIO
    And the channels are "Memory:ram" and "Device:mmio.vga"

  Scenario: Overlapping ranges are refused
    Given ranges "a" 0x1000-0x1FFF and "b" 0x1FFF-0x2FFF
    When the AddressMap is built
    Then it fails with PLY4009 naming "a", "b" and 0x1FFF
