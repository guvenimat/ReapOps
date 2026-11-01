# ADR 0007: Kill switch, audit and noise control

Status: accepted (2026-11-01)

## Context

An operations tool that misbehaves can cause the incident it was meant to fix, or train people to ignore it.

## Decision

A single kill switch stops every executor and controller change while detection and notifications continue. Every state change, approval and execution is written to an append-only audit log. Notifications are deduplicated per incident fingerprint, suppressed during maintenance windows and known events, and capped per hour.

## Consequences

- The operator can always stop automation without stopping visibility.
- Every change can be traced to a detection, a diagnosis and a person.
