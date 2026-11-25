@Layer.basic @ExternalInterop
Feature: Lift MS-DOS 6.22 scalar kernels

  Scenario: A label matches the native short prefix
    Given the pinned DOS fixture whose GOTO operand shares an eight-character prefix with two labels
    When it runs on MS-DOS 6.22 and is separately lifted under dos622
    Then the two results match the selected label and return 42

  Scenario: Windows arithmetic is refused in the DOS profile
    Given a DOS kernel containing SET /A
    When it is lifted under dos622
    Then a source diagnostic identifies the unsupported command and no output is written
