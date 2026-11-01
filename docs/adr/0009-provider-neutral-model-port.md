# ADR 0009: A provider-neutral model port, measured by the evaluation set

Status: accepted (2026-11-01)

## Context

The specification named Claude as the diagnosis model. The first deployment of ReapOps has no Anthropic API key; a Gemini key
with a free tier is available. More generally, the model is the part of ReapOps most likely to change: prices, quotas and quality
move every few months, and some operators will require a specific provider or a self-hosted model.

## Decision

`IDiagnosisModel` in the Application layer is provider-neutral: it receives the system prompt, a transcript of neutral turns
(brief, tool calls, tool results) and the tool and action specifications, and returns tool calls, a structured `DiagnosisDraft`,
or a refusal. The Application layer has no JSON; adapters translate.

The first adapter targets the OpenAI-compatible chat completions API with function tools, which Gemini, OpenAI, Groq, OpenRouter
and local servers expose. The final diagnosis is a `submit_diagnosis` function whose schema lists the action catalog.
Provider-specific data that must be echoed back (Gemini's thought signatures) travels as an opaque string on the tool call.
An Anthropic adapter on the official SDK can be added without touching the agent loop.

Which model to use is decided by the evaluation set, not by preference: every model and prompt version is scored on the same
replayed cases before it may leave shadow mode.

## Consequences

- Switching provider is configuration: base URL, key and model name.
- Free tiers have small daily quotas (20 requests a day on one Gemini model at the time of writing) and may use the data for
  training; ReapOps only sends redacted operational data, and the demo data is synthetic.
- First measurement (prompt 2026-11-01.2, gemini-3.5-flash-lite): 1 of 4 cases passed. It proposed raising replicas during a bot
  flood, the most expensive mistake the set guards against. That is the reason new models start in shadow mode.
