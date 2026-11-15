@EndToEnd @Layer.core
Feature: Native calls through P/Invoke

  C# calls native code through [DllImport] and [LibraryImport]. The IR keeps such a declaration as a foreign
  function with its library, symbol and the marshalling of every parameter (#1332): every C# profile writes it as
  P/Invoke again, and JavaScript calls it through the binding module --bind names, or refuses it. The C library
  fixture is LibC.Linux.cs with glibc ("libc.so.6") or LibC.Windows.cs with msvcrt, for the operating system the
  tests run on; its own build, run against that C library, is the oracle.

  Scenario Outline: The declarations survive re-targeting and still call the library
    Given the C library fixture of the operating system
    When it is translated with "--from csharp --to csharp --profile <profile>"
    Then the output declares StrLen, Abs, Div, MemSet and StrToL with "DllImport" and StrLenUtf8 with "<utf8>"
    And it compiles against the reference assemblies of the profile without diagnostics
    And compiled for the running runtime, StrLen("hello") returns 5, StrLenUtf8("Grüße") returns 7, Abs(-7) returns 7, Div(7, 2) returns 3 and 1, Div(-7, 2) returns -3 and -1, StrToL("ff", out _, 16) returns 255

    Examples:
      | profile        | utf8                    |
      | net2.0         | DllImport               |
      | net4.8         | UnmanagedType.LPUTF8Str |
      | netstandard2.0 | DllImport               |
      | netcoreapp3.1  | UnmanagedType.LPUTF8Str |
      | net10.0        | LibraryImport           |

  Scenario: An array is marshalled in and out
    Given the C library fixture of the operating system
    When it is translated with "--from csharp --to csharp --profile net10.0"
    Then after MemSet(b, 0xAB, 3) on a 4-byte zero array b is 171, 171, 171, 0

  Scenario: Native undefined corners are not repaired
    Given the C library fixture of the operating system
    Then Abs(-2147483648) returns -2147483648 in the fixture and in the translated C# of every profile

  Scenario: Missing native code fails as .NET fails
    Given the fixture "interop/Missing.cs"
    When it is translated with "--from csharp --to csharp --profile net10.0"
    Then calling the function of the missing library throws DllNotFoundException and the one with the missing entry point throws EntryPointNotFoundException

  Scenario: Without a binding JavaScript refuses the native call
    Given the C library fixture of the operating system
    When it is translated without a binding with "--from csharp --to javascript --profile es2022"
    Then the exit code is 3, the output contains "PLY1152", "strlen" and the library of the fixture, and nothing is written

  Scenario: A parameter outside the marshalling table is refused
    Given the C# declaration "[System.Runtime.InteropServices.DllImport(\"libc.so.6\")] static extern int f(System.Text.StringBuilder s);"
    When it is translated without a binding with "--from csharp --to javascript"
    Then the exit code is 3, the output contains "PLY1002", "StringBuilder" and "native function", and nothing is written

  Scenario Outline: With a binding JavaScript calls it and computes what .NET computes
    Given the C library fixture of the operating system
    When it is translated with "--from csharp --to javascript --profile <profile> --module <module>" and the binding module "libc-bindings.mjs"
    Then in node StrLen("hello") returns 5, Abs(-7) returns 7 and Div(7, 2) returns { Quot: 3, Rem: 1 }, as in the .NET run
    And the manifest records every native function as an unverified external

    Examples:
      | profile | module |
      | es5     | cjs    |
      | es2022  | esm    |

  Scenario Outline: An async method calls a native function between its awaits
    Given an async class that calls abs of the C library before and after an await
    When it is translated with "--from csharp --to javascript --profile <profile> --module esm" and the binding module "libc-bindings.mjs"
    Then in node Sum(-8, -3) resolves to 7

    Examples:
      | profile |
      | es2015  |
      | es2022  |

  Scenario Outline: A binding the command line cannot use is refused
    Given the C library fixture of the operating system
    When it is translated without a binding with "--from csharp --to javascript --profile es2022 --bind <binding>"
    Then the exit code is 2, the output contains "PLY0003", "<named>" and "bind", and nothing is written

    Examples:
      | binding               | named                         |
      | libc.so.6             | is no binding                 |
      | other.so=./other.mjs  | which the module calls nothing of |
