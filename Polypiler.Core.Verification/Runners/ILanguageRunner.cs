using System;
using System.Collections.Generic;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>Builds and executes source and generated programs for one language.</summary>
public interface ILanguageRunner {

  /// <summary>The command-line language identifier.</summary>
  string Language { get; }

  /// <summary>The required toolchain keys, in order of precedence.</summary>
  IReadOnlyList<string> Toolchains { get; }

  /// <summary>Builds or syntax-checks the supplied sources.</summary>
  CompileResult Compile(RunnerInput input);

  /// <summary>Runs a compiled program with the given arguments.</summary>
  RunResult Execute(CompileResult compiled, IReadOnlyList<string> arguments);

  /// <summary>Calls the named export once per input image and returns results in input order.</summary>
  IReadOnlyList<CallOutcome> ExecuteFunction(string directory, string export, CallSignature signature, IReadOnlyList<byte[]> inputImages);

  /// <summary>Executes the vectors and compares the results with their expectations.</summary>
  ComparisonReport RunVectors(RunnerInput input, VectorFile vectors);

  /// <summary>Executes vectors without expectations and returns them with the observed outcomes.</summary>
  VectorFile Record(RunnerInput input, VectorFile vectors);

  /// <summary>Runs the test suite of a generated project.</summary>
  TestReport RunTests(string projectDirectory);

}

/// <summary>A runner that can execute a trusted parser or binder shipped by a frontend.</summary>
public interface IHelperRunner {

  /// <summary>Runs the helper with the provided arguments and standard input.</summary>
  RunResult RunHelper(string helperPath, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> stdin);

}

/// <summary>A runner that can check the language's canonical formatting.</summary>
public interface IFormatChecker {

  /// <summary>Returns the relative paths of files that require formatting.</summary>
  IReadOnlyList<string> FormatCheck(string directory);

}
