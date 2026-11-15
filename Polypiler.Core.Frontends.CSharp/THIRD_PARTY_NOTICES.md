# Third-party notices — Polypiler.Core.Frontends.CSharp

This project takes the following third-party software as a NuGet dependency. It is not vendored and
not modified; the `polypiler` executable ships the assemblies as the packages provide them.

## Roslyn — the .NET Compiler Platform

- **Packages:** `Microsoft.CodeAnalysis.CSharp` 5.3.0 and, through it, `Microsoft.CodeAnalysis.Common` 5.3.0
- **Source:** <https://github.com/dotnet/roslyn>
- **Used for:** parsing and binding C# source — syntax trees, the semantic model, constant values and
  the compiler's own diagnostics
- Origin: https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.3.0
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
