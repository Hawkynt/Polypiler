using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Perl;

/// <summary>
/// Maps IR names to idiomatic, collision-free Perl 5 identifiers, CPAN package names, file paths
/// and safely quoted hash keys.
/// </summary>
public static class PerlNaming {

  /// <summary>Perl keywords, built-in operators and special sub names that no emitted sub or lexical may take bare.</summary>
  public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "abs", "accept", "alarm", "and", "atan2", "AUTOLOAD", "BEGIN", "bind", "binmode", "bless", "break",
    "caller", "chdir", "CHECK", "chmod", "chomp", "chop", "chown", "chr", "chroot", "close", "closedir",
    "cmp", "connect", "continue", "cos", "crypt", "dbmclose", "dbmopen", "default", "defined", "delete",
    "DESTROY", "die", "do", "dump", "each", "else", "elsif", "END", "endgrent", "endhostent", "endnetent",
    "endprotoent", "endpwent", "endservent", "eof", "eq", "eval", "exec", "exists", "exit", "exp", "fc",
    "fcntl", "fileno", "flock", "for", "foreach", "fork", "format", "formline", "ge", "getc", "getgrent",
    "getgrgid", "getgrnam", "gethostbyaddr", "gethostbyname", "gethostent", "getlogin", "getnetbyaddr",
    "getnetbyname", "getnetent", "getpeername", "getpgrp", "getppid", "getpriority", "getprotobyname",
    "getprotobynumber", "getprotoent", "getpwent", "getpwnam", "getpwuid", "getservbyname", "getservbyport",
    "getservent", "getsockname", "getsockopt", "given", "glob", "gmtime", "goto", "grep", "gt", "hex",
    "if", "index", "INIT", "int", "ioctl", "join", "keys", "kill", "last", "lc", "lcfirst", "le", "length",
    "link", "listen", "local", "localtime", "lock", "log", "lstat", "lt", "m", "map", "mkdir", "msgctl",
    "msgget", "msgrcv", "msgsnd", "my", "ne", "next", "no", "not", "oct", "open", "opendir", "or", "ord",
    "our", "pack", "package", "pipe", "pop", "pos", "print", "printf", "prototype", "push", "q", "qq",
    "qr", "quotemeta", "qw", "qx", "rand", "read", "readdir", "readline", "readlink", "readpipe", "recv",
    "redo", "ref", "rename", "require", "reset", "return", "reverse", "rewinddir", "rindex", "rmdir", "s",
    "say", "scalar", "seek", "seekdir", "select", "semctl", "semget", "semop", "send", "setgrent",
    "sethostent", "setnetent", "setpgrp", "setpriority", "setprotoent", "setpwent", "setservent",
    "setsockopt", "shift", "shmctl", "shmget", "shmread", "shmwrite", "shutdown", "sin", "sleep", "socket",
    "socketpair", "sort", "splice", "split", "sprintf", "sqrt", "srand", "stat", "state", "study", "sub",
    "substr", "symlink", "syscall", "sysopen", "sysread", "sysseek", "system", "syswrite", "tell",
    "telldir", "tie", "tied", "time", "times", "tr", "truncate", "uc", "ucfirst", "umask", "undef",
    "UNITCHECK", "unless", "unlink", "unpack", "unshift", "untie", "until", "use", "utime", "values",
    "vec", "wait", "waitpid", "wantarray", "warn", "when", "while", "write", "x", "xor", "y",
    "a", "b", "_", "true", "false", "try", "catch", "finally", "import", "unimport"
  };

  /// <summary>Converts an identifier to <c>snake_case</c>.</summary>
  /// <param name="name">The IR identifier.</param>
  /// <returns>The snake_case form, or empty when no ASCII letter or digit is present.</returns>
  public static string Snake(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var text = new StringBuilder();
    for (var index = 0; index < name.Length; ++index) {
      var character = name[index];
      if (char.IsAsciiLetterUpper(character)) {
        var previous = index > 0 ? name[index - 1] : '\0';
        var next = index + 1 < name.Length ? name[index + 1] : '\0';
        if (index > 0 && (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous) || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next))))
          text.Append('_');
        text.Append(char.ToLowerInvariant(character));
      } else if (char.IsAsciiLetterOrDigit(character) || character == '_') {
        text.Append(character);
      } else {
        text.Append('_');
      }
    }

    return text.ToString();
  }

  /// <summary>Ensures a snake_case name is a valid, non-reserved Perl identifier.</summary>
  /// <param name="name">The raw identifier.</param>
  /// <returns>The safe Perl identifier.</returns>
  public static string Usable(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (name.Length > 0 && char.IsAsciiDigit(name[0]))
      name = "n_" + name;

    return Reserved.Contains(name) ? name + "_" : name;
  }

  /// <summary>Maps an IR function name to a Perl sub name (public exported or <c>_</c>-prefixed private).</summary>
  /// <param name="name">The IR function name.</param>
  /// <param name="isPublic">Whether the function is exported.</param>
  /// <returns>The Perl sub name, or empty when nothing of the name remains.</returns>
  public static string Function(string name, bool isPublic) {
    ArgumentNullException.ThrowIfNull(name);
    var cleaned = Snake(name.Replace("<", "_of_", StringComparison.Ordinal).Replace(",", "_and_", StringComparison.Ordinal)).Trim('_');
    if (cleaned.Length == 0)
      return "";

    return isPublic ? Usable(cleaned) : "_" + cleaned;
  }

  /// <summary>Maps an IR parameter or local name to a Perl lexical name (without the leading <c>$</c>). Never returns <c>a</c>, <c>b</c> or <c>_</c>.</summary>
  /// <param name="name">The IR variable name.</param>
  /// <returns>The safe lexical name.</returns>
  public static string Local(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var snake = Snake(name).Trim('_');
    if (snake.Length == 0)
      return "v";

    return Usable(snake);
  }

  /// <summary>Maps an IR constant name to <c>UPPER_SNAKE_CASE</c>.</summary>
  /// <param name="name">The IR constant name.</param>
  /// <returns>The constant identifier.</returns>
  public static string Constant(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var upper = Snake(name).Trim('_').ToUpperInvariant();
    if (upper.Length == 0)
      upper = "CONST";
    if (char.IsAsciiDigit(upper[0]))
      upper = "N_" + upper;
    return Reserved.Contains(upper) ? upper + "_" : upper;
  }

  /// <summary>Maps an IR module name (such as <c>Hawkynt.Checksums.Adler32</c>) to a Perl package name.</summary>
  /// <param name="moduleName">The IR module name.</param>
  /// <returns>The Perl package name (such as <c>Hawkynt::Checksums::Adler32</c>).</returns>
  public static string Package(string moduleName) {
    ArgumentNullException.ThrowIfNull(moduleName);
    var segments = moduleName.Replace("::", ".", StringComparison.Ordinal)
      .Split('.', StringSplitOptions.RemoveEmptyEntries)
      .Select(UpperCamel)
      .Where(segment => segment.Length > 0)
      .ToArray();
    return segments.Length == 0 ? "Module" : string.Join("::", segments);
  }

  /// <summary>The CPAN library path for a Perl package: <c>lib/A/B/C.pm</c>.</summary>
  /// <param name="packageName">The Perl package name.</param>
  /// <returns>The relative path under <c>lib/</c>.</returns>
  public static string PackagePath(string packageName)
    => "lib/" + packageName.Replace("::", "/", StringComparison.Ordinal) + ".pm";

  /// <summary>The CPAN test file path for a Perl package: <c>t/&lt;last_segment&gt;.t</c>.</summary>
  /// <param name="packageName">The Perl package name.</param>
  /// <returns>The relative path under <c>t/</c>.</returns>
  public static string TestPath(string packageName) {
    var last = packageName.Contains("::", StringComparison.Ordinal)
      ? packageName[(packageName.LastIndexOf("::", StringComparison.Ordinal) + 2)..]
      : packageName;
    var stem = Snake(last).Trim('_');
    return "t/" + (stem.Length > 0 ? stem : "module") + ".t";
  }

  /// <summary>Formats a hash key for <c>{ key =&gt; ... }</c> or <c>$h-&gt;{key}</c>, quoting keywords and non-identifiers.</summary>
  /// <param name="name">The field name.</param>
  /// <returns>A bareword or single-quoted Perl string.</returns>
  public static string HashKey(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var key = Snake(name).Trim('_');
    if (key.Length == 0)
      key = name;
    var bare = key.Length > 0 && (char.IsAsciiLetter(key[0]) || key[0] == '_')
      && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
      && !Reserved.Contains(key);
    return bare ? key : "'" + key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
  }

  /// <summary>Formats an ASCII-only single-quoted or double-quoted Perl string literal for arbitrary UTF-16 code units.</summary>
  /// <param name="value">The string text.</param>
  /// <returns>A Perl string literal containing only printable ASCII characters.</returns>
  public static string StringLiteral(string value) {
    ArgumentNullException.ThrowIfNull(value);
    var text = new StringBuilder("\"");
    foreach (var ch in value) {
      switch (ch) {
        case '\\': text.Append("\\\\"); break;
        case '"': text.Append("\\\""); break;
        case '$': text.Append("\\$"); break;
        case '@': text.Append("\\@"); break;
        case '\n': text.Append("\\n"); break;
        case '\r': text.Append("\\r"); break;
        case '\t': text.Append("\\t"); break;
        case '\0': text.Append("\\x{00}"); break;
        default:
          if (ch >= 0x20 && ch <= 0x7E)
            text.Append(ch);
          else
            text.Append($"\\x{{{(int)ch:x2}}}");
          break;
      }
    }

    return text.Append('"').ToString();
  }

  /// <summary>Returns <paramref name="name"/> or <c>name_2</c>, <c>name_3</c>, ... until <paramref name="taken"/> is false.</summary>
  /// <param name="name">The desired name.</param>
  /// <param name="taken">Whether a candidate is already used.</param>
  /// <returns>A free identifier.</returns>
  public static string Free(string name, Func<string, bool> taken) {
    ArgumentNullException.ThrowIfNull(taken);
    if (!taken(name))
      return name;

    for (var suffix = 2; ; ++suffix) {
      var candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
      if (!taken(candidate))
        return candidate;
    }
  }

  private static string UpperCamel(string segment) {
    var parts = segment.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
    var text = new StringBuilder();
    foreach (var part in parts) {
      var clean = new string(part.Where(char.IsAsciiLetterOrDigit).ToArray());
      if (clean.Length == 0)
        continue;
      if (char.IsAsciiDigit(clean[0]) && text.Length == 0)
        text.Append('M');
      text.Append(char.ToUpperInvariant(clean[0]));
      if (clean.Length > 1)
        text.Append(clean[1..]);
    }

    return text.ToString();
  }

}
