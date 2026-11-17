@Layer.core
Feature: Binary input manifest

  Scenario: A minimal manifest for a raw blob loads
    Given "blob.bin" containing the bytes 8B 44 24 04 0F C8 C3 90
    And "min.json" with isa "x86", mode "protected32", loadAddress "0x00401000", one entry "bswap32" at "0x00401000" with abi "cdecl (u32) -> u32" and the SHA-256 of blob.bin
    When ImageBuilder.Load runs
    Then the image has one executable section at 0x00401000 of 8 bytes
    And the entry contract of "bswap32" is convention "cdecl" with one u32 parameter and a u32 result

  Scenario: A changed image is refused
    Given "min.json" and a copy of blob.bin whose last byte is CC instead of 90
    When ImageBuilder.Load runs
    Then it fails with PLY4056 showing both hashes

  Scenario: An entry outside the image is refused
    Given "min.json" with the entry address changed to "0x00401008"
    When ImageBuilder.Load runs
    Then it fails with PLY4058 naming "bswap32" and 0x00401008

  Scenario: An unsupported container is named, not guessed
    Given a file starting with the bytes CF FA ED FE and a manifest without the field "container"
    When ImageBuilder.Load runs
    Then it fails with PLY4059 naming "Mach-O"
