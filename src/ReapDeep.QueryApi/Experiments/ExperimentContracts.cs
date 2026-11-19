using ReapDeep.Analytics.Experiments;
using ReapDeep.Analytics.Flags;

namespace ReapDeep.QueryApi.Experiments;

public sealed record ExperimentBody(
    string? Name, string? Description, long? FlagId, string? ControlVariant, ExperimentMetric? Metric, string? StartDate, string? EndDate);

public sealed record Experiment(
    long Id, string Name, string Description, long FlagId, string FlagKey, bool FlagEnabled, List<FlagVariant> Variants, string ControlVariant,
    string Status, ExperimentMetric Metric, DateTime? StartDate, DateTime? EndDate, string? CreatedBy, string? UpdatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record ExperimentsResponse(List<Experiment> Experiments);

public sealed record ExperimentResultsKey(long Id, string FlagKey, List<FlagVariant> Variants, string ControlVariant, ExperimentMetric Metric, DateTime From, DateTime? To);

public sealed record ExperimentComparison(
    double Difference, double? DifferenceLow, double? DifferenceHigh, double? RelativeLift, double? RelativeLow, double? RelativeHigh,
    double? Statistic, double? PValue, double? DegreesOfFreedom, bool Significant);

public sealed record ExperimentVariantResult(
    string Key, int Percentage, bool IsControl, long Persons, long Conversions, double Mean, double? StandardDeviation, ExperimentComparison? Comparison);

public sealed record ExperimentSrm(double ChiSquare, double PValue, bool Warning, double Threshold);

public sealed record ExperimentResults(
    long ExperimentId, string MetricKind, string Test, string ControlVariant, DateTime DateFrom, DateTime DateTo,
    List<ExperimentVariantResult> Variants, long ExcludedPersons, ExperimentSrm? Srm, double TookMs);

public sealed record SampleSizeRequest(double? BaselineRate, double? RelativeLift, double? Alpha, double? Power, int? Variants);
