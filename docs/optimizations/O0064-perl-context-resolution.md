# O0064 — Resolve Perl scalar, list and void context explicitly

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#493](https://github.com/Hawkynt/Polypiler/issues/493). Once implemented it is switched off with `--disable-opt O0064`.

## What it does

In Perl the same expression computes different things depending on who asks: an array in scalar context is
its element count, `reverse` reverses a list or a string, a list assignment in scalar context yields the
number of right-hand elements, and a subroutine can ask `wantarray` and return something else entirely. No
target language has this, so every expression's context must be made explicit before anything can be typed.
PRD §4.6 (Perl: scalar/list/void context), §6.2 fourth scenario, §8 P3 ("context-aware AST").

## Example

### Before

```
// my @a = (5, 6, 7);  my $n = @a;  my ($f) = @a;  my $c = () = (5, 6, 7);
%n = Dyn.Assign.scalar (Dyn.Array %a)
%f = Dyn.Assign.list   [%f], (Dyn.Array %a)
%c = Dyn.Assign.scalar (Dyn.Assign.list [], (5, 6, 7))
```

### After

```
%n = Length %a                       // 3
%f = Load %a, 0                      // 5   (undef if @a is empty)
%c = 3                               // count of the right-hand list
```

## When it applies

(1) Context is determined syntactically for every expression by the rules of `perldata`/`perlop`: the
right side of a scalar assignment, operands of arithmetic, string and comparison operators, conditions and
`scalar(...)` are scalar context; the right side of a list assignment (left side in parentheses, an array
or a hash), arguments of list operators and of user subs without prototypes, `return` in a list-context
call, and `foreach` lists are list context; a statement whose value is unused is void context. (2) A
`return` expression and the final expression of a sub take the context of the call. A sub is
context-insensitive if no reachable `return` expression is context-dependent and it does not use
`wantarray`; such a sub is not cloned. (3) Cloning requires every call site of the sub to be direct; a sub
that is context-sensitive and is called through a code reference, or is exported for unknown callers, gets
all three versions and a dispatcher only if [#492](https://github.com/Hawkynt/Polypiler/issues/492) allows it; otherwise PLY0507. (4) Evaluation
order and effects of operands are unchanged: the comma operator in scalar context still evaluates its left
operands. (5) The per-builtin context table states the scalar- and list-context result of each supported
builtin; a builtin without an entry is not rewritten and stays dynamic.

## How it is proven

`structural`: context is a syntactic attribute; the rewrites replace a context-dependent operation by the operation the language defines for that context, validated against `perl`. No solver, no timeout path.

## When it must not fire

- `my $n = @a;` is 3 but `my ($n) = @a;` is 5 for `@a = (5, 6, 7)`.
- `my $x = (4, 5, 6);` is 6 (comma operator), not 3 and not 4.
- `sub f { return (7, 8, 9) }  my $s = f();` is 9 (last element of the comma list) but `sub g { my @t = (7, 8, 9); return @t }  my $t = g();` is 3 (array in scalar context).
- `scalar(reverse("ab", "cd"))` is `"dcba"`; in list context `reverse("ab", "cd")` is `("cd", "ab")`.
- `sub w { wantarray ? "list" : "scalar" }`: `my @l = w();` and `my $s = w();` differ.

## Switching it off

`--disable-opt O0064`; `Kind = Required`, so it runs at every level including `--opt none`. With it disabled context-dependent operations stay dynamic and a typed target reports PLY0507.

## Interactions with other passes

runs first in phase `specialize` for Perl sources (before [#253](https://github.com/Hawkynt/Polypiler/issues/253) finalizes facts, since representation depends on context); enables [#779](https://github.com/Hawkynt/Polypiler/issues/779) and all later passes.
