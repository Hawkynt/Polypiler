using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.Go.Printing;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Go;

/// <summary>
/// Builds a generated Go module with the pinned <c>go</c> and runs calls of its exported functions
/// through a driver of the test's own, which lives in a module of its own beside the generated
/// package and calls each function directly with typed literals. It prints one outcome per call in
/// the notation of <see cref="PrototypeFixture.Outcome"/>; a panic is read back as the trap the IR
/// names (<c>docs/reference/exceptions.md</c>, Go): Go's run-time errors by their message without
/// <c>runtime error: </c>, the helpers' panics by their text. Every build and run is made with
/// <c>GOTOOLCHAIN=local</c>, <c>GOPROXY=off</c>, <c>GOFLAGS=-mod=mod</c> and <c>CGO_ENABLED=0</c>.
/// </summary>
internal static class GoDriver {

  private const string Show = """
    func show(v any) string {
    	if v == nil {
    		return "void"
    	}
    	value := reflect.ValueOf(v)
    	switch value.Kind() {
    	case reflect.Bool:
    		if value.Bool() {
    			return "true"
    		}
    		return "false"
    	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
    		return strconv.FormatInt(value.Int(), 10)
    	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
    		return strconv.FormatUint(value.Uint(), 10)
    	case reflect.Float32, reflect.Float64:
    		f := value.Float()
    		if f != f {
    			return "NaN"
    		}
    		if math.Abs(f) <= 9007199254740991 && math.Trunc(f) == f && !(f == 0 && math.Signbit(f)) {
    			return strconv.FormatInt(int64(f), 10)
    		}
    		return fmt.Sprintf("f:%016x", math.Float64bits(f))
    	case reflect.String:
    		return value.String()
    	case reflect.Slice:
    		parts := make([]string, value.Len())
    		for i := range parts {
    			parts[i] = show(value.Index(i).Interface())
    		}
    		return "[" + strings.Join(parts, ",") + "]"
    	case reflect.Struct:
    		parts := make([]string, value.NumField())
    		for i := range parts {
    			parts[i] = show(value.Field(i).Interface())
    		}
    		return "{" + strings.Join(parts, ",") + "}"
    	}
    	panic("a result of the kind " + value.Kind().String())
    }

    func trap(r any) string {
    	text := fmt.Sprint(r)
    	if err, ok := r.(error); ok {
    		text = strings.TrimPrefix(err.Error(), "runtime error: ")
    	}
    	switch {
    	case text == "integer divide by zero":
    		return "trap: divide by zero"
    	case strings.HasPrefix(text, "index out of range"), strings.HasPrefix(text, "slice bounds out of range"):
    		return "trap: index out of range"
    	case strings.HasPrefix(text, "makeslice:"):
    		return "trap: integer overflow"
    	case strings.HasPrefix(text, "invalid memory address"):
    		return "trap: null reference"
    	case text == "integer overflow", text == "divide by zero", text == "invalid operation", text == "index out of range", text == "argument out of range":
    		return "trap: " + text
    	}
    	return "exception: " + text
    }

    func run(call func() string) (out string) {
    	defer func() {
    		if r := recover(); r != nil {
    			out = trap(r)
    		}
    	}()
    	return call()
    }
    """;

  /// <summary>
  /// Runs calls through a generated module and returns one outcome per call.
  /// </summary>
  /// <param name="files">The files of the generated module: <c>go.mod</c>, the package and its helpers.</param>
  /// <param name="names">The Go name of every function by its name in the IR.</param>
  /// <param name="calls">The calls, each of a public function.</param>
  /// <param name="vet">Whether <c>go vet</c> and <c>gofmt -l</c> check the package first: generated code, not a source of the test's own.</param>
  /// <returns>The outcomes, in the order of the calls.</returns>
  public static IReadOnlyList<string> Outcomes(IReadOnlyList<GeneratedFile> files, IReadOnlyDictionary<string, string> names, IReadOnlyList<PrototypeCall> calls, bool vet = true) {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-go-test-" + Guid.NewGuid().ToString("N"));
    var package = Path.Combine(directory, "gen");
    Directory.CreateDirectory(package);
    try {
      var version = files.Single(file => file.Path == "go.mod").Content.Split('\n').Single(line => line.StartsWith("go ", StringComparison.Ordinal))[3..];
      foreach (var file in files.Where(file => file.Path.EndsWith(".go", StringComparison.Ordinal)))
        File.WriteAllText(Path.Combine(package, file.Path), file.Content);

      File.WriteAllText(Path.Combine(directory, "go.mod"), $"module polytest\n\ngo {version}\n");
      File.WriteAllText(Path.Combine(directory, "main.go"), Driver(names, calls));
      File.WriteAllText(Path.Combine(directory, "calls.json"), Calls(calls));
      if (vet) {
        var (vetExit, vetOutput) = Run(package, "vet", "./...");
        Assert.That((vetExit, vetOutput), Is.EqualTo((0, "")), "go vet");
        var formatted = Execute("gofmt", directory, "-l", package);
        Assert.That(formatted.Output.Trim(), Is.Empty, "gofmt -l lists the generated files");
      }
      var (exit, output) = Run(directory, "run", ".");
      Assert.That(exit, Is.Zero, output);
      var outcomes = JsonSerializer.Deserialize<string[]>(output)!;
      Assert.That(outcomes, Has.Length.EqualTo(calls.Count));
      return outcomes;
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>Runs a <c>go</c> command in a folder and returns its exit code and output, both streams.</summary>
  public static (int ExitCode, string Output) Run(string directory, params string[] arguments) {
    var result = Execute("go", directory, arguments);
    return (result.ExitCode, result.Output + result.Errors);
  }

  private static (int ExitCode, string Output, string Errors) Execute(string program, string directory, params string[] arguments) {
    var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    start.Environment["GOTOOLCHAIN"] = "local";
    start.Environment["GOPROXY"] = "off";
    start.Environment["GOFLAGS"] = "-mod=mod";
    start.Environment["CGO_ENABLED"] = "0";
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, output.Result, errors.Result);
  }

  /// <summary>
  /// The driver: it reads the calls from <c>calls.json</c>, each a function name and its arguments - an
  /// integer as its decimal digits, a floating-point number as the 64 bits of its double in
  /// hexadecimal, a list for an array - decodes them into the parameter types of the function, which
  /// reflection gives it, and calls it.
  /// </summary>
  private static string Driver(IReadOnlyDictionary<string, string> names, IReadOnlyList<PrototypeCall> calls) {
    var text = new StringBuilder("package main\n\nimport (\n\t\"encoding/json\"\n\t\"fmt\"\n\t\"math\"\n\t\"os\"\n\t\"reflect\"\n\t\"strconv\"\n\t\"strings\"\n\n\tgen \"polytest/gen\"\n)\n\n");
    text.Append("func call(name string, args []json.RawMessage) string {\n\tswitch name {\n");
    foreach (var method in calls.Select(call => call.Method).Distinct(StringComparer.Ordinal))
      text.Append($"\tcase {GoPrinter.Quote(method)}:\n\t\treturn invoke(reflect.ValueOf(gen.{names[method]}), args)\n");

    text.Append("\t}\n\tpanic(\"no function \" + name)\n}\n\n");
    text.Append("""
      func invoke(function reflect.Value, args []json.RawMessage) string {
      	in := make([]reflect.Value, function.Type().NumIn())
      	for i := range in {
      		in[i] = reflect.New(function.Type().In(i)).Elem()
      		set(in[i], args[i])
      	}
      	out := function.Call(in)
      	parts := []string{"void"}
      	if len(out) > 0 {
      		parts[0] = show(out[0].Interface())
      	}
      	for _, value := range in {
      		if value.Kind() == reflect.Slice {
      			parts = append(parts, show(value.Interface()))
      		}
      	}
      	return strings.Join(parts, " | ")
      }

      func set(target reflect.Value, raw json.RawMessage) {
      	switch target.Kind() {
      	case reflect.Slice:
      		var items []json.RawMessage
      		json.Unmarshal(raw, &items)
      		slice := reflect.MakeSlice(target.Type(), len(items), len(items))
      		for i, item := range items {
      			set(slice.Index(i), item)
      		}
      		target.Set(slice)
      	case reflect.Bool:
      		var b bool
      		json.Unmarshal(raw, &b)
      		target.SetBool(b)
      	case reflect.String:
      		var s string
      		json.Unmarshal(raw, &s)
      		target.SetString(s)
      	default:
      		var text string
      		json.Unmarshal(raw, &text)
      		switch target.Kind() {
      		case reflect.Float32, reflect.Float64:
      			bits, _ := strconv.ParseUint(text, 16, 64)
      			target.SetFloat(math.Float64frombits(bits))
      		case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
      			n, _ := strconv.ParseUint(text, 10, 64)
      			target.SetUint(n)
      		default:
      			n, _ := strconv.ParseInt(text, 10, 64)
      			target.SetInt(n)
      		}
      	}
      }

      func main() {
      	input, _ := os.ReadFile("calls.json")
      	var calls [][]json.RawMessage
      	json.Unmarshal(input, &calls)
      	outcomes := make([]string, len(calls))
      	for i, c := range calls {
      		var name string
      		json.Unmarshal(c[0], &name)
      		var args []json.RawMessage
      		json.Unmarshal(c[1], &args)
      		outcomes[i] = run(func() string { return call(name, args) })
      	}
      	out, _ := json.Marshal(outcomes)
      	os.Stdout.Write(out)
      }


      """).Append(Show).Append('\n');
    return text.ToString();
  }

  /// <summary>The calls as the driver reads them.</summary>
  private static string Calls(IReadOnlyList<PrototypeCall> calls)
    => JsonSerializer.Serialize(calls.Select(call => new object[] { call.Method, call.Arguments.Select(Encode).ToArray() }).ToArray());

  private static object Encode(object argument) => argument switch {
    Array array => array.Cast<object>().Select(Encode).ToArray(),
    bool or string => argument,
    float single => BitConverter.DoubleToUInt64Bits(single).ToString("x", CultureInfo.InvariantCulture),
    double number => BitConverter.DoubleToUInt64Bits(number).ToString("x", CultureInfo.InvariantCulture),
    char unit => ((int)unit).ToString(CultureInfo.InvariantCulture),
    _ => Convert.ToString(argument, CultureInfo.InvariantCulture)!
  };

  /// <summary>Whether a call can cross the boundary of the driver: every argument a number, truth value, string or array of them.</summary>
  public static bool Crosses(IrModule module, PrototypeCall call, IReadOnlyDictionary<string, string> names) {
    var function = module.Functions.FirstOrDefault(candidate => candidate.Name == call.Method);
    return function is { IsPublic: true } && names.ContainsKey(call.Method) && char.IsUpper(names[call.Method][0])
      && function.Parameters.All(parameter => Simple(parameter.Type)) && (function.ReturnType == IrType.Void || Simple(function.ReturnType) || function.ReturnType.IsStruct)
      && call.Arguments.All(argument => argument is not string text || !text.Any(char.IsSurrogate));
  }

  private static bool Simple(IrType type) => type.IsNumeric || type == IrType.Bool || type.IsString || (type.IsArray && Simple(type.Element!));

}
