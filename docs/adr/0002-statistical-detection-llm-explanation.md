# ADR 0002: Deterministic detection, LLM explanation

Status: proposed

## Context

An LLM can look at metrics and logs and say something is wrong, but its answers vary between runs, cost money per call and are hard to test. Detection runs continuously over many series.

## Decision

Incidents are opened only by deterministic logic: multi-window SLO burn rates as the backbone, rules (HPA at max, OOMKilled, CrashLoopBackOff), then statistical anomalies and log templates as a second layer that must correlate with the backbone. The LLM is called once an incident exists, to explain it and propose options.

## Consequences

- Detection is testable with replayed data and cheap to run.
- The LLM budget scales with incidents, not with metrics.
- Anomaly detection needs history; new services rely on SLOs and rules for their first week.
