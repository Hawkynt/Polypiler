using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Dynamic;

/// <summary>What the manifest records of one caller contract (its section <c>assumptions</c>).</summary>
/// <param name="Target">The target, <c>function.parameter</c>, <c>function.return</c> or <c>global.name</c>.</param>
/// <param name="Contract">The contract, canonically spelled.</param>
/// <param name="Checked">Whether the contract holds by a run-time check or by a proof; <see langword="false"/> for a trusted one.</param>
/// <param name="Origin">Where it was stated.</param>
/// <param name="UsedBy">How many facts were derived from it.</param>
public sealed record ContractRecord(string Target, string Contract, bool Checked, string Origin, int UsedBy);

/// <summary>The module with the checks of its contracts, and the record of every contract.</summary>
/// <param name="Module">The module with a check at the boundary for every checked contract; <see langword="null"/> after an error.</param>
/// <param name="Assumptions">The record of every contract that was accepted, in the order given.</param>
public sealed record ContractEnforcement(IrModule? Module, IReadOnlyList<ContractRecord> Assumptions);

/// <summary>
/// Applies the caller contracts to a module of the structured tree IR (PRD §4.6, <c>spec/ir/assumptions.md</c>).
/// Each contract is resolved against the exported functions and globals (<c>PLY0514</c> for one that
/// names nothing) and compared with the type the module proves for its target:
/// <list type="bullet">
/// <item>a contract the type already guarantees has no effect (<c>PLY0515</c>);</item>
/// <item>a contract no value of the type can satisfy contradicts it (<c>PLY0513</c>, an error in either mode);</item>
/// <item>a checked contract the type does not guarantee becomes a test of constant cost at the
/// boundary: at the entry of the function, in parameter order and before any other statement, or
/// before every <c>return</c> for a result; a violating value throws an <c>ArgumentException</c> whose
/// message is <see cref="ViolationPrefix"/> and the contract, before any effect of the callee;</item>
/// <item>a checked contract no such test can check is <c>PLY0516</c>;</item>
/// <item>an unchecked contract the type does not guarantee is trusted: <c>PLY0512</c>, no check, and the
/// manifest lists it with <c>checked: false</c>.</item>
/// </list>
/// </summary>
public static class ContractEnforcer {

  /// <summary>The beginning of the message of the exception a violated contract throws, which the contract follows.</summary>
  public const string ViolationPrefix = "ContractViolation: ";

  private enum Verdict { NoEffect, Check, Uncheckable, Contradiction }

  /// <summary>How a contract relates to a type: the verdict, the test that is true for a violating value, or the fact it contradicts.</summary>
  private sealed record Outcome(Verdict Verdict, Func<IrExpr, IrExpr>? Violated = null, string? Fact = null);

  private static readonly Outcome _noEffect = new(Verdict.NoEffect);
  private static readonly Outcome _uncheckable = new(Verdict.Uncheckable);

  /// <summary>Applies contracts to a module.</summary>
  /// <param name="module">The module as the frontend imported it.</param>
  /// <param name="assumptions">The contracts, in the order given.</param>
  /// <param name="language">The source language, which the language-native spellings must belong to.</param>
  /// <param name="sink">Receives the diagnostics.</param>
  /// <returns>The module with its checks and the records, or no module after an error.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static ContractEnforcement Enforce(IrModule module, IReadOnlyList<ContractAssumption> assumptions, string language, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(assumptions);
    ArgumentNullException.ThrowIfNull(language);
    ArgumentNullException.ThrowIfNull(sink);
    var failed = false;
    var records = new List<ContractRecord>();
    var entryChecks = new List<(IrFunction Function, int Parameter, IrStmt Check)>();
    var resultChecks = new List<(IrFunction Function, ContractAssumption Assumption, Func<IrExpr, IrExpr> Violated)>();
    foreach (var assumption in assumptions) {
      if (Resolve(module, assumption, language, out var function, out var type, out var subject) is { } problem) {
        sink.Report(Diagnostic.Create(ContractDiagnostics.Invalid, null, assumption.Text, problem));
        failed = true;
        continue;
      }

      var outcome = Classify(assumption.Contract, type!);
      // A global has no boundary that a call passes: nothing checks it at run time.
      if (outcome.Verdict == Verdict.Check && assumption.IsGlobal)
        outcome = _uncheckable;

      switch (outcome.Verdict) {
        case Verdict.Contradiction:
          sink.Report(Diagnostic.Create(ContractDiagnostics.Contradiction, null, assumption.Text, $"{subject} has the type {type}{outcome.Fact}",
            function is null ? $"global '{assumption.Name}'" : $"function '{function.Name}'"));
          failed = true;
          break;
        case Verdict.NoEffect:
          sink.Report(Diagnostic.Create(ContractDiagnostics.NoEffect, null, assumption.Text));
          records.Add(Record(assumption, true));
          break;
        case Verdict.Uncheckable when assumption.Checked:
          sink.Report(Diagnostic.Create(ContractDiagnostics.NotCheckable, null, assumption.Text));
          failed = true;
          break;
        case Verdict.Check when assumption.Checked:
          if (assumption.IsReturn)
            resultChecks.Add((function!, assumption, outcome.Violated!));
          else
            entryChecks.Add((function!, function!.Parameters.ToList().FindIndex(parameter => parameter.Name == assumption.Name),
              Guard(outcome.Violated!(new IrLocalRef(function.Parameters.First(parameter => parameter.Name == assumption.Name))), assumption)));
          records.Add(Record(assumption, true));
          break;
        default:
          sink.Report(Diagnostic.Create(ContractDiagnostics.Unchecked, null, assumption.Text));
          records.Add(Record(assumption, false));
          break;
      }
    }

    if (failed)
      return new ContractEnforcement(null, records);

    var functions = module.Functions.Select(function => {
      var entry = entryChecks.Where(check => ReferenceEquals(check.Function, function)).OrderBy(check => check.Parameter).Select(check => check.Check).ToList();
      var results = resultChecks.Where(check => ReferenceEquals(check.Function, function)).ToList();
      if (entry.Count == 0 && results.Count == 0)
        return function;

      var locals = function.Locals.ToList();
      var body = results.Count == 0 ? function.Body : (IrBlock)Results(function.Body, function, results, locals);
      return function with { Locals = locals, Body = new IrBlock([.. entry, .. body.Statements]) };
    }).ToList();
    return new ContractEnforcement(module with { Functions = functions }, records);
  }

  private static ContractRecord Record(ContractAssumption assumption, bool isChecked)
    => new(assumption.Target, assumption.Contract.Text, isChecked, assumption.Origin, 0);

  /// <summary>What the target names, or why it names nothing of the module.</summary>
  private static string? Resolve(IrModule module, ContractAssumption assumption, string language, out IrFunction? function, out IrType? type, out string subject) {
    function = null;
    type = null;
    subject = "";
    if (assumption.Aliases.FirstOrDefault(alias => alias.Language != language) is { Spelling: not null } foreign)
      return $"'{foreign.Spelling}' is a spelling of {foreign.Language}, and the source is {language}";

    if (assumption.IsGlobal) {
      var global = module.Globals.FirstOrDefault(candidate => candidate.Name == assumption.Name);
      if (global is null)
        return $"the module has no global '{assumption.Name}'";

      type = global.Type;
      subject = $"the global '{global.Name}'";
      return null;
    }

    function = module.Functions.FirstOrDefault(candidate => candidate.Name == assumption.Function);
    if (function is null)
      return $"the module has no function '{assumption.Function}'";

    if (!function.IsPublic)
      return $"the function '{function.Name}' is not exported";

    if (assumption.IsReturn) {
      if (function.ReturnType == IrType.Void)
        return $"the function '{function.Name}' returns nothing";

      type = function.ReturnType;
      subject = $"the result of '{function.Name}'";
      return null;
    }

    var parameter = function.Parameters.FirstOrDefault(candidate => candidate.Name == assumption.Name);
    if (parameter is null)
      return $"the function '{function.Name}' has no parameter '{assumption.Name}'";

    type = parameter.Type;
    subject = $"the parameter '{parameter.Name}'";
    return null;
  }

  /// <summary>How a contract relates to the type the module proves for its target.</summary>
  private static Outcome Classify(Contract contract, IrType type) {
    if (type.IsOptional) {
      // The absent value is a value of the type: only a nullable contract the present value satisfies by its type has nothing to check.
      if (contract is not NullableContract nullable)
        return _uncheckable;

      var present = Classify(nullable.Inner, type.Underlying!);
      return present.Verdict is Verdict.NoEffect or Verdict.Contradiction ? present : _uncheckable;
    }

    return contract switch {
      // A type without an absent value satisfies the nullable contract wherever it satisfies the contract.
      NullableContract nullable => Classify(nullable.Inner, type),
      IntegerContract range => Range(range.Low, range.High, type),
      ScalarContract { Name: "bool" } => type == IrType.Bool ? _noEffect : Never(),
      ScalarContract { Name: "text" } => type == IrType.String ? _noEffect : Never(),
      ScalarContract { Name: "f32" or "f64" } floating => Floating(floating.Name, type),
      ScalarContract scalar => Range(Minimum(scalar.Name), Maximum(scalar.Name), type),
      SequenceContract sequence => Sequence(sequence, type),
      RecordContract record => Record(record, type),
      _ => throw new ArgumentOutOfRangeException(nameof(contract), contract, "not a contract")
    };
  }

  private static Outcome Never(string? fact = null) => new(Verdict.Contradiction, Fact: fact);

  private static BigInteger Minimum(string scalar) => scalar[0] == 'u' ? BigInteger.Zero : -(BigInteger.One << (Bits(scalar) - 1));

  private static BigInteger Maximum(string scalar) => (BigInteger.One << (scalar[0] == 'u' ? Bits(scalar) : Bits(scalar) - 1)) - 1;

  private static int Bits(string scalar) => int.Parse(scalar.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>An integer between two bounds against the type: a fixed-width integer is tested against the bounds that lie inside it.</summary>
  private static Outcome Range(BigInteger low, BigInteger high, IrType type) {
    if (type.IsFloat || type == IrType.BigInt)
      return _uncheckable;

    if (!type.IsInteger)
      return Never();

    var minimum = (BigInteger)IrOperators.Minimum(type);
    var maximum = (BigInteger)IrOperators.Maximum(type);
    if (high < minimum || low > maximum)
      return Never($", which never lies in [{low}, {high}]");

    if (low <= minimum && high >= maximum)
      return _noEffect;

    return new Outcome(Verdict.Check, value => {
      IrExpr? below = low > minimum ? new IrBinary(IrBinaryOp.Less, value, Constant(type, low)) : null;
      IrExpr? above = high < maximum ? new IrBinary(IrBinaryOp.Greater, value, Constant(type, high)) : null;
      return below is null ? above! : above is null ? below : new IrBinary(IrBinaryOp.LogicalOr, below, above);
    });
  }

  /// <summary>A constant of an integer type, a <c>u64</c> above the largest <c>i64</c> as its 64 bits.</summary>
  private static IrConst Constant(IrType type, BigInteger value) => new(type, (long)(ulong)(value & ulong.MaxValue));

  private static Outcome Floating(string scalar, IrType type) {
    if (type == IrType.F32 || (type == IrType.F64 && scalar == "f64"))
      return _noEffect;

    // Whether a double is a float, or an integer a floating-point number, needs a conversion and a comparison; no test is built for it yet.
    return type.IsNumeric ? _uncheckable : Never();
  }

  /// <summary>A sequence against an array: every element by its type, the length by one test.</summary>
  private static Outcome Sequence(SequenceContract sequence, IrType type) {
    if (type.Kind != IrTypeKind.Array)
      return type.IsCollection ? _uncheckable : Never();

    var element = Classify(sequence.Element, type.Element!);
    if (element.Verdict == Verdict.Contradiction)
      return Never($" whose elements are {type.Element}{element.Fact}");

    // A test of every element costs as much as the sequence is long.
    if (element.Verdict != Verdict.NoEffect)
      return _uncheckable;

    if (sequence.MinimumLength is not { } shortest)
      return _noEffect;

    var longest = sequence.MaximumLength!.Value;
    if (shortest > int.MaxValue)
      return Never($", which never has {shortest} elements");

    return new Outcome(Verdict.Check, value => {
      var length = new IrArrayLength(value);
      if (shortest == longest)
        return new IrBinary(IrBinaryOp.NotEqual, length, new IrConst(IrType.I32, (long)shortest));

      IrExpr shorter = new IrBinary(IrBinaryOp.Less, length, new IrConst(IrType.I32, (long)shortest));
      return longest >= int.MaxValue ? shorter : new IrBinary(IrBinaryOp.LogicalOr, shorter, new IrBinary(IrBinaryOp.Greater, length, new IrConst(IrType.I32, (long)longest)));
    });
  }

  /// <summary>A record against a struct or a class: every listed field must be one of the type's, and its type must guarantee its contract.</summary>
  private static Outcome Record(RecordContract record, IrType type) {
    if (type.Declaration is not { } declaration || !(type.IsStruct || type.ClassType is not null))
      return Never();

    var fields = declaration is IrClassType @class ? @class.AllFields : declaration.Fields;
    var outcomes = new List<Outcome>();
    foreach (var listed in record.Fields) {
      var field = fields.FirstOrDefault(candidate => candidate.Name == listed.Name);
      if (field is null)
        return Never($", which has no field '{listed.Name}'");

      var outcome = Classify(listed.Contract, field.Type);
      if (outcome.Verdict == Verdict.Contradiction)
        return Never($", whose field '{listed.Name}' has the type {field.Type}{outcome.Fact}");

      outcomes.Add(outcome);
    }

    return outcomes.All(outcome => outcome.Verdict == Verdict.NoEffect) ? _noEffect : _uncheckable;
  }

  /// <summary>The check of one contract: when the value violates it, an <c>ArgumentException</c> naming the contract is thrown.</summary>
  private static IrIf Guard(IrExpr violated, ContractAssumption assumption) {
    var exception = IrExceptionClass.ArgumentException;
    return new IrIf(violated, new IrBlock([
      new IrThrow(new IrNew(IrType.Object(exception), IrExceptionClass.Constructor(exception, 1), [new IrStrConst(ViolationPrefix + assumption.Text)]))
    ]), null);
  }

  /// <summary>Every <c>return</c> of a statement, the bodies of lambdas left alone, checked before it returns.</summary>
  private static IrStmt Results(IrStmt statement, IrFunction function, IReadOnlyList<(IrFunction Function, ContractAssumption Assumption, Func<IrExpr, IrExpr> Violated)> checks, List<IrLocal> locals) {
    switch (statement) {
      case IrBlock block:
        return new IrBlock([.. block.Statements.Select(inner => Results(inner, function, checks, locals))]);
      case IrReturn { Value: { } value }:
        var result = new IrLocal(Fresh(function, locals), function.ReturnType);
        locals.Add(result);
        return new IrBlock([
          new IrDeclare(result, value),
          .. checks.Select(check => Guard(check.Violated(new IrLocalRef(result)), check.Assumption)),
          new IrReturn(new IrLocalRef(result))
        ]);
      default:
        var blocks = IrTree.Blocks(statement);
        return blocks.Count == 0 ? statement : IrTree.With(statement, IrTree.Expressions(statement), [.. blocks.Select(block => (IrBlock)Results(block, function, checks, locals))]);
    }
  }

  /// <summary>A name no parameter or local of the function has.</summary>
  private static string Fresh(IrFunction function, IReadOnlyList<IrLocal> locals) {
    var taken = function.Parameters.Concat(locals).Select(local => local.Name).ToHashSet(StringComparer.Ordinal);
    var index = 0;
    while (taken.Contains($"contractResult{index}"))
      ++index;

    return $"contractResult{index}";
  }

}
