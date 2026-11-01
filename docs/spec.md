# ReapOps specification

ReapOps is an agentic operations control plane for Kubernetes. It watches a cluster and its services, detects
problems with deterministic rules and statistics, asks an LLM agent to explain them and propose fixes, and applies
a fix only after a human approves it, through Git and the cluster's GitOps tooling.

Status: draft for review. Decisions are recorded in [docs/adr](adr).

## 1. Goals and non-goals

**Goals**

- Detect incidents early: SLO burn, saturation, anomalies in metrics and logs, failing workloads.
- Explain each incident with evidence (metrics, logs, traces, recent deploys) and a ranked list of options.
- Act only through a fixed catalog of actions, each with a risk level, a rollback and an approval rule.
- Never act without a human approval, except inside limits the operator configured in advance (for example the HPA range).
- Work with any Kubernetes cluster and any Git repository. The first monitored system is ReapSeek, which ReapOps does not know about.

**Non-goals**

- Autonomous remediation without approval.
- Replacing Kubernetes autoscaling. HPA, KEDA and the cluster autoscaler keep doing their job; ReapOps handles what they cannot decide.
- DDoS mitigation in the application. Traffic controls are applied at the ingress or edge layer.
- Being a metrics or log store. ReapOps reads Prometheus, Loki and Tempo; it does not replace them.

## 2. Actors

| Actor | Role |
|---|---|
| Operator | Configures services, SLOs, limits and the approval matrix; approves or rejects proposals. |
| Approver | A person allowed to approve a given risk level (by default the operator). |
| Agent | LLM with read-only tools. Explains incidents and drafts proposals. Cannot change anything. |
| Policy engine | Deterministic code that filters and classifies proposals against the action catalog. |
| Executor | The only component that changes state, and only by opening pull requests or creating `CapacityOverride` resources. |

## 3. Architecture

```
 Prometheus  Loki  Tempo  Kubernetes events  Git/CI  optional business signals (e.g. ReapDeep)
      \        |      |          |              |              /
       +-------+------+----------+--------------+-------------+
                                 |
                          Collector (signal providers)
                                 |
                          Detector (SLO burn, rules, statistics, log templates)
                                 |
                          Incident store (PostgreSQL) -- state machine, dedupe
                                 |
                          Agent (Claude, read-only tools) --> Proposal
                                 |
                          Policy engine --> allowed options, risk, approval rule
                                 |
                          Approval API + UI + email/push
                                 |
                          Executor --> Git pull request --> CI --> merge --> Argo CD
                                   --> CapacityOverride CRD --> reapops-controller --> HPA
                                 |
                          Verifier (SLO after the change) --> close or roll back
```

Deployables: `reapops-api` (approval API and UI backend), `reapops-worker` (collector, detector, agent runs, verifier),
`reapops-controller` (Kubernetes controller for `CapacityOverride`), `reapops-migrate` (database migrations, run as a Job),
and a web UI. Everything runs in the `reapops` namespace.

## 4. Signals

Signal providers implement one interface (`query(range) -> series | events`) and are configured per monitored service.

| Provider | Used for |
|---|---|
| Prometheus | SLIs, saturation (CPU, memory, connection pools, queue depth), HPA state |
| Loki | error logs, log template frequencies |
| Tempo | slow or failing traces as evidence |
| Kubernetes | pod restarts, OOMKilled, CrashLoopBackOff, failed scheduling, HPA at max |
| Git / CI | recent deploys and their commits, the "what changed" question |
| Business (optional) | conversion and revenue signals, for example from ReapDeep's Query API |

Business signals are evidence, never a requirement: the agent must reach a conclusion without them, and they may lag by a minute or more.

## 5. Detection

Detection is deterministic. The LLM never decides whether an incident exists.

1. **SLO burn rate (backbone).** Each service declares SLOs (availability, latency). Incidents open on multi-window burn rates,
   for example 14.4x over 5 min and 1 h (page) or 6x over 30 min and 6 h (ticket).
2. **Rules.** HPA at `maxReplicas` while an SLO burns; OOMKilled; CrashLoopBackOff; Pending pods that cannot be scheduled; outbox or queue lag.
3. **Statistical anomalies (second layer).** Per-metric seasonal baselines (same hour of previous weeks), MAD-based scores and change-point
   detection for slow drifts such as memory leaks. Cold start: no anomaly alerts until a metric has at least 7 days of history.
4. **Log templates (second layer).** Logs are clustered into templates (Drain). A new error template, or a template whose rate jumps,
   is a candidate; it becomes an incident only when it correlates with an SLO burn, error traces or a recent deploy.

Noise controls: one open incident per fingerprint (service + signal + template), maintenance windows, an allowlist of known events
(campaigns, load tests), a suppression period after a deploy for warm-up, and a cap on notifications per hour.

## 6. Incident lifecycle

```
detected -> diagnosing -> proposed -> approved -> executing -> verifying -> resolved
                 |            |           |            |            |
                 v            v           v            v            v
             escalated     rejected    expired      failed     rolled_back
```

- Every transition is a row in `incident_events` (who, when, why). Transitions are idempotent: the same event applied twice changes nothing.
- An incident that resolves by itself (SLO recovers) before approval moves to `resolved` and its proposal expires.
- `escalated`: the agent could not reach a confident diagnosis; the operator gets the evidence without a proposal.

## 7. Agent

- Model: Claude through the Anthropic API, pinned per release; the model id is stored with every diagnosis.
- Tools are read-only and allowlisted: `query_metrics`, `search_logs`, `get_trace`, `describe_workload`, `recent_deploys`, `read_file`, `diff_commits`, `business_signals`.
- **Untrusted input.** Logs, traces and code are data, never instructions. Tool output is wrapped and labelled; the system prompt says so,
  and the policy engine ignores anything the agent proposes outside the catalog.
- **Redaction.** Secrets, tokens, emails and phone numbers are masked before they reach the model.
- **Structured output.** A diagnosis is JSON: summary, root cause, confidence, evidence (each item links to a query, log line or trace),
  alternatives considered and why they were rejected, and options that reference catalog actions.
- **Budget.** Per-incident token and tool-call limits, a monthly spend cap; when the cap is hit incidents are escalated without a diagnosis.
- **Evaluation.** A fixed set of recorded incidents with expected diagnoses; every prompt or model change runs against it. New capabilities start in shadow mode (diagnose and record, notify nobody) until their accuracy is acceptable.

### Demand or attack

When a service is at its maximum replicas and still burning its SLO, the agent must first answer two questions:

1. **Is the bottleneck downstream?** Database connection pool, Redis, search, external APIs. If so, adding replicas makes it worse, and the proposal says so.
2. **Is the traffic real?** Real demand converts: sessions that pay, revenue per session, checkout completion. Signals of abuse: concentration in a few networks or ASNs,
   low user agent diversity, a narrow route mix (login, search), rising 4xx/401/403, a jump in new sessions, no matching campaign or deploy.

## 8. Action catalog

The agent can only reference these actions. The policy engine assigns risk, approval rule and rollback.

| Action | Mechanism | Risk | Rollback |
|---|---|---|---|
| Raise max replicas temporarily | `CapacityOverride` (TTL) | medium | automatic on expiry, or manual revert |
| Tighten a rate limit | PR to ingress config | low | revert PR |
| Enable a challenge on routes | PR to ingress/edge config | medium | revert PR |
| Block a network range (TTL) | PR to ingress config with expiry | high | automatic on expiry |
| Raise resource requests/limits | PR to manifests | medium | revert PR |
| Roll back a deployment | PR pinning the previous image digest | medium | revert PR |
| Code fix | PR with code change and tests | high | revert PR, canary rollback |

Never proposed: restarting databases, rotating or reading secrets, changing node pool limits, deleting data, disabling security controls.

## 9. Capacity limits

Three tiers:

1. **Automatic:** HPA scales within its configured range (for example 4 to 8). ReapOps does nothing.
2. **At the ceiling:** HPA at max and an SLO burning opens an incident; the agent diagnoses.
3. **Approval:** the operator chooses an option, for example "raise the ceiling to 12 for 2 hours".

Hard limits are layered; the TTL is a convenience, not the safety guarantee:

| Layer | Guarantee |
|---|---|
| Hard cap per service (for example 16) | No override can exceed it. |
| Admission policy (Kyverno) | Rejects any HPA with `maxReplicas` above the hard cap, and any override without an expiry. |
| Namespace ResourceQuota | Bounds total CPU and memory regardless of replica counts. |
| Cluster autoscaler node limits | Bounds total cost; the policy engine checks there is room before proposing more replicas. |
| `CapacityOverride` controller | Applies the override and restores the original value at `expiresAt`. |
| Independent sweeper (CronJob) | Removes expired overrides the controller missed and raises an alert if it cannot. |

## 10. Approvals

- Default: one approval. The approval matrix is configurable by risk level; two approvals can be required for high-risk actions such as code deploys.
- Notifications by email and in-app push contain a link to an approval page, never an approve link: email scanners open links. Approving is a POST from an authenticated session.
- An approval binds to the exact proposal: incident id, action, parameters, commit SHA and image digest. A new commit or a changed parameter invalidates it.
- Approvals expire (default 30 minutes for capacity actions, 24 hours for code fixes). No answer means no action.
- Every approval, rejection and expiry is audited with the person, time, reason and proposal hash.

## 11. Execution and verification

- The executor has no `kubectl` write access to workloads. It opens pull requests in the GitOps repository, or creates `CapacityOverride` resources that only the controller acts on.
- Code fixes are a separate flow: the agent drafts a change on a branch in an isolated workspace, CI runs tests, lint and static analysis, a pull request opens, and only an approval merges it. Argo CD deploys it as a canary.
- Verification: after an action the verifier watches the SLO for a fixed window. If the burn continues or a new error appears, it proposes a rollback (automatic for canaries).
- **Kill switch:** one setting stops every executor action and controller change; detection and notifications keep running.

## 12. Data model (PostgreSQL)

`services`, `slos`, `signal_providers`, `incidents`, `incident_events`, `diagnoses` (model, prompt version, cost, JSON),
`proposals` (action, parameters, hash), `approvals`, `executions` (PR url, override name, status), `maintenance_windows`,
`known_events`, `audit_log`. Writes that trigger work go through a transactional outbox, as in ReapSeek and ReapDeep.

## 13. Threat model (summary)

| Threat | Mitigation |
|---|---|
| Prompt injection through logs or code | Untrusted-data labelling, action catalog, policy engine, no write tools for the agent |
| Stolen approval link | No approve links; session-based POST, short expiry, binding to proposal hash |
| Cost attack (force scaling) | Hard caps, ResourceQuota, node limits, conversion-based abuse detection |
| Compromised agent output | Agent cannot act; executor only accepts catalog actions with valid approvals |
| Data leak to the model provider | Redaction, minimal context, no secrets in tool access |
| ReapOps outage | Clusters keep running with their own HPA and caps; overrides expire through the sweeper |

## 14. Testing and rollout

- Unit tests for detection math, the state machine, the policy engine and approval binding.
- Replay tests: recorded metric and log windows fed into the detector.
- Agent evaluation set and shadow mode.
- Chaos scenarios against ReapSeek on a local cluster: load spike (real), bot flood (fake traffic without conversions), database pool exhaustion, memory leak, bad deploy with a new exception.
- Success metrics: detection time, false positives per week, diagnosis accuracy on the eval set, time from approval to recovery.

## 15. Milestones

1. **Foundation:** ReapSeek and ReapDeep on Kubernetes (k3d locally) with Helm charts, HPA, ResourceQuota, Argo CD, the observability stack.
2. **Detect and notify:** collector, detector, incident store, notifications and the approval UI, with no actions.
3. **Diagnose:** the read-only agent in shadow mode, then visible proposals, with the evaluation set.
4. **Capacity actions:** `CapacityOverride` CRD and controller, Kyverno policies, ingress rate limit and challenge PRs, verification.
5. **Code fixes:** branch, tests, pull request, canary, rollback.
