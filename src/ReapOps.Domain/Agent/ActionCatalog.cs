namespace ReapOps.Domain.Agent;

public enum Risk { None, Low, Medium, High }

public sealed record CatalogAction(string Id, string Description, Risk Risk, string Rollback, IReadOnlyList<string> Parameters);

public static class ActionCatalog
{
    public const string Observe = "observe";

    public static readonly IReadOnlyList<CatalogAction> All =
    [
        new(Observe, "Take no action; keep watching. Use when the incident is expected to clear or the evidence is not conclusive.", Risk.None, "none", []),
        new("raise_max_replicas", "Raise a workload's autoscaling ceiling for a limited time.", Risk.Medium, "expires automatically", ["workload", "max_replicas", "hours"]),
        new("tighten_rate_limit", "Lower a rate limit on specific routes at the ingress.", Risk.Low, "revert the pull request", ["routes", "requests_per_minute"]),
        new("enable_challenge", "Require a challenge on specific routes at the ingress or edge.", Risk.Medium, "revert the pull request", ["routes"]),
        new("block_network", "Block a network range at the ingress for a limited time. Last resort.", Risk.High, "expires automatically", ["cidr", "hours"]),
        new("raise_resources", "Raise a workload's CPU or memory requests and limits.", Risk.Medium, "revert the pull request", ["workload", "cpu", "memory"]),
        new("rollback_deployment", "Pin a workload to its previous image digest.", Risk.Medium, "revert the pull request", ["workload"]),
        new("code_fix", "Open a pull request with a code change and tests.", Risk.High, "revert the pull request, canary rollback", ["summary"]),
    ];

    public static CatalogAction? Find(string id) => All.FirstOrDefault(a => a.Id == id);
}
