# ADR 0008: Clean Architecture for ReapOps

Status: accepted (2026-11-01)

## Context

ReapOps is mostly decisions and integrations. Its core (incident state machine, policy engine, action catalog,
approval binding, capacity rules, detection math) must be testable without a database or network, and replayed
against recorded data. It talks to many external systems that differ between installations: Prometheus, Loki,
Tempo, the Kubernetes API, GitHub, Argo CD, the Anthropic API, email and optional business signal sources.

The safety model also depends on code boundaries: the agent must not be able to reach anything that writes.

The sibling project ReapSeek deliberately uses a lighter hybrid (feature folders, a rich domain only where the
rules are), because most of its modules are CRUD. ReapOps has the opposite profile.

## Decision

Four layers, one project each, with dependencies pointing inward:

| Project | Contains | May depend on |
|---|---|---|
| `ReapOps.Domain` | Incident, Proposal, Approval, ActionCatalog, Policy, CapacityLimits, detection math; no packages | nothing |
| `ReapOps.Application` | Use cases (`DetectIncidents`, `DiagnoseIncident`, `ApproveProposal`, `ExecuteAction`, `VerifyAction`) and port interfaces (`ISignalProvider`, `IDiagnosisModel`, `IChangeExecutor`, `IIncidentStore`, `INotifier`, `IClock`) | Domain |
| `ReapOps.Infrastructure` | Adapters: Prometheus, Loki, Tempo, Kubernetes, GitHub, Argo CD, Anthropic, PostgreSQL, SMTP | Application, Domain |
| `ReapOps.Api`, `ReapOps.Worker`, `ReapOps.Controller` | Hosts and composition roots only | all of the above |

Rules:

- Read and write ports are separate interfaces. The diagnosis use case and the agent's tools can only receive read ports;
  `IChangeExecutor` is injected only into `ExecuteAction`.
- Use cases are plain classes called directly; no mediator library (MediatR is not AOT-friendly and adds indirection).
- No generic repository; each aggregate has its own store port.
- Simple reads (lists for the UI, settings) do not get a use case class; they are query services in Application.
- The rules are enforced by architecture tests (NetArchTest), including "nothing in the diagnosis path references a write port".
- The `CapacityOverride` controller is written in .NET (KubeOps) rather than Go, so the hard-cap and expiry rules live in
  one Domain project shared by the policy engine and the controller.

## Consequences

- The core is unit tested with fakes; adapters get integration tests against real services in containers.
- Adding a signal source or an executor is a new adapter and a registration, not a change to use cases.
- More projects and interfaces than ReapSeek; the README explains why the two codebases differ.
- KubeOps is less mature than kubebuilder; if it blocks us, the controller can move to Go while keeping its rules as a small shared spec.
