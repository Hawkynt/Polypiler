@Layer.basic @ExternalInterop
Feature: Verify native DOS executable loading

  Scenario: DOS starts both executable containers
    Given the pinned COM and MZ loader fixtures
    When a fresh MS-DOS 6.22 guest runs the loader probe
    Then the COM program exits with status 42
    And the MZ program exits with status 42
    And the DOS loader trace matches the native reference

  Scenario: The loader applies only declared relocations
    Given the pinned COM and MZ loader fixtures
    When a fresh MS-DOS 6.22 guest runs the loader probe
    Then the RELOC program exits with status 42
    And the NORELOC program exits with status 1
    And the DOS loader trace matches the native reference

  Scenario: An absent executable cannot inherit earlier success
    Given the pinned COM and MZ loader fixtures
    When a fresh MS-DOS 6.22 guest runs the loader probe without EXIT42.EXE
    Then the probe reports the missing MZ program
