using MySqlConnector;

namespace ReapDeep.QueryApi.Infrastructure;

public enum StarRocksFailure { ResourceLimit, Unavailable, QueryError }

public static class StarRocksErrors
{
    public const int QueryTimeout = 5024;
    public const int GenericError = 1064;
    const string ResourceClass = "53";

    public static StarRocksFailure Classify(int number, string? sqlState, string message) => number switch
    {
        QueryTimeout => StarRocksFailure.ResourceLimit,
        _ when sqlState is not null && sqlState.StartsWith(ResourceClass, StringComparison.Ordinal) => StarRocksFailure.ResourceLimit,
        GenericError when message.Contains("exceed limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("exceed the limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("resource group", StringComparison.OrdinalIgnoreCase) => StarRocksFailure.ResourceLimit,
        GenericError when message.Contains("Backend node not found", StringComparison.OrdinalIgnoreCase) => StarRocksFailure.Unavailable,
        (int)MySqlErrorCode.UnableToConnectToHost or 0 => StarRocksFailure.Unavailable,
        _ => StarRocksFailure.QueryError,
    };

    public static StarRocksFailure Classify(MySqlException e) => Classify(e.Number, e.SqlState, e.Message);
}
