namespace Polypiler.Core.Optimize.Cost;

/// <summary>
/// One of several forms a pass has proven equivalent, with what it costs: <c>native</c>, <c>sequence</c>,
/// <c>helper</c>, <c>loop</c>, <c>unrolled</c>, <c>scalar</c>, <c>swar-64</c>, <c>simd-128</c>, <c>unpacked</c>,
/// <c>packed</c>, <c>eager</c>, <c>lazy</c>, <c>cpu</c>, <c>threads-4</c>, <c>gpu</c>, <c>npu</c>.
/// </summary>
/// <param name="Form">The name of the form.</param>
/// <param name="Cost">What it costs.</param>
/// <param name="Note">Where the cost came from when that is worth saying, such as <c>default cost</c>; carried into the decision's reason.</param>
public sealed record Alternative(string Form, Cost Cost, string? Note = null);

/// <summary>
/// What the cost model chose: the index of the alternative in the caller's list, its form and score, why
/// it won, and the <c>cost:</c> detail an opt-report entry carries.
/// </summary>
/// <param name="Index">The index of the chosen alternative.</param>
/// <param name="Form">Its form.</param>
/// <param name="Score">Its score under the objective.</param>
/// <param name="Reason">Why it won: the lowest score, a tie broken by the Balanced weights, or a tie broken by the order of the list.</param>
/// <param name="Detail">The opt-report detail, <c>cost: &lt;chosen&gt; &lt;score&gt; vs &lt;rejected&gt; &lt;score&gt;, …</c>.</param>
public readonly record struct Decision(int Index, string Form, long Score, string Reason, string Detail);
