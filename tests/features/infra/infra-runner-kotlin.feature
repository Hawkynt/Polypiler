@Layer.jvm
Feature: Kotlin toolchain runner

  Scenario: A fixture reproduces its vectors
    Given the Kotlin file "fun add(a: Int, b: Int): Int = a + b"
    And a vector file with the vectors add(2, 3) = 5 and add(2147483647, 1) = -2147483648
    When RunVectors is executed with the profile kotlin/jvm
    Then the report lists 2 vectors passed and 0 failed
    And the vector "add(2, 3)" has the input image "0200000003000000" and the output image "05000000"

  Scenario: One exported function is executed on a list of argument tuples
    Given the compiled fixture "fun div(a: Int, b: Int): Int = a / b"
    When ExecuteFunction is called for the export "div" with the tuples (7, 2), (1, 0) and (-7, 2)
    Then the driver prints exactly the lines "03000000", "!divide-by-zero ArithmeticException" and "fdffffff"
    And ExecuteFunction returns three results in that order

  Scenario: Wide values and arrays travel bit-exactly
    Given the Kotlin file "fun id(a: ULong): ULong = a" with "fun nan(a: Double): Double = a" and "fun sum(a: IntArray): Int = a.sum()"
    When Record is executed for id(18446744073709551615), nan(NaN with the bit pattern 0x7FF8000000000001) and sum([1, 2, 3])
    Then the recorded output image of id is "ffffffffffffffff"
    And the recorded output image of nan is "010000000000f87f"
    And the input image of sum is "03000000010000000200000003000000" and its output image is "06000000"
