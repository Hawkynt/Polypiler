@EndToEnd @Layer.core
Feature: C# toolchain runner

  The C# runner builds C# with the C# compiler of dotnet-sdk and runs it on its runtime, through a
  driver generated for one export; the images it compares are written by hand.

  Scenario: A fixture reproduces its vectors
    Given the C# file "static class T { public static int F(int a, int b) => a + b; }"
    And a vector file for the export "T.F" with the signature "(a: i32 in, b: i32 in) -> i32" and the vectors "(2, 3) -> 5" and "(2147483647, 1) -> -2147483648"
    When RunVectors is executed with the profile csharp/net10
    Then the report lists 2 vectors passed and 0 failed
    And the first vector has the input image "0200000003000000" and the output image "05000000"

  Scenario: One exported function is executed on a list of input images
    Given the C# file "static class T { public static int F(int a, int b) => checked(a + b); }"
    When ExecuteFunction is called for the export "T.F" with the signature "(a: i32 in, b: i32 in) -> i32" on the input images "0200000003000000", "ffffffff01000000" and "ffffff7f01000000"
    Then the driver answers exactly "05000000", "00000000" and "!overflow OverflowException"
    And the third outcome has the trap class overflow

  Scenario: The signature is derived from the declaration
    Given the C# file "using System; static class T { public static int F(ReadOnlySpan<byte> data, Span<int> scratch, ref uint state, int[]? table) => 0; }"
    When CSharpSignatures.Derive is applied to "T.F"
    Then the signature is "(data: bytes in, scratch: seq<i32> inout, state: u32 inout, table: opt<seq<i32>> in) -> i32"

  Scenario: Wide values and absent arrays travel as the shared images
    Given the C# file "static class T { public static double G(double a) => a; public static int N(int[]? a) => a == null ? -1 : a.Length; }"
    When Record is executed for the export "T.G" with the signature "(a: f64 in) -> f64" on the input image "010000000000f87f"
    Then the recorded output image is "010000000000f87f"
    When Record is executed for the export "T.N" with the signature "(a: opt<seq<i32>> in) -> i32" on the input images "00" and "010100000007000000"
    Then the recorded output images are "ffffffff" and "01000000"

  Scenario: A wrong expectation is reported, not hidden
    Given the fixture "Add.cs" and the vector file "Mismatch.vectors.json"
    When RunVectors is executed with the profile csharp/net10
    Then the report lists 0 vectors passed and 1 failed
    And the report is explained by the diagnostics PLY0051 and PLY1094 "call 0: expected 6, actual 5"

  Scenario: Code that does not compile is a compile failure
    Given the C# file "static class T { public static int F(int a) => \"x\"; }"
    When Compile is executed for the export "T.F" with the signature "(a: i32 in) -> i32"
    Then the outcome is BuildFailed with the diagnostics PLY0030 and PLY1091 carrying "CS0029"
    And no assembly exists in the build directory

  Scenario: A program that never returns hits the time limit
    Given the C# file "static class T { public static int F(int a) { while (true) { } } }"
    And a wall-time limit of 5 seconds
    When ExecuteFunction is called for the export "T.F" with the signature "(a: i32 in) -> i32" on the input image "01000000"
    Then the operation fails with the diagnostic PLY0031
    And no call outcome is returned
