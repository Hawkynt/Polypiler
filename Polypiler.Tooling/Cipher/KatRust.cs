using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Tooling.Cipher;

/// <summary>Why the tests cannot be written around a translation to Rust: the crate lacks what they reach it through.</summary>
/// <param name="message">What is missing.</param>
public sealed class KatRustException(string message) : Exception(message);

/// <summary>
/// What the known-answer tests reach of a translation to Rust, read back through the Rust syntax tree
/// of its files (#1365). Rust has no reflection, so the tests name everything they call, and their
/// arguments are literals of the types the translation declares. The translation is reached through:
/// <list type="bullet">
/// <item>the registry, a public function <c>algorithms()</c> of a module of the crate without
/// parameters, which gives the registered algorithms, <c>Vec&lt;Rc&lt;dyn Algorithm&gt;&gt;</c>;</item>
/// <item>the name of an algorithm, its accessor <c>field_name</c> (<c>&amp;RefCell&lt;Vec&lt;u16&gt;&gt;</c>,
/// <c>&amp;RefCell&lt;Option&lt;Vec&lt;u16&gt;&gt;&gt;</c> where the name may be null, <c>Vec&lt;u16&gt;</c> or <c>&amp;[u16]</c>);</item>
/// <item>the methods and properties of the algorithm and its instance, the JavaScript names in
/// <c>snake_case</c> - <c>create_instance</c>, <c>feed</c>, <c>result</c>, a property <c>key</c> through its
/// accessor <c>field_key</c> - each a method of the type the value has, or of a trait it implements.</item>
/// </list>
/// </summary>
public sealed class KatRustApi {

  private readonly Dictionary<string, RsTrait> _traits = new(StringComparer.Ordinal);
  private readonly List<RsImpl> _impls = [];

  private KatRustApi(string crate, int edition) {
    this.Crate = crate;
    this.Edition = edition;
  }

  /// <summary>The crate as the tests name it: its package name with <c>_</c> for <c>-</c>.</summary>
  public string Crate { get; }

  /// <summary>The edition of the crate, which decides its names.</summary>
  public int Edition { get; }

  /// <summary>The paths the tests import everything of: the crate's root and every module it declares that hold more than module declarations.</summary>
  public IReadOnlyList<string> Imports { get; private set; } = [];

  /// <summary>The type of the registered algorithms: the trait of <c>Rc&lt;dyn Trait&gt;</c>, or the struct of <c>Rc&lt;Struct&gt;</c>.</summary>
  public string Algorithm { get; private set; } = "";

  /// <summary>The element type of the registry, <c>Rc&lt;dyn Algorithm&gt;</c>.</summary>
  public string Registered { get; private set; } = "";

  /// <summary>The type of the name accessor of an algorithm.</summary>
  public string NameType { get; private set; } = "";

  /// <summary>Reads the crate a translation wrote to a folder: <c>Cargo.toml</c>, <c>src/lib.rs</c> and the file of every module it declares.</summary>
  /// <exception cref="KatRustException">The crate has no registry, or its algorithms no name.</exception>
  /// <exception cref="IOException">A file of the crate cannot be read.</exception>
  /// <exception cref="FormatException">A file is no Rust the reader takes.</exception>
  public static KatRustApi Read(string folder) {
    var manifest = File.ReadAllText(Path.Combine(folder, "Cargo.toml"));
    var name = ManifestKey(manifest, "name") ?? throw new KatRustException("the Cargo.toml of the translation names no package");
    var edition = int.TryParse(ManifestKey(manifest, "edition"), NumberStyles.None, CultureInfo.InvariantCulture, out var given) ? given : 2015;
    var sources = Path.Combine(folder, "src");
    var root = Parse(Path.Combine(sources, "lib.rs"));
    var files = new List<(string Path, RsFile File)> { (name.Replace('-', '_'), root) };
    foreach (var module in root.Items.OfType<RsModuleDeclaration>())
      files.Add(($"{name.Replace('-', '_')}::{module.Name}", Parse(Path.Combine(sources, module.Name + ".rs"))));
    return Of(name, edition, files);
  }

  /// <summary>The API of a crate from its files, each with the path the tests import it by.</summary>
  /// <param name="package">The package name.</param>
  /// <param name="edition">The edition.</param>
  /// <param name="files">The files: the crate's root first, then its modules.</param>
  /// <exception cref="KatRustException">The crate has no registry, or its algorithms no name.</exception>
  public static KatRustApi Of(string package, int edition, IReadOnlyList<(string Path, RsFile File)> files) {
    var api = new KatRustApi(package.Replace('-', '_'), edition);
    (string Path, RsFunction Function)? registry = null;
    foreach (var (path, file) in files) {
      foreach (var item in file.Items) {
        switch (item) {
          case RsTrait trait:
            api._traits[trait.Name] = trait;
            break;
          case RsImpl impl:
            api._impls.Add(impl);
            break;
          case RsFunction { Name: "algorithms", Visibility: "pub", Parameters.Count: 0 } function:
            registry ??= (path, function);
            break;
        }
      }
    }

    // A file of nothing but module declarations is not imported: its glob would bring in a module
    // of the crate's own name, which a path of the crate could then mean as well.
    api.Imports = [.. files.Where(file => file.File.Items.Any(item => item is not RsModuleDeclaration)).Select(file => file.Path)];
    if (registry is null)
      throw new KatRustException("the translation has no registry the tests can reach, a pub fn algorithms()");

    var element = Generic(registry.Value.Function.Returns ?? "", "Vec");
    var algorithm = element is null ? null : ObjectType(element);
    if (element is null || algorithm is null)
      throw new KatRustException($"the registry algorithms() gives {registry.Value.Function.Returns ?? "nothing"}, not a Vec of objects");

    api.Registered = element;
    api.Algorithm = algorithm;
    var accessor = api.Member(algorithm, "field_name", 0);
    if (accessor?.Returns is not ("&RefCell<Vec<u16>>" or "&RefCell<Option<Vec<u16>>>" or "Vec<u16>" or "&[u16]"))
      throw new KatRustException($"the algorithms of the translation have no name the tests can read, an accessor field_name of {algorithm}");

    api.NameType = accessor.Returns;
    return api;
  }

  /// <summary>
  /// A method of a type with a number of parameters besides the object: of a trait, its own or one of
  /// its supertraits; of a struct, one of its <c>impl</c> blocks or of a trait it implements.
  /// </summary>
  /// <param name="type">The trait or the struct.</param>
  /// <param name="name">The method.</param>
  /// <param name="arguments">The number of parameters besides the object.</param>
  /// <returns>The method, or <see langword="null"/> where the type has none of the name with that many parameters.</returns>
  public RsFunction? Member(string type, string name, int arguments) {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var pending = new Queue<string>();
    pending.Enqueue(type);
    foreach (var impl in this._impls.Where(impl => impl.TypeName == type)) {
      if (impl.Trait is { } trait)
        pending.Enqueue(trait);
      if (Find(impl.Items, name, arguments) is { } own)
        return own;
    }

    while (pending.TryDequeue(out var next)) {
      if (!seen.Add(next) || !this._traits.TryGetValue(next, out var trait))
        continue;
      if (Find(trait.Items, name, arguments) is { } found)
        return found;
      foreach (var super in trait.Supertraits)
        pending.Enqueue(super);
    }

    return null;
  }

  /// <summary>The type of the objects a type of the translation holds: <c>T</c> of <c>Rc&lt;dyn T&gt;</c> or <c>Rc&lt;T&gt;</c>; <see langword="null"/> of any other.</summary>
  public static string? ObjectType(string type) {
    var inner = Generic(type, "Rc");
    if (inner is null)
      return null;

    return inner.StartsWith("dyn ", StringComparison.Ordinal) ? inner[4..].Trim() : inner;
  }

  /// <summary>The argument of a generic type of one argument, <c>T</c> of <c>Vec&lt;T&gt;</c>; <see langword="null"/> where the type is not of that generic.</summary>
  public static string? Generic(string type, string generic) {
    var trimmed = type.Trim();
    return trimmed.StartsWith(generic + "<", StringComparison.Ordinal) && trimmed.EndsWith('>') ? trimmed[(generic.Length + 1)..^1].Trim() : null;
  }

  /// <summary>The parameters of a function besides its object, <c>&amp;self</c> or <c>self: &amp;Rc&lt;Self&gt;</c>.</summary>
  public static IReadOnlyList<RsParameter> Arguments(RsFunction function)
    => [.. function.Parameters.Where(parameter => !parameter.Name.EndsWith("self", StringComparison.Ordinal))];

  private static RsFunction? Find(IEnumerable<RsItem> items, string name, int arguments)
    => items.OfType<RsFunction>().FirstOrDefault(function => function.Name == name && Arguments(function).Count == arguments);

  private static RsFile Parse(string path) => RustSyntaxReader.Read(File.ReadAllText(path));

  /// <summary>The value of a key of <c>[package]</c> in a manifest, <c>name = "adler"</c>; <see langword="null"/> where it has none.</summary>
  private static string? ManifestKey(string manifest, string key) {
    var match = Regex.Match(manifest, $"^\\s*{key}\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline);
    return match.Success ? match.Groups[1].Value : null;
  }

}

/// <summary>The outcome of one test of <c>cargo test</c>.</summary>
/// <param name="Passed">Whether it passed.</param>
/// <param name="Message">Why it failed: the message of its panic, of the translation's own one with what panicked; empty when it passed.</param>
public sealed record KatRustOutcome(bool Passed, string Message);

/// <summary>
/// The Rust test crate of <c>cipher kat</c> (#1365): the translated crate below <c>crate/</c>, a
/// <c>Cargo.toml</c> that depends on it, and <c>tests/kat.rs</c> with one <c>#[test]</c> per vector
/// a translation can be checked on, its values Rust literals built as a Rust syntax tree; and the
/// reading of what <c>cargo test</c> reports.
/// </summary>
public static partial class KatRustCrate {

  /// <summary>The folder of the translated crate in the test crate.</summary>
  public const string Translation = "crate";

  /// <summary>The integer types of Rust with their ranges.</summary>
  private static readonly Dictionary<string, (BigInteger Min, BigInteger Max)> _integers = new(StringComparer.Ordinal) {
    ["u8"] = (byte.MinValue, byte.MaxValue), ["i8"] = (sbyte.MinValue, sbyte.MaxValue),
    ["u16"] = (ushort.MinValue, ushort.MaxValue), ["i16"] = (short.MinValue, short.MaxValue),
    ["u32"] = (uint.MinValue, uint.MaxValue), ["i32"] = (int.MinValue, int.MaxValue),
    ["u64"] = (ulong.MinValue, ulong.MaxValue), ["i64"] = (long.MinValue, long.MaxValue),
    ["usize"] = (ulong.MinValue, ulong.MaxValue), ["isize"] = (long.MinValue, long.MaxValue),
    ["u128"] = (UInt128.MinValue, UInt128.MaxValue), ["i128"] = (Int128.MinValue, Int128.MaxValue)
  };

  /// <summary>The name of the test of the vector at an index: <c>kat_1</c> for the first.</summary>
  public static string TestName(int index) => $"kat_{index + 1}";

  /// <summary>The name of a vector in the table, <c>Adler-32 #3</c>: its algorithm and its position among the algorithm's vectors.</summary>
  public static string Id(KatAlgorithm algorithm, KatVector vector) => $"{algorithm.Name} #{algorithm.Vectors.ToList().IndexOf(vector) + 1}";

  /// <summary>Writes the test crate around the translation in <c>crate/</c> of a folder.</summary>
  /// <returns>The name of every vector, in the order of the tests.</returns>
  /// <exception cref="KatRustException">The translation has no registry, or its algorithms no name.</exception>
  public static IReadOnlyList<string> Write(string folder, IReadOnlyList<(KatAlgorithm Algorithm, KatVector Vector)> checkable) {
    var api = KatRustApi.Read(Path.Combine(folder, Translation));
    Directory.CreateDirectory(Path.Combine(folder, "tests"));
    File.WriteAllText(Path.Combine(folder, "Cargo.toml"), Manifest(api.Crate));
    File.WriteAllText(Path.Combine(folder, "tests", "kat.rs"), Source(api, checkable));
    return [.. checkable.Select(entry => Id(entry.Algorithm, entry.Vector))];
  }

  /// <summary>The manifest of the test crate: a package of its own, its own workspace, the translation its one dependency; with LF line ends on every system.</summary>
  public static string Manifest(string crate) => $$"""
    [package]
    name = "kat-tests"
    version = "0.1.0"
    edition = "2024"
    publish = false

    [dependencies]
    {{crate}} = { path = "{{Translation}}" }

    [workspace]

    """.ReplaceLineEndings("\n");

  /// <summary>The text of <c>tests/kat.rs</c>: the fixed helpers, then the imports, the lookup of the registry and a test per vector.</summary>
  public static string Source(KatRustApi api, IReadOnlyList<(KatAlgorithm Algorithm, KatVector Vector)> checkable) {
    var items = new List<RsItem> { new RsUse("std::rc::Rc") };
    items.AddRange(api.Imports.Select(path => new RsUse(path + "::*")));
    items.Add(Registered(api));
    items.AddRange(checkable.Select((entry, index) => Test(api, entry.Algorithm, entry.Vector, index)));
    return KatHarness.Template("KatCheck.rs.txt") + RustPrinter.Print(new RsFile([], [], items));
  }

  /// <summary>
  /// <c>fn registered(name: &amp;str) -&gt; Rc&lt;dyn Algorithm&gt;</c>: the algorithm of the registry with the
  /// name, a panic where none has it.
  /// </summary>
  private static RsFunction Registered(KatRustApi api) {
    RsExpression name = new RsMethodCall(new RsPath("algorithm"), "field_name", []);
    if (api.NameType.StartsWith("&RefCell<", StringComparison.Ordinal))
      name = new RsUnary("*", new RsMethodCall(name, "borrow", []));
    // A name that may be null is compared as the Option it is: a null name is no algorithm's.
    RsExpression wanted = new RsCall(new RsPath("utf16"), [new RsPath("name")]);
    if (api.NameType.Contains("Option<", StringComparison.Ordinal))
      wanted = new RsCall(new RsPath("Some"), [wanted]);
    var found = new RsIf(new RsBinary(name, "==", new RsPath("wanted")), new RsBlock([new RsExpressionStatement(new RsReturn(new RsPath("algorithm")))], null), null);
    return new RsFunction("registered", "", [new RsParameter("name", "&str")], api.Registered, new RsBlock([
      new RsLet("wanted", false, null, wanted),
      new RsExpressionStatement(new RsFor(null, "algorithm", new RsCall(new RsPath("algorithms"), []), new RsBlock([new RsExpressionStatement(found)], null))),
      new RsExpressionStatement(new RsMacro("panic", [new RsString("the translation registers no algorithm named '{name}'")]))
    ], null));
  }

  /// <summary>
  /// The test of one vector: the algorithm, its instance, the steps, <c>feed</c> and the comparison of
  /// <c>result</c> with the recorded bytes. Where the translation lacks what a step needs, or takes a
  /// value no recorded value can be, the test does what it can up to that step and panics with why.
  /// </summary>
  public static RsFunction Test(KatRustApi api, KatAlgorithm algorithm, KatVector vector, int index) {
    var statements = new List<RsStatement> {
      new RsLet("algorithm", false, null, new RsCall(new RsPath("registered"), [new RsString(algorithm.Name)]))
    };
    try {
      var create = api.Member(api.Algorithm, "create_instance", 1) ?? api.Member(api.Algorithm, "create_instance", 0)
        ?? throw new KatRustException($"the translation of {api.Algorithm} has no method 'CreateInstance' of 1 arguments");
      var arguments = KatRustApi.Arguments(create).Count == 1
        ? [Value(KatValueOf("{\"t\":\"bool\",\"v\":" + (vector.Inverse ? "true" : "false") + "}"), KatRustApi.Arguments(create)[0].TypeName)]
        : new List<RsExpression>();
      var (made, type) = Unwrapped(new RsMethodCall(new RsPath("algorithm"), "create_instance", arguments), create.Returns, "CreateInstance");
      if (KatRustApi.Generic(type, "Option") is { } optional) {
        made = new RsMethodCall(made, "expect", [new RsString("CreateInstance gave no instance")]);
        type = optional;
      }

      var instance = KatRustApi.ObjectType(type) ?? throw new KatRustException($"CreateInstance gives {type}, not an object");
      statements.Add(new RsLet("instance", false, null, made));
      foreach (var step in vector.Steps)
        statements.Add(Statement(step.IsCall ? Call(api, instance, step.Name, step.Values).Expression : Set(api, instance, step.Name, step.Values[0])));
      statements.Add(Statement(Call(api, instance, "Feed", [vector.Input]).Expression));
      var (result, shape) = Call(api, instance, "Result", []);
      statements.Add(new RsExpressionStatement(new RsCall(new RsPath("check"), [
        new RsReference(false, new RsIndex(Sequence(result, shape), new RsRange(null, null, false))),
        new RsReference(false, new RsArray([.. vector.Answer!.Bytes!.Select(item => (RsExpression)new RsLiteral(item.ToString(CultureInfo.InvariantCulture)))]))
      ])));
    } catch (KatRustException problem) {
      statements.Add(new RsExpressionStatement(Panic(problem.Message)));
    }

    return new RsFunction(TestName(index), "", [], null, new RsBlock(statements, null)) {
      Docs = [Id(algorithm, vector).Replace('\r', ' ').Replace('\n', ' ')],
      Attributes = ["test"]
    };
  }

  /// <summary>
  /// A call whose value is dropped: as a statement; <c>let _ = match … { … };</c> where the call hands
  /// back a <c>Result</c>, whose <c>match</c> would otherwise be a statement that must give nothing.
  /// </summary>
  private static RsStatement Statement(RsExpression call) => call is RsMatch ? new RsLet("_", false, null, call) : new RsExpressionStatement(call);

  /// <summary>What a method gives as a sequence of numbers: a vector, a slice or an array of numbers, also behind an <c>Option</c>; a panic of the test for anything else.</summary>
  private static RsExpression Sequence(RsExpression result, string type) {
    if (KatRustApi.Generic(type, "Option") is { } optional) {
      result = new RsMethodCall(result, "expect", [new RsString("Result gave nothing, not a sequence of bytes")]);
      type = optional;
    }

    var element = KatRustApi.Generic(type, "Vec") ?? KatRustApi.Generic(type.TrimStart('&'), "Vec") ?? Slice(type.TrimStart('&').Trim());
    return element is not null && Scalar(element)
      ? result is RsMatch ? new RsParenthesized(result) : result
      : throw new KatRustException($"Result gives {(type.Length == 0 ? "nothing" : type)}, not a sequence of bytes");
  }

  /// <summary>The element type of <c>[T]</c> or <c>[T; N]</c>; <see langword="null"/> of any other type.</summary>
  private static string? Slice(string type) {
    if (!type.StartsWith('[') || !type.EndsWith(']'))
      return null;

    var inner = type[1..^1];
    var semicolon = inner.LastIndexOf(';');
    return (semicolon < 0 ? inner : inner[..semicolon]).Trim();
  }

  /// <summary>A call of a method of the instance by its JavaScript name, with its value and the type of that value.</summary>
  private static (RsExpression Expression, string Type) Call(KatRustApi api, string type, string name, IReadOnlyList<KatValue> values) {
    var method = api.Member(type, RustNames.Snake(name, api.Edition), values.Count)
      ?? throw new KatRustException($"the translation of {type} has no method '{name}' of {values.Count} arguments");
    var parameters = KatRustApi.Arguments(method);
    var arguments = values.Select((value, index) => Value(value, parameters[index].TypeName)).ToList();
    return Unwrapped(new RsMethodCall(new RsPath("instance"), method.Name, arguments), method.Returns, name);
  }

  /// <summary>A property of the instance set through its accessor: <c>instance.field_key().set(v)</c> of a <c>Cell</c>, <c>*instance.field_key().borrow_mut() = v</c> of a <c>RefCell</c>.</summary>
  private static RsExpression Set(KatRustApi api, string type, string name, KatValue value) {
    var accessor = api.Member(type, "field_" + RustNames.Snake(name, api.Edition), 0);
    var cell = accessor?.Returns is { } returns ? KatRustApi.Generic(returns.TrimStart('&'), "Cell") : null;
    var shared = accessor?.Returns is { } held ? KatRustApi.Generic(held.TrimStart('&'), "RefCell") : null;
    if (accessor is null || (cell ?? shared) is null)
      throw new KatRustException($"the translation of {type} has no property '{name}' to set");

    var field = new RsMethodCall(new RsPath("instance"), accessor.Name, []);
    return cell is not null
      ? new RsMethodCall(field, "set", [Value(value, cell)])
      : new RsAssign(new RsUnary("*", new RsMethodCall(field, "borrow_mut", [])), "=", Value(value, shared!));
  }

  /// <summary>The value of a call whose method may hand back an exception as a <c>Result</c>, which fails the test.</summary>
  private static (RsExpression Expression, string Type) Unwrapped(RsExpression call, string? returns, string name) {
    var type = returns ?? "";
    if (KatRustApi.Generic(type, "Result") is not { } outcome)
      return (call, type);

    var comma = Comma(outcome);
    return (new RsMatch(call, [
      new RsMatchArm(["Ok(value)"], new RsPath("value")),
      new RsMatchArm(["Err(_)"], Panic($"{name} threw"))
    ]), comma < 0 ? outcome : outcome[..comma].Trim());
  }

  /// <summary>The comma that separates the two arguments of a generic type, outside any nested one; -1 where there is none.</summary>
  private static int Comma(string arguments) {
    var depth = 0;
    for (var index = 0; index < arguments.Length; ++index) {
      switch (arguments[index]) {
        case '<' or '(' or '[':
          ++depth;
          break;
        case '>' or ')' or ']':
          --depth;
          break;
        case ',' when depth == 0:
          return index;
      }
    }

    return -1;
  }

  /// <summary><c>panic!("{}", "text")</c>: a panic whose text is a literal, whatever braces it holds.</summary>
  private static RsMacro Panic(string text) => new("panic", [new RsString("{}"), new RsString(text)]);

  private static KatValue KatValueOf(string json) {
    using var document = System.Text.Json.JsonDocument.Parse(json);
    return KatValue.Of(document.RootElement);
  }

  /// <summary>
  /// A recorded value as a Rust literal of the type a parameter, a cell or an element has: a number of
  /// an integer type where it is a whole number in its range, of a floating-point type, a truth value,
  /// a text as its UTF-16 code units, a sequence as an array, a slice borrowed from one or a vector, a
  /// null as <c>None</c> of an <c>Option</c>.
  /// </summary>
  /// <exception cref="KatRustException">The value cannot be one of the type.</exception>
  public static RsExpression Value(KatValue value, string type) {
    type = type.Trim();
    var json = value.Json.GetProperty("v");
    if (KatRustApi.Generic(type, "Option") is { } optional)
      return value.Kind is "null" or "undefined" ? new RsPath("None") : new RsCall(new RsPath("Some"), [Value(value, optional)]);
    if (type.StartsWith("&mut ", StringComparison.Ordinal))
      return new RsReference(true, Owned(value, type[5..].Trim(), json));
    if (type.StartsWith('&'))
      return new RsReference(false, Owned(value, type[1..].Trim(), json));
    return Owned(value, type, json);
  }

  private static RsExpression Owned(KatValue value, string type, System.Text.Json.JsonElement json) {
    var element = Slice(type);
    var vector = KatRustApi.Generic(type, "Vec");
    switch (value.Kind) {
      case "bool" when type == "bool":
        return new RsLiteral(json.GetBoolean() ? "true" : "false");
      case "number" when Scalar(type):
        return Number(NumberOf(json), type);
      case "bigint" when Scalar(type):
        var big = BigInteger.Parse(json.GetString()!, CultureInfo.InvariantCulture);
        return _integers.ContainsKey(type) ? Integer(big, type, big.ToString(CultureInfo.InvariantCulture)) : Number((double)big, type);
      case "string" when (element ?? vector) == "u16":
        return Text(Units(json.GetRawText()), vector is not null);
      case "bytes" or "typed" or "array" when (element ?? vector) is { } item:
        var items = value.Kind switch {
          "bytes" => Convert.FromHexString(json.GetString()!).Select(octet => Number(octet, item)).ToList(),
          "typed" => [.. json.EnumerateArray().Select(entry => Number(NumberOf(entry), item))],
          _ => [.. json.EnumerateArray().Select(entry => Value(KatValue.Of(entry), item))]
        };
        var array = new RsArray(items);
        return vector is null ? array : new RsMacro("vec", [array]) { Bracketed = true };
      default:
        throw new KatRustException($"a recorded value of the kind '{value.Kind}' cannot be given as {type}");
    }
  }

  private static bool Scalar(string type) => _integers.ContainsKey(type) || type is "f32" or "f64";

  /// <summary>A recorded number: JSON's own, or the text of one JSON has none for (<c>NaN</c>, <c>Infinity</c>, <c>-0</c>).</summary>
  private static double NumberOf(System.Text.Json.JsonElement json)
    => json.ValueKind == System.Text.Json.JsonValueKind.String ? double.Parse(json.GetString()!, CultureInfo.InvariantCulture) : json.GetDouble();

  /// <summary>A number as a literal of an integer or a floating-point type.</summary>
  private static RsExpression Number(double number, string type) {
    if (type is "f32" or "f64") {
      if (double.IsNaN(number))
        return new RsPath($"{type}::NAN");
      if (double.IsInfinity(number))
        return new RsPath(number > 0 ? $"{type}::INFINITY" : $"{type}::NEG_INFINITY");
      var text = number.ToString("R", CultureInfo.InvariantCulture);
      return new RsLiteral(text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text.Replace("E+", "e", StringComparison.Ordinal).Replace('E', 'e') : text + ".0");
    }

    if (!_integers.ContainsKey(type))
      throw new KatRustException($"the number {number.ToString("R", CultureInfo.InvariantCulture)} is given where the translation takes {type}");
    if (double.IsNaN(number) || double.IsInfinity(number) || number != Math.Floor(number))
      throw new KatRustException($"the number {number.ToString("R", CultureInfo.InvariantCulture)} is given where the translation takes an integer {type}");
    return Integer(new BigInteger(number), type, number.ToString("R", CultureInfo.InvariantCulture));
  }

  private static RsLiteral Integer(BigInteger number, string type, string shown) {
    var (min, max) = _integers[type];
    if (number < min || number > max)
      throw new KatRustException($"the number {shown} is outside the range of {type} the translation takes");
    return new RsLiteral(number.ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>
  /// A text as its UTF-16 code units: <c>utf16("text")</c>, a <c>Vec&lt;u16&gt;</c> borrowed where a slice
  /// is taken; the units one by one where the text holds a lone surrogate, which no Rust string can.
  /// </summary>
  private static RsExpression Text(string text, bool owned) {
    if (!Lone(text))
      return new RsCall(new RsPath("utf16"), [new RsString(text)]);

    var units = new RsArray([.. text.Select(unit => (RsExpression)new RsLiteral(((int)unit).ToString(CultureInfo.InvariantCulture)))]);
    return owned ? new RsMacro("vec", [units]) { Bracketed = true } : units;
  }

  /// <summary>
  /// The code units of a JSON string as it is written, quotes included: JavaScript writes a lone
  /// surrogate as <c>\ud800</c>, which <c>System.Text.Json</c> refuses to decode.
  /// </summary>
  private static string Units(string raw) {
    var text = new System.Text.StringBuilder();
    for (var index = 1; index < raw.Length - 1; ++index) {
      if (raw[index] != '\\') {
        text.Append(raw[index]);
        continue;
      }

      var escaped = raw[++index];
      if (escaped == 'u') {
        text.Append((char)Convert.ToUInt16(raw.Substring(index + 1, 4), 16));
        index += 4;
        continue;
      }

      text.Append(escaped switch { 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped });
    }

    return text.ToString();
  }

  /// <summary>Whether a text holds a surrogate that is not one of a pair.</summary>
  private static bool Lone(string text) {
    for (var index = 0; index < text.Length; ++index) {
      if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        ++index;
      else if (char.IsSurrogate(text[index]))
        return true;
    }

    return false;
  }

  /// <summary>
  /// The outcome of every test of what <c>cargo test</c> wrote on its standard output, by the test's
  /// name: <c>test kat_1 ... ok</c>, and for a failed one the first line of its panic - as it is where the
  /// test itself panicked, after <c>the translation panicked: </c> where the translation did.
  /// </summary>
  public static IReadOnlyDictionary<string, KatRustOutcome> ReadTests(string output) {
    var text = output.ReplaceLineEndings("\n");
    var panics = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (Match panic in PanicLine().Matches(text)) {
      var message = panic.Groups["message"].Value.Trim();
      panics.TryAdd(panic.Groups["name"].Value, panic.Groups["at"].Value.StartsWith("tests/", StringComparison.Ordinal) ? message : $"the translation panicked: {message}");
    }

    var outcomes = new Dictionary<string, KatRustOutcome>(StringComparer.Ordinal);
    foreach (Match test in TestLine().Matches(text)) {
      var name = test.Groups["name"].Value;
      outcomes[name] = test.Groups["outcome"].Value == "ok" ? new(true, "") : new(false, panics.GetValueOrDefault(name) ?? "failed without a message");
    }

    return outcomes;
  }

  /// <summary>
  /// The first error of a build Cargo gave up on: <c>E0599: no method named …</c> of
  /// <c>error[E0599]: no method named …</c>, the message of an <c>error:</c> without a code; the
  /// summaries Cargo adds after the compiler's errors only where there is nothing else.
  /// </summary>
  public static string FirstError(string text) {
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
    var errors = lines.Select(line => ErrorLine().Match(line)).Where(match => match.Success).ToList();
    var error = errors.FirstOrDefault(match => !Summary(match.Groups["message"].Value)) ?? errors.FirstOrDefault();
    if (error is null)
      return lines.FirstOrDefault() ?? "no output";

    var code = error.Groups["code"].Value;
    return code.Length == 0 ? error.Groups["message"].Value : $"{code}: {error.Groups["message"].Value}";
  }

  private static bool Summary(string message)
    => message.StartsWith("could not compile", StringComparison.Ordinal) || message.StartsWith("test failed", StringComparison.Ordinal)
      || message.Contains("target failed", StringComparison.Ordinal) || message.StartsWith("aborting due to", StringComparison.Ordinal);

  [GeneratedRegex(@"^test (?<name>\S+) \.\.\. (?<outcome>ok|FAILED)", RegexOptions.Multiline)]
  private static partial Regex TestLine();

  [GeneratedRegex(@"^thread '(?<name>[^']+)'(?: \([^)\n]*\))? panicked at (?<at>[^\n]*?):\d+:\d+:\n(?<message>[^\n]*)", RegexOptions.Multiline)]
  private static partial Regex PanicLine();

  [GeneratedRegex(@"^error(?:\[(?<code>E\d+)\])?: (?<message>.+)$")]
  private static partial Regex ErrorLine();

}
