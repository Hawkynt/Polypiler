using System.Collections.Generic;
using System.Text.Json;

namespace Polypiler.Core.Frontends.Perl.Parsing;

internal sealed record PerlTree {
  public int Wire { get; init; }
  public int Stacked { get; init; }
  public int TargetMy { get; init; }
  public int IterReversed { get; init; }
  public Dictionary<string, PerlSubroutine> Functions { get; init; } = [];
  public Dictionary<string, PerlPackage> Packages { get; init; } = [];
  public Dictionary<string, PerlMoosePackage> Moose { get; init; } = [];
  public PerlSubroutine[] Begin { get; init; } = [];
  public PerlOp? Program { get; init; }
  public string?[] ProgramLexicals { get; init; } = [];
}

internal sealed record PerlMoosePackage {
  public string Kind { get; init; } = "class";
  public string[] Extends { get; init; } = [];
  public string[] With { get; init; } = [];
  public string[] Requires { get; init; } = [];
  public PerlMooseAttribute[] Attributes { get; init; } = [];
}

internal sealed record PerlMooseAttribute {
  public string Name { get; init; } = "";
  public string Is { get; init; } = "rw";
  public string Isa { get; init; } = "";
  public string Writer { get; init; } = "";
  public string Reader { get; init; } = "";
  public string DefaultKind { get; init; } = "none";
  public string DefaultValue { get; init; } = "";
}

internal sealed record PerlPackage {
  public Dictionary<string, PerlSubroutine> Functions { get; init; } = [];
  public string[] Export { get; init; } = [];
  public string[] ExportOk { get; init; } = [];
  public string[] Isa { get; init; } = [];
}

internal sealed record PerlSubroutine {
  public string?[] Lexicals { get; init; } = [];
  public string? Prototype { get; init; }
  public PerlOp? Body { get; init; }
}

internal sealed record PerlOp {
  public string Name { get; init; } = "";
  public int Target { get; init; }
  public int Flags { get; init; }
  public int Private { get; init; }
  public string Want { get; init; } = "";
  public int Integer { get; init; }
  public int Line { get; init; }
  public string? Label { get; init; }
  public string? Package { get; init; }
  public string? Symbol { get; init; }
  public string? Stash { get; init; }
  public string? Pv { get; init; }
  public PerlLiteral? Constant { get; init; }
  public PerlSubroutine? Code { get; init; }
  public PerlDerefStep[]? Deref { get; init; }
  public string? DerefOp { get; init; }
  public PerlConcatSpec? Concat { get; init; }
  public int[]? Aux { get; init; }
  public PerlOp[] Children { get; init; } = [];
}

internal sealed record PerlLiteral(string Kind, string Value, int Wide = 0);

internal sealed record PerlDerefStep {
  public string Kind { get; init; } = "";
  public string Source { get; init; } = "";
  public int Target { get; init; }
  public string? Symbol { get; init; }
  public string IndexKind { get; init; } = "";
  public string? IndexValue { get; init; }
  public int IndexTarget { get; init; }
  public string? IndexSymbol { get; init; }
}

internal sealed record PerlConcatSpec {
  public int Nargs { get; init; }
  public string Template { get; init; } = "";
  public int[] Lengths { get; init; } = [];
}

internal static class PerlTreeJson {
  public static JsonSerializerOptions Options { get; } = new() { PropertyNameCaseInsensitive = true, MaxDepth = 128 };
}
