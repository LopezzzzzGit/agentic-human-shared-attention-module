# Harness contract

ASHA is a **desktop capability**, not an agent runtime. It has no dependency
on Lantern, a model SDK, a planner, or a particular inference server.

The host (your agentic harness) owns:

- the local driver model and its tool-call loop;
- screen understanding, target resolution, policy, and action replay;
- choosing when to ask the human for a demonstration;
- mapping the model provider's native function-call objects to ASHA calls.

ASHA owns only the shared-attention channel: marks, human demonstration
capture, and compilation of a recording into a neutral semantic recipe.

## Model-neutral tool boundary

Run `node bin/asha.mjs manifest` to get the canonical JSON Schema definition
for `asha_mark`, `asha_ack`, `asha_clear`, and `asha_armed`. The host maps that
manifest to its model provider. It can also import
`src/harness-adapter.ts` and pass each normalized call to
`dispatchAshaTool({ name, arguments })`.

```text
local tool-calling model
          │ native tool call
          ▼
host adapter ── normalized ASHA call ──► ASHA mark/teach/compile
          │                                      │
          └──── visible desktop action ◄─────────┘
```

For an OpenAI-compatible local server, the host normally maps `name`,
`description`, and `inputSchema` directly to its function-tool definition.
For a Gemma QAT setup or another provider, only that mapping changes; ASHA's
CLI and recipe format do not.

## Demonstrator scope

The stand-alone demo intentionally does not require the full harness or
Lantern. It proves the end-to-end shared-attention loop in independently
runnable pieces:

1. `asha mark` shows the model's intended target before action.
2. `asha teach` captures a human demonstration with UI Automation context.
3. `asha compile` emits a model-neutral recipe the harness can replay.

That keeps the first video focused and lets the final host integration remain
a thin adapter rather than a second implementation of ASHA.

The stand-alone `asha compile` command uses an offline deterministic fallback
so the demo remains runnable without a cloud key. For the final harness,
replace it with a local structured-output model adapter (possibly the same
Gemma model that calls tools) and validate its result against ASHA's recipe
schema before replay.
