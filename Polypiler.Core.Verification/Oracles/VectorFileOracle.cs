using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>The <c>published-vectors</c> oracle: the expected values of a vector file.</summary>
/// <param name="file">The vector file.</param>
public sealed class VectorFileOracle(VectorFile file) : IVerificationOracle {

  private readonly VectorFile _file = file ?? throw new ArgumentNullException(nameof(file));

  /// <summary>The vector file's name.</summary>
  public string Id => System.IO.Path.GetFileName(this._file.Path);

  /// <summary>The kind its provenance names.</summary>
  public OracleKind Kind => this._file.Provenance.OracleKind;

  /// <summary>What its provenance says wrote the values.</summary>
  public string Producer => this._file.Provenance.GeneratedBy;

  /// <summary>The vector file.</summary>
  public string? ReferenceFile => this._file.Path;

  /// <summary>The expected value of every requested vector; a vector without one makes the oracle fail.</summary>
  /// <param name="request">The fixture and its vectors.</param>
  /// <param name="cancellationToken">Unused: nothing runs.</param>
  public Task<OracleResult> EvaluateAsync(OracleRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    var outputs = new List<byte[]>();
    foreach (var vector in request.Vectors) {
      if (vector.Expected is not { } expected)
        return Task.FromResult(OracleResult.Failed(this.Id, request.FixtureId, $"vector '{vector.Name}' has no published value"));

      outputs.Add(expected);
    }

    return Task.FromResult(new OracleResult(outputs, []));
  }

}
