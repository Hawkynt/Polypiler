using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The signature of a function value: the types of its parameters, in call order, and the type of its
/// result, which is <see cref="IrType.Void"/> for a function that returns nothing. Two signatures are
/// equal when their parameters and results are: function types are structural, not nominal, so a C#
/// <c>Func&lt;int, int&gt;</c> and a delegate type <c>delegate int Op(int a)</c> of the source are one type.
/// </summary>
public sealed class IrFunctionType : IEquatable<IrFunctionType> {

  /// <summary>A signature.</summary>
  /// <param name="parameters">The types of the parameters, in call order.</param>
  /// <param name="result">The type of the result, or <see cref="IrType.Void"/>.</param>
  public IrFunctionType(IReadOnlyList<IrType> parameters, IrType result) {
    this.Parameters = [.. parameters];
    this.Result = result;
  }

  /// <summary>The types of the parameters, in call order.</summary>
  public IReadOnlyList<IrType> Parameters { get; }

  /// <summary>The type of the result, or <see cref="IrType.Void"/> for none.</summary>
  public IrType Result { get; }

  /// <inheritdoc/>
  public bool Equals(IrFunctionType? other)
    => other is not null && this.Result == other.Result && this.Parameters.SequenceEqual(other.Parameters);

  /// <inheritdoc/>
  public override bool Equals(object? obj) => this.Equals(obj as IrFunctionType);

  /// <inheritdoc/>
  public override int GetHashCode() => this.Parameters.Aggregate(this.Result.GetHashCode(), HashCode.Combine);

  /// <summary>The signature as the IR writes it, for example <c>fn(i32, i32) -&gt; i32</c>.</summary>
  public override string ToString() => $"fn({string.Join(", ", this.Parameters)}) -> {this.Result}";

}
