namespace ProsimCompanion.Core.Checklists;

/// <summary>
/// Condition-tree evaluation with the exact Prosim2FO semantics: everything is a double
/// (booleans read 1/0), equals/notEquals/oneOf compare with epsilon 1e-6, between is inclusive
/// with open-ended defaults, compound logic defaults to AND, and a malformed leaf is FALSE
/// (fail-closed) rather than an exception.
/// </summary>
public static class ConditionEvaluator
{
    private const double Epsilon = 1e-6;

    public static bool Evaluate(VerifyCondition condition, Func<string, double> read)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(read);

        if (condition.IsCompound)
        {
            var logic = condition.Logic ?? ConditionLogic.And;
            var results = condition.Conditions!.Select(child => Evaluate(child, read)).ToList();
            return logic == ConditionLogic.Or ? results.Any(result => result) : results.All(result => result);
        }

        if (string.IsNullOrWhiteSpace(condition.Dataref) || condition.Op is null)
        {
            return false;
        }

        var actual = read(condition.Dataref);
        return condition.Op switch
        {
            ComparisonOp.Equals => Math.Abs(actual - (condition.Value ?? 0)) < Epsilon,
            ComparisonOp.NotEquals => Math.Abs(actual - (condition.Value ?? 0)) >= Epsilon,
            ComparisonOp.GreaterThan => actual > (condition.Value ?? 0),
            ComparisonOp.LessThan => actual < (condition.Value ?? 0),
            ComparisonOp.Between => actual >= (condition.Low ?? double.MinValue)
                && actual <= (condition.High ?? double.MaxValue),
            ComparisonOp.OneOf => condition.Values?.Any(value => Math.Abs(actual - value) < Epsilon) ?? false,
            _ => false,
        };
    }
}
