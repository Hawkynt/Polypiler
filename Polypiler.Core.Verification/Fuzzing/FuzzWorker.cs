using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>
/// The worker side of supervised fuzzing: it runs inputs a <see cref="WorkerProcessExecutor"/> sends
/// and reports each one as soon as it has finished. The protocol is binary and little-endian:
/// <list type="bullet">
///   <item>the worker announces itself with the byte <see cref="Ready"/> once its target is loaded;</item>
///   <item>a request is an <c>int32</c> length and that many input bytes; a negative length ends the worker;</item>
///   <item>a response is a byte (0 returned, 1 crash, 2 mismatch), the <c>int64</c> bytes allocated, the
///   <c>int64</c> elapsed ticks (100 ns), an <c>int32</c> length and the UTF-8 signature.</item>
/// </list>
/// </summary>
public static class FuzzWorker {

  /// <summary>The byte a worker writes once it is ready for requests.</summary>
  public const byte Ready = (byte)'R';

  /// <summary>Serves requests until the input ends or a negative length arrives.</summary>
  /// <param name="target">The target to run.</param>
  /// <param name="requests">Where requests arrive.</param>
  /// <param name="responses">Where responses go.</param>
  public static void Serve(IFuzzTarget target, Stream requests, Stream responses) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(requests);
    ArgumentNullException.ThrowIfNull(responses);
    responses.WriteByte(Ready);
    responses.Flush();
    var header = new byte[4];
    while (true) {
      if (requests.ReadAtLeast(header, header.Length, false) < header.Length)
        return;

      var length = BinaryPrimitives.ReadInt32LittleEndian(header);
      if (length < 0)
        return;

      var input = new byte[length];
      requests.ReadExactly(input);
      WriteResponse(responses, InProcessExecutor.Measure(target, input));
    }
  }

  /// <summary>Writes one request: the length and the input.</summary>
  internal static void WriteRequest(Stream stream, byte[] input) {
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, input.Length);
    stream.Write(header);
    stream.Write(input);
    stream.Flush();
  }

  /// <summary>Writes the request that ends a worker.</summary>
  internal static void WriteEnd(Stream stream) {
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, -1);
    stream.Write(header);
    stream.Flush();
  }

  private static void WriteResponse(Stream stream, FuzzOutcome outcome) {
    var signature = Encoding.UTF8.GetBytes(outcome.Signature);
    var response = new byte[21 + signature.Length];
    response[0] = outcome.Kind switch {
      FindingKind.Crash => 1,
      FindingKind.Mismatch => 2,
      _ => 0
    };
    BinaryPrimitives.WriteInt64LittleEndian(response.AsSpan(1), outcome.AllocatedBytes);
    BinaryPrimitives.WriteInt64LittleEndian(response.AsSpan(9), outcome.Elapsed.Ticks);
    BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(17), signature.Length);
    signature.CopyTo(response, 21);
    stream.Write(response);
    stream.Flush();
  }

  /// <summary>Reads one response; throws <see cref="EndOfStreamException"/> when the worker is gone.</summary>
  internal static FuzzOutcome ReadResponse(Stream stream) {
    var header = new byte[21];
    stream.ReadExactly(header);
    var signature = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(17))];
    stream.ReadExactly(signature);
    FindingKind? kind = header[0] switch {
      1 => FindingKind.Crash,
      2 => FindingKind.Mismatch,
      _ => null
    };
    return new(kind, Encoding.UTF8.GetString(signature), BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(1)), TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(9))));
  }

}
