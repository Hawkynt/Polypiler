using System;
using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>How a builtin takes its arguments, which decides how much of what follows it is its operand.</summary>
internal enum PerlBuiltinShape {
  /// <summary>No arguments: <c>time</c>, <c>wantarray</c>.</summary>
  Term,
  /// <summary>A named unary operator: one optional operand, which binds tighter than comparisons.</summary>
  Unary,
  /// <summary>A list operator: everything to its right up to a low-precedence operator or a closing bracket.</summary>
  List,
  /// <summary>Syntax of its own, parsed by name.</summary>
  Special
}

/// <summary>
/// The builtins of perl and their prototypes, as <c>prototype("CORE::name")</c> of perl 5.42 gives them
/// (copied, not derived: they are the agreed values); <see langword="null"/> is a builtin with syntax of
/// its own. A prototype with at most one argument slot makes a named unary operator, an empty one a
/// term, any other a list operator (perlop, "Named Unary Operators").
/// </summary>
internal static class PerlBuiltins {

  private static readonly Dictionary<string, string?> _prototypes = new(StringComparer.Ordinal) {
    ["__FILE__"] = "", ["__LINE__"] = "", ["__PACKAGE__"] = "", ["__CLASS__"] = "",
    ["__DATA__"] = null, ["__END__"] = null, ["__SUB__"] = "", ["ADJUST"] = null,
    ["AUTOLOAD"] = null, ["BEGIN"] = null, ["UNITCHECK"] = null, ["DESTROY"] = null,
    ["END"] = null, ["INIT"] = null, ["CHECK"] = null, ["abs"] = "_",
    ["accept"] = "**", ["alarm"] = "_", ["all"] = "\\&@", ["and"] = null,
    ["any"] = "\\&@", ["atan2"] = "$$", ["bind"] = "*$", ["binmode"] = "*;$",
    ["bless"] = "$;$", ["break"] = "", ["caller"] = ";$", ["catch"] = "",
    ["chdir"] = ";$", ["chmod"] = "@", ["chomp"] = null, ["chop"] = null,
    ["chown"] = "@", ["chr"] = "_", ["chroot"] = "_", ["class"] = null,
    ["close"] = ";*", ["closedir"] = "*", ["cmp"] = null, ["connect"] = "*$",
    ["continue"] = "", ["cos"] = "_", ["crypt"] = "$$", ["dbmclose"] = "\\%",
    ["dbmopen"] = "\\%$$", ["default"] = null, ["defer"] = null, ["defined"] = null,
    ["delete"] = null, ["die"] = "@", ["do"] = null, ["dump"] = "",
    ["each"] = "\\[%@]", ["else"] = null, ["elsif"] = null, ["endgrent"] = "",
    ["endhostent"] = "", ["endnetent"] = "", ["endprotoent"] = "", ["endpwent"] = "",
    ["endservent"] = "", ["eof"] = ";*", ["eq"] = null, ["eval"] = null,
    ["evalbytes"] = "_", ["exec"] = null, ["exists"] = null, ["exit"] = ";$",
    ["exp"] = "_", ["fc"] = "_", ["fcntl"] = "*$$", ["field"] = null,
    ["fileno"] = "*", ["finally"] = null, ["flock"] = "*$", ["for"] = null,
    ["foreach"] = null, ["fork"] = "", ["format"] = null, ["formline"] = "$@",
    ["ge"] = null, ["getc"] = ";*", ["getgrent"] = "", ["getgrgid"] = "$",
    ["getgrnam"] = "$", ["gethostbyaddr"] = "$$", ["gethostbyname"] = "$", ["gethostent"] = "",
    ["getlogin"] = "", ["getnetbyaddr"] = "$$", ["getnetbyname"] = "$", ["getnetent"] = "",
    ["getpeername"] = "*", ["getpgrp"] = ";$", ["getppid"] = "", ["getpriority"] = "$$",
    ["getprotobyname"] = "$", ["getprotobynumber"] = "$;", ["getprotoent"] = "", ["getpwent"] = "",
    ["getpwnam"] = "$", ["getpwuid"] = "$", ["getservbyname"] = "$$", ["getservbyport"] = "$$",
    ["getservent"] = "", ["getsockname"] = "*", ["getsockopt"] = "*$$", ["given"] = null,
    ["glob"] = "_;", ["gmtime"] = ";$", ["goto"] = null, ["grep"] = null,
    ["gt"] = null, ["hex"] = "_", ["if"] = null, ["index"] = "$$;$",
    ["int"] = "_", ["ioctl"] = "*$$", ["isa"] = "", ["join"] = "$@",
    ["keys"] = "\\[%@]", ["kill"] = "@", ["last"] = null, ["lc"] = "_",
    ["lcfirst"] = "_", ["le"] = null, ["length"] = "_", ["link"] = "$$",
    ["listen"] = "*$", ["local"] = null, ["localtime"] = ";$", ["lock"] = "\\[$@%&*]",
    ["log"] = "_", ["lstat"] = ";*", ["lt"] = null, ["m"] = null,
    ["map"] = null, ["method"] = "", ["mkdir"] = "_;$", ["msgctl"] = "$$$",
    ["msgget"] = "$$", ["msgrcv"] = "$$$$$", ["msgsnd"] = "$$$", ["my"] = null,
    ["ne"] = null, ["next"] = null, ["no"] = null, ["not"] = "$;",
    ["oct"] = "_", ["open"] = "*;$@", ["opendir"] = "*$", ["or"] = null,
    ["ord"] = "_", ["our"] = null, ["pack"] = "$@", ["package"] = null,
    ["pipe"] = "**", ["pop"] = ";\\@", ["pos"] = ";\\[$*]", ["print"] = null,
    ["printf"] = null, ["prototype"] = "_", ["push"] = "\\@@", ["q"] = null,
    ["qq"] = null, ["qr"] = null, ["quotemeta"] = "_", ["qw"] = null,
    ["qx"] = null, ["rand"] = ";$", ["read"] = "*\\$$;$", ["readdir"] = "*",
    ["readline"] = ";*", ["readlink"] = "_", ["readpipe"] = "_", ["recv"] = "*\\$$$",
    ["redo"] = null, ["ref"] = "_", ["rename"] = "$$", ["require"] = null,
    ["reset"] = ";$", ["return"] = null, ["reverse"] = "@", ["rewinddir"] = "*",
    ["rindex"] = "$$;$", ["rmdir"] = "_", ["s"] = null, ["say"] = null,
    ["scalar"] = "$", ["seek"] = "*$$", ["seekdir"] = "*$", ["select"] = null,
    ["semctl"] = "$$$$", ["semget"] = "$$$", ["semop"] = "$$", ["send"] = "*$$;$",
    ["setgrent"] = "", ["sethostent"] = "$", ["setnetent"] = "$", ["setpgrp"] = ";$$",
    ["setpriority"] = "$$$", ["setprotoent"] = "$", ["setpwent"] = "", ["setservent"] = "$",
    ["setsockopt"] = "*$$$", ["shift"] = ";\\@", ["shmctl"] = "$$$", ["shmget"] = "$$$",
    ["shmread"] = "$$$$", ["shmwrite"] = "$$$$", ["shutdown"] = "*$", ["sin"] = "_",
    ["sleep"] = ";$", ["socket"] = "*$$$", ["socketpair"] = "**$$$", ["sort"] = null,
    ["splice"] = "\\@;$$@", ["split"] = null, ["sprintf"] = "$@", ["sqrt"] = "_",
    ["srand"] = ";$", ["stat"] = ";*", ["state"] = null, ["study"] = "_",
    ["sub"] = null, ["substr"] = "$$;$$", ["symlink"] = "$$", ["syscall"] = "$@",
    ["sysopen"] = "*$$;$", ["sysread"] = "*\\$$;$", ["sysseek"] = "*$$", ["system"] = null,
    ["syswrite"] = "*$;$$", ["tell"] = ";*", ["telldir"] = "*", ["tie"] = "\\[$@%*]$@",
    ["tied"] = "\\[$@%*]", ["time"] = "", ["times"] = "", ["tr"] = null,
    ["try"] = null, ["truncate"] = "$$", ["uc"] = "_", ["ucfirst"] = "_",
    ["umask"] = ";$", ["undef"] = ";\\[$@%&*]", ["unless"] = null, ["unlink"] = "@",
    ["unpack"] = "$_", ["unshift"] = "\\@@", ["untie"] = "\\[$@%*]", ["until"] = null,
    ["use"] = null, ["utime"] = "@", ["values"] = "\\[%@]", ["vec"] = "$$$",
    ["wait"] = "", ["waitpid"] = "$$", ["wantarray"] = "", ["warn"] = "@",
    ["when"] = null, ["while"] = null, ["write"] = ";*", ["x"] = null,
    ["xor"] = null, ["y"] = null,
  };

  /// <summary>The keywords a sub of the same name cannot override (<c>+</c> in perl's <c>regen/keywords.pl</c>).</summary>
  private static readonly HashSet<string> _strong = new(StringComparer.Ordinal) {
    "__DATA__", "__END__", "ADJUST", "AUTOLOAD", "BEGIN", "UNITCHECK", "DESTROY", "END", "INIT", "CHECK",
    "catch", "default", "defer", "defined", "delete", "do", "else", "elsif", "eval", "exists",
    "finally", "for", "foreach", "format", "given", "glob", "goto", "grep", "if", "last",
    "local", "m", "map", "my", "next", "no", "our", "package", "pos", "print",
    "printf", "prototype", "q", "qq", "qr", "qw", "qx", "redo", "require", "return",
    "s", "say", "scalar", "sort", "split", "state", "study", "sub", "tr", "try",
    "undef", "unless", "until", "use", "when", "while", "y",
  };

  /// <summary>Whether <paramref name="name"/> is a builtin of perl.</summary>
  public static bool IsBuiltin(string name) => _prototypes.ContainsKey(name);

  /// <summary>Whether a sub named <paramref name="name"/> cannot override the builtin.</summary>
  public static bool IsStrong(string name) => _strong.Contains(name);

  /// <summary>The prototype of a builtin; <see langword="null"/> for one with syntax of its own or for no builtin.</summary>
  public static string? Prototype(string name) => _prototypes.GetValueOrDefault(name);

  /// <summary>How the builtin <paramref name="name"/> takes its arguments.</summary>
  public static PerlBuiltinShape Shape(string name) => _prototypes.TryGetValue(name, out var prototype) ? ShapeOf(prototype) : PerlBuiltinShape.List;

  /// <summary>The shape a prototype gives: empty is a term, one slot a named unary operator, more a list operator.</summary>
  public static PerlBuiltinShape ShapeOf(string? prototype) {
    if (prototype is null)
      return PerlBuiltinShape.Special;
    if (prototype.Length == 0)
      return PerlBuiltinShape.Term;
    return Slots(prototype) <= 1 ? PerlBuiltinShape.Unary : PerlBuiltinShape.List;
  }

  /// <summary>The number of argument slots of a prototype: <c>\[$@%]</c> is one; <c>@</c> and <c>%</c> count as many.</summary>
  public static int Slots(string prototype) {
    var slots = 0;
    for (var i = 0; i < prototype.Length; ++i) {
      var c = prototype[i];
      switch (c) {
        case ';' or ' ':
          continue;
        case '@' or '%':
          return int.MaxValue;
        case '\\':
          if (i + 1 < prototype.Length && prototype[i + 1] == '[') {
            var close = prototype.IndexOf(']', i);
            i = close < 0 ? prototype.Length : close;
          } else {
            ++i;
          }
          ++slots;
          continue;
        default:
          ++slots;
          continue;
      }
    }
    return slots;
  }

}
