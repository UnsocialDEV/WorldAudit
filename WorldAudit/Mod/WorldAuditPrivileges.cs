namespace WorldAudit.Mod;

public static class WorldAuditPrivileges
{
    public const string Inspect = "worldaudit.inspect";
    public const string Lookup = "worldaudit.lookup";
    public const string LookupBlock = "worldaudit.lookup.block";
    public const string LookupContainer = "worldaudit.lookup.container";
    public const string Rollback = "worldaudit.rollback";
    public const string Restore = "worldaudit.restore";
    public const string Purge = "worldaudit.purge";
    public const string Reload = "worldaudit.reload";
    public const string Status = "worldaudit.status";
    public const string Consumer = "worldaudit.consumer";
    public const string Admin = "worldaudit.admin";

    public static readonly string[] All =
    [
        Inspect,
        Lookup,
        LookupBlock,
        LookupContainer,
        Rollback,
        Restore,
        Purge,
        Reload,
        Status,
        Consumer,
        Admin
    ];
}
