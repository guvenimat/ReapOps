# ADR 0001: A separate product that knows nothing about the systems it watches

Status: proposed

## Context

ReapOps could be a module inside ReapSeek. That would make it quick to build but would tie operations to one application, and a failure in the application could take its own repair tool down with it.

## Decision

ReapOps is its own repository, deployment and namespace. It learns about a monitored system only through configuration: services, SLOs, signal providers and the GitOps repository. ReapSeek is the first monitored system and is not referenced in ReapOps code.

## Consequences

- ReapOps can watch any cluster, and stays available when the watched application is down.
- Integration points (signal providers, executors) must be generic interfaces from the start.
