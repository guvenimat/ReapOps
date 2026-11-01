# ADR 0006: Business signals are optional evidence

Status: accepted (2026-11-01)

## Context

Telling real demand from abuse is much easier with business data: real customers pay. ReapDeep provides that for ReapSeek, but other systems have no such source, and analytics can lag.

## Decision

Signals come from providers behind one interface. Infrastructure providers (Prometheus, Loki, Tempo, Kubernetes, Git) are required; business providers (for example ReapDeep's Query API) are optional and only add evidence. The agent must reach a conclusion without them.

## Consequences

- ReapOps works for any service; ReapDeep makes its diagnoses better where it exists.
- The diagnosis states which providers it used.
