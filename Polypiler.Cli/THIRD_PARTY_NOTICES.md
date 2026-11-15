# Third-party notices — Polypiler.Cli

This project takes the following third-party software as a NuGet dependency. It is not vendored and
not modified; the `polypiler` executable ships the assembly as the package provides it.

## System.CommandLine

- **Package:** `System.CommandLine` 2.0.15
- **Source:** <https://github.com/dotnet/dotnet> (commit `8e0f8ed2312954727f8919d7d1c2f78b483b4ee7`), originally
  <https://github.com/dotnet/command-line-api>
- **Used for:** the command tree of `polypiler`, the tokenisation of `--name value`, `--name=value` and the
  `--` terminator, and the typed values a command reads; the grammar, its refusals and the help texts are
  Polypiler's own (ADR 0004)
- Origin: https://github.com/dotnet/dotnet/tree/8e0f8ed2312954727f8919d7d1c2f78b483b4ee7/src/command-line-api
- Licence: MIT

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
