namespace ReapDeep.Migrations;

public static class StarRocksSettings
{
    public const int QueuePendingTimeoutSeconds = 15;

    public static readonly string[] Statements =
    [
        "SET GLOBAL enable_query_queue_select = true",
        "SET GLOBAL enable_group_level_query_queue = true",
        $"SET GLOBAL query_queue_pending_timeout_second = {QueuePendingTimeoutSeconds}",
    ];
}
