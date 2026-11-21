@Layer.core
Feature: Pointer provenance

  For every bounded pointer (#1331) the provenance analysis (#93) knows the allocation sites it derives
  from and its byte offset, and the facade answers whether an access stays within the region. A
  stackalloc, a pin and the address of a local are sites; a pointer parameter is Unknown, as a pointer
  read from memory would be. The sources are C# methods; facts are asked before the return.

  Scenario: Pointer arithmetic keeps the allocation and tracks the offset
    Given the analysed C# method "public static unsafe uint F() { byte* a = stackalloc byte[64]; byte* p = a + 16; byte* q = p + 4; return *q; }"
    When the provenance analysis runs
    Then the provenance of "q" is "{a} offset [20, 20]"

  Scenario: A ranged index gives an offset interval
    Given the analysed C# method "public static unsafe uint F(uint x) { uint* a = stackalloc uint[16]; uint i = x & 7; uint* p = a + i; return *p; }"
    When the provenance analysis runs
    Then the provenance of "p" is "{a} offset [0, 28]"
    And IsInBounds of "p" for 4 bytes is True

  Scenario: An offset that may leave the allocation is not proven in bounds
    Given the analysed C# method "public static unsafe uint F(uint x) { uint* a = stackalloc uint[16]; uint i = x & 31; uint* p = a + i; return *p; }"
    When the provenance analysis runs
    Then the provenance of "p" is "{a} offset [0, 124]"
    And IsInBounds of "p" for 4 bytes is Unknown

  Scenario: A branch unites the sites
    Given the analysed C# method "public static unsafe uint F(bool c) { byte* a = stackalloc byte[8]; byte* b = stackalloc byte[8]; byte* p = a; if (c) { p = b; } return *p; }"
    When the provenance analysis runs
    Then the provenance of "p" is "{a, b} offset [0, 0]"

  Scenario: A pointer from outside has unknown provenance
    Given the analysed C# method "static unsafe uint F(uint* p) { return *p; } public static unsafe uint G() { uint v = 1; return F(&v); }"
    When the provenance analysis runs
    Then the provenance of "p" is "unknown"
    And IsInBounds of "p" for 1 bytes is Unknown

  Scenario: An access entirely outside the allocation is decided false
    Given the analysed C# method "public static unsafe uint F() { byte* a = stackalloc byte[8]; byte* p = a + 8; return *p; }"
    When the provenance analysis runs
    Then IsInBounds of "p" for 1 bytes is False
