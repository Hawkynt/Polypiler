# Third-party notices

## PB-Compiler — LGPL-3.0-or-later

Origin: https://github.com/Hawkynt/PB-Compiler/tree/0d73acbea185a2597357d53f7ce9acb44758ba76
Licence: LGPL-3.0-or-later

Dead function elimination (O0381) and its reachability analysis adapt the module sweep and
test cases of Hawkynt's PB-Compiler, commit
`0d73acbea185a2597357d53f7ce9acb44758ba76`:

- `PowerBasic.Compiler/Ir/Passes/GlobalDce.cs`
- `PowerBasic.Compiler.Tests/Ir/GlobalDceTests.cs`
- `docs/optimizations/O0022-dead-procedure-elimination.md`
- `docs/optimizations/O0201-inlined-procedure-purge.md`

Source: https://github.com/Hawkynt/PB-Compiler/tree/0d73acbea185a2597357d53f7ce9acb44758ba76

Copyright Hawkynt. Licensed under GNU Lesser General Public License version 3 or later;
see the repository's `LICENSE`. The conversion uses the structured tree IR and marks from
roots rather than deleting functions without users, which also removes unreachable cycles.
Globals and type declarations remain intact.
