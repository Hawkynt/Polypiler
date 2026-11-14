Feature: Floating-point numbers in byte buffers

  Scenario Outline: A JavaScript DataView float read translates to C# for every profile
    Given the fixture "js/FloatView.js"
    When it is translated with "--to csharp --profile <profile>"
    Then the generated file compiles against the reference assemblies of <profile>
    And getF32LE of the bytes 00 00 80 3F at index 0 returns 1, as node returns
    And getF64BE of the bytes 40 09 21 FB 54 44 2D 18 at index 0 returns 3.141592653589793, as node returns

    Examples:
      | profile        |
      | net2.0         |
      | netstandard2.1 |
      | net10.0        |

  Scenario Outline: A C# float store translates to JavaScript for every level and writes the same bytes
    Given the fixture "float/FloatView.cs"
    When it is translated with "--to javascript --profile <level> <flags>"
    Then PutF32LE(bytes, 0, <x>) leaves the bytes <bytes> in node, as .NET leaves them

    Examples:
      | level  | flags          | x                      | bytes       |
      | es5    |                | 0.1                    | CD CC CC 3D |
      | es5    | --typed-arrays | 1e40                   | 00 00 80 7F |
      | es2015 |                | 3.4028235677973366e38  | 00 00 80 7F |
      | es2022 |                | 1.0509738482436128e-45 | 01 00 00 00 |

  Scenario: Negative zero keeps its sign bit in both directions
    Then PutF64BE(bytes, 0, -0.0) leaves 80 00 00 00 00 00 00 00 in .NET, in node and in every profile of both backends

  Scenario: A read outside the buffer traps
    Given the 12-byte buffer of the first scenario
    Then getF32LE at index 9 throws a RangeError in node, and the C# translation throws for every profile

  Scenario: A computed byte order is refused
    Given the JavaScript function "/** @param {Uint8Array} b @param {boolean} le */ export function f(b, le) { const v = new DataView(b.buffer); return v.getFloat32(0, le); }"
    When I run the translate command
    Then the exit code is 3 and the output contains "PLY1102" and "DataView.getFloat32 with a byte order that is not a literal"

  Scenario: Half precision stays refused
    Given the JavaScript function "/** @param {Uint8Array} b */ export function f(b) { const v = new DataView(b.buffer); return v.getFloat16(0, true); }"
    When I run the translate command
    Then the exit code is 3 and the output contains "PLY1102" and "the DataView method getFloat16"
