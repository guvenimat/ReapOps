# ReapOps

An agentic operations control plane for Kubernetes. ReapOps detects incidents with SLO burn rates, rules and
statistics, asks an LLM agent to explain them with evidence and propose fixes from a fixed action catalog, and
applies a fix only after a human approves it, through Git pull requests and Argo CD.

> Status: design. Read the [specification](docs/spec.md) and the [decision records](docs/adr).

## License

Apache 2.0, see [LICENSE](LICENSE).
