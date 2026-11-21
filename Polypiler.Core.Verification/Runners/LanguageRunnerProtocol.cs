using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Runners;

/// <summary>Encodes inputs and reads outputs using the shared language-runner protocol.</summary>
public static class LanguageRunnerProtocol {

  private static readonly UTF8Encoding _utf8 = new(false, true);

  /// <summary>Writes one lower-case hexadecimal line per input image.</summary>
  /// <exception cref="ImageException">An input image does not fit the signature.</exception>
  public static byte[] Write(CallSignature signature, IReadOnlyList<byte[]> inputImages) {
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentNullException.ThrowIfNull(inputImages);
    var text = new StringBuilder();
    foreach (var image in inputImages) {
      ArgumentNullException.ThrowIfNull(image);
      _ = CallImages.Decode(signature, CallImage.Input, image);
      text.Append(Convert.ToHexStringLower(image)).Append('\n');
    }

    return _utf8.GetBytes(text.ToString());
  }

  /// <summary>Reads one output image or trap per call, preserving their order.</summary>
  /// <exception cref="ImageException">The driver output does not follow the protocol or the signature.</exception>
  public static IReadOnlyList<CallOutcome> Read(ReadOnlySpan<byte> stdout, CallSignature signature, int callCount) {
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentOutOfRangeException.ThrowIfNegative(callCount);

    string text;
    try {
      text = _utf8.GetString(stdout);
    } catch (DecoderFallbackException) {
      throw new ImageException("the driver output is not UTF-8");
    }

    using var reader = new System.IO.StringReader(text);
    var outcomes = new List<CallOutcome>();
    while (reader.ReadLine() is { } line) {
      if (outcomes.Count == callCount)
        throw new ImageException($"the driver printed more than {callCount} line(s)");

      try {
        outcomes.Add(ReadLine(line, signature));
      } catch (ImageException exception) {
        throw new ImageException($"line {outcomes.Count + 1}: {exception.Message}");
      }
    }

    if (outcomes.Count != callCount)
      throw new ImageException($"the driver printed {outcomes.Count} line(s) for {callCount} {(callCount == 1 ? "call" : "calls")}");

    return outcomes;
  }

  private static CallOutcome ReadLine(string line, CallSignature signature) {
    if (line.StartsWith('!')) {
      var space = line.IndexOf(' ', StringComparison.Ordinal);
      if (space < 0 || string.IsNullOrWhiteSpace(line[(space + 1)..]))
        throw new ImageException("a trap line requires a class and a native name");

      return new(null, TrapClass.Parse(line[1..space])) { NativeTrapName = line[(space + 1)..] };
    }

    if (!line.All(char.IsAsciiHexDigitLower))
      throw new ImageException("not an image in lower-case hexadecimal");
    if (line.Length % 2 != 0)
      throw new ImageException("odd number of hex digits");

    var image = Convert.FromHexString(line);
    _ = CallImages.Decode(signature, CallImage.Output, image);
    return new(image, null);
  }

}
