namespace Polypiler.Core.BinaryFormats.Elf;

internal static class ElfDiagnostics {

  [Diagnostic]
  internal static readonly DiagnosticDescriptor Incomplete = new("PLY4060", DiagnosticSeverity.Error, ExitCode.EnvironmentError,
    "'{0}' is not a complete ELF file: {1}", "machine.elf.incomplete");

  [Diagnostic]
  internal static readonly DiagnosticDescriptor Unsupported = new("PLY4061", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "ELF {0} '{1}' is not supported", "machine.elf.unsupported");

  [Diagnostic]
  internal static readonly DiagnosticDescriptor Corrupt = new("PLY4062", DiagnosticSeverity.Error, ExitCode.EnvironmentError,
    "ELF structure is corrupt: {0}", "machine.elf.corrupt");

  [Diagnostic]
  internal static readonly DiagnosticDescriptor UnsupportedRelocation = new("PLY4063", DiagnosticSeverity.Warning, null,
    "relocation type {0} at {1} is not supported; code that depends on it cannot be translated standalone", "machine.elf.relocation");

}
