# Licence of the code Polypiler emits

Polypiler itself, the compiler, is licensed under LGPL-3.0-or-later (see [LICENSE](LICENSE)).

A translation copies code of Polypiler's own into the files it generates: the helper functions of
the backends (`Polypiler.Core.Backends.CSharp/Helpers`, `Polypiler.Core.Backends.JavaScript/Helpers`)
and the portable floating-point implementations (`Polypiler.Core.IR/Float/Operations`). That code,
wherever it appears in a generated output, is released under the MIT licence below, so a translated
program keeps the licence of its source. The compiler is not relicensed by this grant.

Code converted from third-party sources keeps its own licence, which its file states in its
`SPDX-License-Identifier` header and its `THIRD-PARTY-NOTICE.*.txt` records: CORE-MATH and musl
under MIT, FreeBSD msun under BSD-2-Clause. Every generated file that carries helpers names their
licence in its header, and `polypiler.manifest.json` lists each helper with its origin and licence
under `helpers`.

---

MIT License

Copyright (c) 2026 Hawkynt

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
