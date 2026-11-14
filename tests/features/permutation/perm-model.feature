Feature: Pure bit-mapping model

  Scenario: A byte reversal is a bijection
    Given the mapping text "8:[7,6,5,4,3,2,1,0]"
    When it is parsed
    Then IsBijective is true and HasDuplication is false
    And RequiredOutputMask is 0xFF and DroppedInputMask is 0x00
    And it equals BitMapping.Reverse(8)

  Scenario: Duplication, inversion, constants and don't-cares are facts of the mapping
    Given the mapping text "8:[0,0,~1,=1,?,7,6,5]"
    When it is parsed
    Then IsBijective is false and IsInjective is false
    And UseCount of input bit 0 is 2
    And InvertMask is 0x04, OnesMask is 0x08 and RequiredOutputMask is 0xEF
    And UsedInputMask is 0xE3 and DroppedInputMask is 0x1C

  @EndToEnd
  Scenario: Every exported BitPerm mapping loads and matches BitPerm's route map
    Given the 44 files in "tests/fixtures/bitperm/mappings"
    When each is read with BitPermState.Read
    Then each loads without a diagnostic
    And ToRouteMap of each equals the route map BitPerm derives from the same file under node
    And "tests/fixtures/permutation/bitperm-routemaps.json" holds the same route maps

  Scenario: A source index outside the input is refused
    Given the mapping text "8:[7,6,5,4,3,2,1,8]"
    When it is parsed
    Then the diagnostic is "PLY0801: output bit 7 reads input bit 8, input width is 8"
    And no mapping is produced

  Scenario: A non-square mapping is valid but not a bijection
    Given the mapping text "8:[0,2,4,6]"
    When it is parsed
    Then OutputWidth is 4, IsInjective is true and IsBijective is false
    And ToRouteMap throws InvalidOperationException
