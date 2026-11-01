# ADR 0003: Three-tier scaling with layered hard caps

Status: proposed

## Context

Autoscalers handle normal load. When they hit their ceiling the cause can be real demand, a downstream bottleneck or abuse, and the right response differs in each case. Raising limits automatically would let an attacker drive cost.

## Decision

Tier 1: HPA scales inside its range on its own. Tier 2: HPA at max while an SLO burns opens an incident and the agent diagnoses, checking downstream saturation and traffic quality first. Tier 3: a human approves a temporary ceiling through a `CapacityOverride` with an expiry. Safety comes from layered hard limits (per-service cap, Kyverno admission policy, ResourceQuota, node limits); the controller's TTL and an independent sweeper are conveniences on top.

## Consequences

- No approval can exceed the hard cap.
- An override that is never cleaned up is still bounded.
- A controller and a CRD have to be built and operated.
