using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Polypiler.Core;
using Polypiler.Core.Verification.Running;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The commands of the Kotlin/Java layer (<c>tooling/ci/layers/jvm.yaml</c>), run through the
/// cross-language runner: <c>javac -Xlint:all -Werror -d out &lt;file&gt;</c> then
/// <c>java -cp out &lt;Main&gt;</c>, and <c>kotlinc &lt;file&gt; -Werror -include-runtime -d out.jar</c>
/// then <c>java -jar out.jar</c>. Warnings are errors for both compilers, no build system runs, and
/// every program runs on the <c>java</c> of the pinned JDK: the one beside its <c>javac</c>, never a
/// JVM that comes with another tool. The Kotlin compiler is started with that JDK as <c>JAVA_HOME</c>.
/// </summary>
public sealed class JvmCommands {

  /// <summary>The key of the JDK in <c>tooling/toolchains.lock</c>; its executable is <c>javac</c>.</summary>
  public const string Jdk = "jdk";

  /// <summary>The key of the Kotlin compiler in <c>tooling/toolchains.lock</c>.</summary>
  public const string Kotlinc = "kotlinc";

  /// <summary>
  /// The toolchain an execute step names: the <c>java</c> launcher of the JDK. It is not a key of the
  /// lock; <see cref="Toolchains"/> finds it beside the JDK's <c>javac</c>.
  /// </summary>
  public const string Java = "java";

  /// <summary>The directory <c>javac</c> writes the classes to.</summary>
  public const string ClassDirectory = "out";

  /// <summary>The runnable jar <c>kotlinc</c> writes, the Kotlin runtime included.</summary>
  public const string Jar = "out.jar";

  /// <summary>Finds <see cref="Java"/> beside the JDK's <c>javac</c> and every other toolchain in the lock.</summary>
  private sealed class JvmResolver(JvmCommands commands, IToolchainResolver inner) : IToolchainResolver {

    public ToolchainResolution Resolve(string toolchain) {
      if (toolchain != Java)
        return inner.Resolve(toolchain);

      var jdk = inner.Resolve(Jdk);
      if (jdk.Executable is null)
        return jdk;

      var java = Path.Combine(commands.JdkHome!, "bin", Java + Path.GetExtension(jdk.Executable));
      return File.Exists(java)
        ? ToolchainResolution.Found(java)
        : ToolchainResolution.Failed(Diagnostic.Create(ToolchainDiagnostic.NotInstalledDescriptor, null, $"toolchain '{Java}' is not installed (probe: {java}): the JDK at {commands.JdkHome} has no java launcher"));
    }

  }

  private readonly IToolchainResolver _lock;
  private readonly Lazy<string?> _jdkHome;

  /// <summary>The commands over the toolchains of a lock.</summary>
  /// <param name="toolchains">Resolves the keys of the lock, <see cref="Jdk"/> and <see cref="Kotlinc"/> among them.</param>
  public JvmCommands(IToolchainResolver toolchains) {
    this._lock = toolchains ?? throw new ArgumentNullException(nameof(toolchains));
    this._jdkHome = new(this.FindJdkHome);
    this.Toolchains = new JvmResolver(this, toolchains);
  }

  /// <summary>The resolver a runner of these commands needs: the lock's, and <see cref="Java"/> beside the JDK's <c>javac</c>.</summary>
  public IToolchainResolver Toolchains { get; }

  /// <summary>
  /// The folder of the pinned JDK: the parent of the folder of its <c>javac</c>, symbolic links
  /// followed; <see langword="null"/> when the JDK cannot be resolved.
  /// </summary>
  public string? JdkHome => this._jdkHome.Value;

  private string? FindJdkHome() {
    if (this._lock.Resolve(Jdk).Executable is not { } javac)
      return null;

    var target = File.Exists(javac) ? File.ResolveLinkTarget(javac, returnFinalTarget: true)?.FullName ?? javac : javac;
    return Path.GetDirectoryName(Path.GetDirectoryName(target));
  }

  /// <summary>Whether a source file is Kotlin (<c>.kt</c>) rather than Java (<c>.java</c>).</summary>
  /// <param name="source">The source's file name.</param>
  /// <exception cref="ArgumentException">The file is neither.</exception>
  public static bool IsKotlin(string source) => Path.GetExtension(source) switch {
    ".kt" => true,
    ".java" => false,
    _ => throw new ArgumentException($"'{source}' is neither a Java (.java) nor a Kotlin (.kt) source", nameof(source))
  };

  /// <summary>
  /// The class whose <c>main</c> a source declares: a Java source's file name; for Kotlin the class
  /// of the file's top-level functions, the file name with its first letter in upper case, every
  /// character that cannot be in an identifier replaced by <c>_</c>, and <c>Kt</c> appended.
  /// </summary>
  /// <param name="source">The source's file name.</param>
  public static string MainClass(string source) {
    var name = Path.GetFileNameWithoutExtension(source);
    if (!IsKotlin(source))
      return name;

    var identifier = new string([.. name.Select(character => char.IsLetterOrDigit(character) ? character : '_')]);
    return char.ToUpperInvariant(identifier[0]) + identifier[1..] + "Kt";
  }

  /// <summary>The build step of a Java source: <c>javac -Xlint:all -Werror -d out &lt;source&gt;</c>.</summary>
  /// <param name="source">The source's file name in the working directory.</param>
  public static ProcessSpec CompileJava(string source) => new(Jdk, "-Xlint:all", "-Werror", "-d", ClassDirectory, source);

  /// <summary>
  /// The build step of a Kotlin source: <c>kotlinc &lt;source&gt; -Werror -include-runtime -d out.jar</c>,
  /// started with the pinned JDK as <c>JAVA_HOME</c>.
  /// </summary>
  /// <param name="source">The source's file name in the working directory.</param>
  public ProcessSpec CompileKotlin(string source) {
    var step = new ProcessSpec(Kotlinc, source, "-Werror", "-include-runtime", "-d", Jar);
    return this.JdkHome is { } home ? step with { Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["JAVA_HOME"] = home } } : step;
  }

  /// <summary>The execute step of compiled classes: <c>java -cp out &lt;main class&gt; &lt;arguments&gt;</c>.</summary>
  /// <param name="mainClass">The class whose <c>main</c> runs.</param>
  /// <param name="arguments">The program's arguments.</param>
  public static ProcessSpec RunClass(string mainClass, params string[] arguments) => new(Java, ["-cp", ClassDirectory, mainClass, .. arguments]);

  /// <summary>The execute step of the runnable jar: <c>java -jar out.jar &lt;arguments&gt;</c>.</summary>
  /// <param name="arguments">The program's arguments.</param>
  public static ProcessSpec RunJar(params string[] arguments) => new(Java, ["-jar", Jar, .. arguments]);

  /// <summary>The build step of a source, by its language.</summary>
  /// <param name="source">The source's file name in the working directory.</param>
  public ProcessSpec Compile(string source) => IsKotlin(source) ? this.CompileKotlin(source) : CompileJava(source);

  /// <summary>The execute step of a compiled source: the jar of a Kotlin source, the classes of a Java one.</summary>
  /// <param name="source">The source's file name.</param>
  /// <param name="arguments">The program's arguments.</param>
  public static ProcessSpec Execute(string source, params string[] arguments) => IsKotlin(source) ? RunJar(arguments) : RunClass(MainClass(source), arguments);

  /// <summary>
  /// The plan that compiles one source and executes the result, under the default limits, as trusted
  /// code; it collects what the compiler wrote.
  /// </summary>
  /// <param name="source">The source's file name, which the working directory holds it under.</param>
  /// <param name="text">The source's bytes.</param>
  /// <param name="execute">The execute step; <see cref="Execute"/> of the source without arguments unless given.</param>
  public RunPlan Plan(string source, ReadOnlyMemory<byte> text, ProcessSpec? execute = null)
    => new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [source] = text }, [this.Compile(source)], execute ?? Execute(source), RunLimits.Default, TrustLevel.Trusted) {
      Collect = [ClassDirectory, Jar]
    };

  /// <summary>Runs a plan of these commands in a sandbox.</summary>
  /// <param name="plan">The plan.</param>
  /// <param name="sandbox">The sandbox of the host.</param>
  public Task<RunResult> RunAsync(RunPlan plan, ISandbox sandbox) => new CrossLanguageRunner(sandbox, this.Toolchains).RunAsync(plan);

  /// <summary>
  /// Compares the standard output of the Java and the Kotlin program of a pair byte for byte, nothing
  /// trimmed or normalised.
  /// </summary>
  /// <param name="java">The Java program's standard output.</param>
  /// <param name="kotlin">The Kotlin program's standard output.</param>
  /// <returns><see langword="null"/> when they are identical, else a message that shows both outputs.</returns>
  public static string? Difference(ReadOnlySpan<byte> java, ReadOnlySpan<byte> kotlin) {
    var comparison = OutputComparison.Compare(java, kotlin, ComparisonMode.Exact);
    return comparison.IsEqual
      ? null
      : $"the Java and the Kotlin output differ ({comparison}):\n--- Java ---\n{Encoding.UTF8.GetString(java)}--- Kotlin ---\n{Encoding.UTF8.GetString(kotlin)}";
  }

}
