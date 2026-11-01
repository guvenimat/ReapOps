# ADR 0005: Changes go through Git; the agent has no write access

Status: proposed

## Context

An agent with cluster write access is one prompt injection away from damage. Direct `kubectl` changes are also invisible in history and hard to roll back.

## Decision

The agent's tools are read-only. A separate executor accepts only catalog actions with a valid approval and turns them into pull requests in the GitOps repository, or `CapacityOverride` resources that a controller applies. Argo CD deploys merged changes; code changes deploy as canaries with automatic rollback when the SLO burns.

## Consequences

- Every change is a reviewed commit with an obvious revert.
- Changes take minutes, not seconds; fast paths stay with the autoscaler.
