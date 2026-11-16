using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The code for what the trap policy <c>result</c> cannot write without a panic.</summary>
  public const string TrapPolicyLimit = "PLY1552";

  /// <summary>The registered diagnostic of <see cref="TrapPolicyLimit"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TrapPolicyLimitDescriptor = new(TrapPolicyLimit, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "rust.backend.trap-policy");

  /// <summary>The name of the error type of a crate under the trap policy <c>result</c> whose module has no exception classes.</summary>
  public const string TrapErrorType = "Error";

  /// <summary>The traps whose codes the issues fixed first, in code order; every other trap follows them in the order of <see cref="IrTrap"/>.</summary>
  private static readonly IrTrap[] _firstCodes = [IrTrap.DivideByZero, IrTrap.IntegerOverflow, IrTrap.IndexOutOfRange];

  /// <summary>
  /// The discriminant of a trap's variant in the crate's <c>Error</c> enum: <c>DivideByZero</c> 1,
  /// <c>IntegerOverflow</c> 2, <c>IndexOutOfRange</c> 3, then the other traps from 4 in the order of
  /// <see cref="IrTrap"/>. A variant keeps its number in every crate.
  /// </summary>
  /// <param name="trap">The trap.</param>
  /// <returns>The code.</returns>
  public static int ErrorCode(IrTrap trap) {
    var first = Array.IndexOf(_firstCodes, trap);
    return first >= 0 ? first + 1 : _firstCodes.Length + 1 + Enum.GetValues<IrTrap>().Where(other => !_firstCodes.Contains(other)).ToList().IndexOf(trap);
  }

  /// <summary>
  /// The functions of a module that return a <c>Result</c> in its crate: under the trap policy
  /// <c>result</c> every function a trap or an exception can leave, under <c>panic</c> those an
  /// exception of a class of the module leaves and that are no export.
  /// </summary>
  /// <param name="module">A module, legalized for the profile, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The Rust to write.</param>
  /// <param name="capabilities">The table; the default one when <see langword="null"/>.</param>
  /// <returns>The IR names of the functions.</returns>
  public static IReadOnlySet<string> Fallible(IrModule module, RustProfile profile, RustCapabilities? capabilities = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    var (writer, _) = Written(module, profile, capabilities ?? RustCapabilities.Default, RustEmitOptions.Default);
    return writer.Throwing;
  }

  /// <summary>
  /// The writer of a module that has built its crate. Under the trap policy <c>result</c> which
  /// functions return a <c>Result</c> depends on what their bodies write: the crate is built again,
  /// with every function that let an error out declared fallible, until no build adds one - a fixed
  /// point over the calls, reached since the set only grows and is bounded by the module's functions.
  /// </summary>
  private static (Writer Writer, RustCrate? Crate) Written(IrModule module, RustProfile profile, RustCapabilities capabilities, RustEmitOptions options) {
    var fallible = new HashSet<string>(StringComparer.Ordinal);
    while (true) {
      var writer = new Writer(profile, capabilities, options, fallible);
      var crate = writer.Crate(module);
      var known = fallible.Count;
      fallible.UnionWith(writer.Escaping);
      if (fallible.Count == known)
        return (writer, crate);
    }
  }

  private sealed partial class Writer {

    /// <summary>The functions declared fallible before the crate is built, from the build before (<see cref="Written"/>).</summary>
    private readonly IReadOnlySet<string> _declared = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The functions an error left under the trap policy <c>result</c>: each must return a <c>Result</c>.</summary>
    private readonly HashSet<string> _escaping = new(StringComparer.Ordinal);

    /// <summary>The traps the crate's <c>Error</c> enum has a variant for.</summary>
    private readonly SortedSet<IrTrap> _traps = new(Comparer<IrTrap>.Create((a, b) => ErrorCode(a).CompareTo(ErrorCode(b))));

    /// <summary>Whether a spelling function is being built, whose errors leave it with <c>return Err(e)</c> whatever stands around the call that asked for it.</summary>
    private bool _spelling;

    /// <summary>Creates the writer of a module with the functions declared fallible already.</summary>
    public Writer(RustProfile profile, RustCapabilities capabilities, RustEmitOptions options, IReadOnlySet<string> fallible) : this(profile, capabilities, options)
      => this._declared = fallible;

    /// <summary>The functions an error left, which must return a <c>Result</c>.</summary>
    public IReadOnlySet<string> Escaping => this._escaping;

    /// <summary>The functions that return a <c>Result</c>.</summary>
    public IReadOnlySet<string> Throwing => this._throwing;

    /// <summary>Whether a trap is an error value: the trap policy <c>result</c>.</summary>
    private bool Results => this._profile.TrapPolicy == RustTrapPolicy.Result;

    /// <summary>Whether an error is an exception object, <c>Rc&lt;PlyError&gt;</c>, rather than a variant of the crate's <c>Error</c>.</summary>
    private bool PlyErrors => this._errorClasses.Count > 0;

    /// <summary>Records what the trap policy <c>result</c> cannot write (<c>PLY1552</c>) and returns a stand-in.</summary>
    private RsExpression LimitResult(string what) {
      if (this._refused.Add(what))
        this._problems.Add(TrapPolicyLimitDescriptor.At($"trap policy 'result' is not available for '{what}' (in {this._construct})"));

      return new RsPath("_refused");
    }

    /// <summary>The error a trap is: <c>Error::DivideByZero</c>, or the exception of the trap's class with its default message, <c>ply_trap(2)</c>.</summary>
    private RsExpression TrapError(IrTrap trap) {
      if (!this.PlyErrors) {
        this._traps.Add(trap);
        return new RsPath($"{TrapErrorType}::{trap}");
      }

      return new RsCall(new RsPath(this.TrapHelper()), [new RsLiteral(this.ErrorClass(IrExceptionClass.Of(trap)).ToString(CultureInfo.InvariantCulture))]) { Type = IrExceptionClass.Reference };
    }

    /// <summary>
    /// The spelling function <c>ply_trap(class)</c>: the exception a trap of a class throws, with the
    /// default message of the class from <c>PLY_ERROR_MESSAGES</c> and no inner exception.
    /// </summary>
    private string TrapHelper() {
      const string name = "ply_trap";
      var error = this.StringHelper("ply_error");
      var utf16 = this.StringHelper("ply_utf16");
      this.Spell(name, () => new RsFunction(name, "", [new RsParameter("class", "usize")], ErrorType, new RsBlock([],
        new RsCall(new RsPath(error), [new RsPath("class"), new RsCall(new RsPath(utf16), [new RsIndex(new RsPath("PLY_ERROR_MESSAGES"), new RsPath("class"))]), new RsPath("None")]))));
      // Called wherever a trap may happen: inlined, it would only make every one of them long.
      this._spellings[name] = this._spellings[name] with { IsHelper = false };
      return name;
    }

    /// <summary><c>panic!("Trap")</c>; under the trap policy <c>result</c> the trap's error leaving where it stands.</summary>
    private RsExpression Trap(IrTrap trap) => this.Results ? this.Fail(this.TrapError(trap)) : Panic(trap);

    /// <summary>An error leaving where it stands: a spelling function with <c>return Err(e)</c>, any other code as <see cref="Escape"/> says; a closure cannot hand it on.</summary>
    private RsExpression Fail(RsExpression error) {
      if (this._spelling)
        return new RsReturn(new RsCall(new RsPath("Err"), [error]));
      if (this.Results && this._closureResults.Count > 0)
        return this.LimitResult("a trap inside a closure");

      return this.Escape(error);
    }

    /// <summary>
    /// The value of an <c>Option</c> or a <c>Result</c> that holds nothing where an operation traps:
    /// <c>value.expect("Trap")</c>; under the trap policy <c>result</c> <c>value.ok_or(Error::Trap)?</c>
    /// (<c>map_err(|_| …)?</c> for a <c>Result</c>) where the error leaves the function, else a
    /// <c>match</c> whose empty arm leaves the <c>try</c> body with it.
    /// </summary>
    private RsExpression Checked(RsExpression value, IrTrap trap) {
      if (!this.Results)
        return Expect(value, trap);

      var result = value is RsCall { Callee: RsPath { Name: var callee } } && callee.EndsWith("::try_from", StringComparison.Ordinal);
      var error = this.TrapError(trap);
      var fail = this.Fail(error);
      if (fail is RsReturn { Value: RsCall { Arguments: [var returned] } }) {
        var lazy = this.PlyErrors;
        RsExpression converted = result
          ? new RsMethodCall(this.Operand(value, 15), "map_err", [new RsClosure(false, [new RsParameter("_", "")], returned)])
          : new RsMethodCall(this.Operand(value, 15), lazy ? "ok_or_else" : "ok_or", [lazy ? new RsClosure(false, [], returned) : returned]);
        return new RsTry(converted) { Type = value.Type };
      }

      if (fail is RsPath)
        return fail;

      return new RsMatch(value, [
        new RsMatchArm([result ? "Ok(value)" : "Some(value)"], new RsPath("value")),
        new RsMatchArm([result ? "Err(_)" : "None"], fail)
      ]) { Type = value.Type };
    }

    /// <summary>
    /// An element of an array or a string as a place: <c>a[i]</c>; under the trap policy <c>result</c>
    /// <c>*a.get(i).ok_or(Error::IndexOutOfRange)?</c>, <c>get_mut</c> where it is written, so that an
    /// index outside the array is the trap's error and no panic.
    /// </summary>
    private RsExpression Element(RsExpression target, RsExpression index, IrType? type, bool write = false) {
      if (!this.Results)
        return new RsIndex(target, index) { Type = type };

      var element = this.Checked(new RsMethodCall(this.Operand(target, 15), write ? "get_mut" : "get", [index]), IrTrap.IndexOutOfRange);
      return new RsUnary("*", this.Operand(element, 13)) { Type = type };
    }

    /// <summary>The call of a spelling function that traps: as it is; under the trap policy <c>result</c>, where it returns a <c>Result</c>, its error handed on.</summary>
    private RsExpression Spelled(RsExpression call) => this.Results ? this.Propagated(call) : call;

    /// <summary>Builds a spelling function under the trap policy <c>result</c>: its traps leave it with <c>return Err(e)</c>, its result type is a <c>Result</c>.</summary>
    private RsFunction SpelledFallible(Func<RsFunction> build) {
      if (!this.Results)
        return build();

      var spelling = this._spelling;
      this._spelling = true;
      try {
        var function = build();
        return function with { Returns = Fallible(function.Returns, this.FailureType), Body = function.Body with { Tail = Succeeded(function.Body.Tail) } };
      } finally {
        this._spelling = spelling;
      }
    }

    /// <summary>
    /// The value a fallible spelling function ends with: <c>Ok(v)</c>; an error handed on as the value
    /// itself rather than <c>Ok(x?)</c>, an error returned as <c>Err(e)</c>, each branch of an <c>if</c> on its own.
    /// </summary>
    private static RsExpression Succeeded(RsExpression? tail) => tail switch {
      null => new RsCall(new RsPath("Ok"), [new RsTuple([])]),
      RsTry handed => handed.Operand,
      RsReturn { Value: { } error } => error,
      RsIf { Else: { } otherwise } choice => choice with { Then = choice.Then with { Tail = Succeeded(choice.Then.Tail) }, Else = Succeeded(otherwise) },
      RsBlockExpression block => block with { Block = block.Block with { Tail = Succeeded(block.Block.Tail) } },
      _ => new RsCall(new RsPath("Ok"), [tail])
    };

    /// <summary>The Rust type of an error: <c>Rc&lt;PlyError&gt;</c>, or the crate's <c>Error</c>.</summary>
    private string FailureType => this.Results && !this.PlyErrors ? TrapErrorType : ErrorType;

    /// <summary>
    /// The items of <c>src/error.rs</c> under the trap policy <c>result</c>, where the module has no
    /// exception classes and a trap can happen: the <c>Error</c> enum with one variant per trap, its
    /// code as the discriminant, <c>Display</c> printing the trap's name, and <c>std::error::Error</c>.
    /// </summary>
    private RsFile? ErrorFile() {
      if (!this.Results || this.PlyErrors || this._traps.Count == 0)
        return null;

      var enumeration = new RsEnum(TrapErrorType, "pub", [.. this._traps.Select(trap => new RsVariant(trap.ToString(), ErrorCode(trap).ToString(CultureInfo.InvariantCulture)))]) {
        Docs = ["A trap of the generated code as an error value: what the source would have thrown or panicked with."],
        Attributes = ["derive(Clone, Copy, Debug, PartialEq, Eq)", "repr(u16)"]
      };
      var display = new RsImpl("std::fmt::Display", TrapErrorType, [
        new RsFunction("fmt", "", [new RsParameter("&self", ""), new RsParameter("f", "&mut std::fmt::Formatter<'_>")], "std::fmt::Result",
          new RsBlock([], new RsCall(new RsPath("std::fmt::Debug::fmt"), [new RsPath("self"), new RsPath("f")])))
      ]);
      return new RsFile([], [], [enumeration, display, new RsImpl("std::error::Error", TrapErrorType, [])]);
    }

    /// <summary><c>Display</c> of an exception under the trap policy <c>result</c>: the name of its trap where its class is a trap's, else its class's name.</summary>
    private IEnumerable<RsItem> ErrorDisplay() {
      var count = this._errorClasses.Count.ToString(CultureInfo.InvariantCulture);
      if (this._spellings.ContainsKey("ply_trap"))
        yield return new RsConst("PLY_ERROR_MESSAGES", "", $"[&str; {count}]",
          new RsArray([.. this._errorClasses.Select(type => (RsExpression)new RsString(IrExceptionClass.DefaultMessage(type)))])) { IsStatic = true };
      yield return new RsConst("PLY_ERROR_DISPLAY", "", $"[&str; {count}]",
        new RsArray([.. this._errorClasses.Select(type => (RsExpression)new RsString(TrapOf(type)?.ToString() ?? type.Name))])) { IsStatic = true };
      yield return new RsImpl("std::fmt::Display", "PlyError", [
        new RsFunction("fmt", "", [new RsParameter("&self", ""), new RsParameter("f", "&mut std::fmt::Formatter<'_>")], "std::fmt::Result",
          new RsBlock([], new RsMethodCall(new RsPath("f"), "write_str", [new RsIndex(new RsPath("PLY_ERROR_DISPLAY"), new RsField(new RsPath("self"), "class"))])))
      ]);
    }

    /// <summary>
    /// Refuses, under the trap policy <c>result</c>, a call of a helper that may panic: the helpers of
    /// the strings and collections signal their traps by panicking, which no caller can turn into an error.
    /// </summary>
    private void PanicFree(RsFunction function) {
      if (!this.Results)
        return;

      foreach (var node in RustSyntaxRewriter.DescendantsAndSelf(function.Body))
        if (node is RsCall { Callee: RsPath { Name: var name } } && Polypiler.Core.Backends.Rust.Helpers.Strings.Panics(name))
          this.LimitResult($"the helper {name}, which panics");
    }

  }

}
