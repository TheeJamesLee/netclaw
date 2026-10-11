# Netclaw Compliance Scorer

`evals/run.py` is a small, on-demand behavioral scorer. It asks one question:
do agents follow mechanical direction?

It measures coarse facts only:

- skill load calls;
- accepted child runs;
- selected tool calls;
- missing-profile refusal without profile creation.

It does not check exact final prose or exact orchestration.

## Run

Set the provider and model for one run:

```bash
NETCLAW_EVAL_PROVIDER_TYPE=ollama \
NETCLAW_EVAL_PROVIDER_ENDPOINT=http://my-gpu-server.tailnet.ts.net:11434 \
NETCLAW_EVAL_MODEL_ID=qwen3:30b \
  python3 evals/run.py
```

For a model matrix, run the command once per model:

```bash
for model in qwen3:8b qwen3:30b; do
  NETCLAW_EVAL_MODEL_ID="$model" python3 evals/run.py
done
```

Useful options:

- `--list-cases` lists the case names.
- `--case <name>` runs one case. Use it more than once for a small subset.
- `--runs <n>` overrides `NETCLAW_EVAL_RUNS`.
- `--threshold <n>` overrides `NETCLAW_EVAL_THRESHOLD`.
- `--timeout <seconds>` overrides `NETCLAW_EVAL_TIMEOUT`.
- `--no-build` reuses `NETCLAW_IMAGE` and `NETCLAW_BIN`.

## Environment

Required:

| Variable | Meaning |
| --- | --- |
| `NETCLAW_EVAL_PROVIDER_TYPE` | Provider type, such as `ollama` or `openai-compatible`. |
| `NETCLAW_EVAL_PROVIDER_ENDPOINT` | Provider URL that the container can reach. |
| `NETCLAW_EVAL_MODEL_ID` | Main model ID for the trial. |

Optional:

| Variable | Default | Meaning |
| --- | --- | --- |
| `NETCLAW_EVAL_PROVIDER_API_KEY` | unset | Provider API key. The archive does not write this value. |
| `NETCLAW_EVAL_FALLBACK_MODEL_ID` | main model | Fallback model ID. |
| `NETCLAW_EVAL_COMPACTION_MODEL_ID` | main model | Compaction model ID. |
| `NETCLAW_EVAL_RUNS` | `5` | Runs per case. |
| `NETCLAW_EVAL_THRESHOLD` | `0.80` | Per-case pass threshold. |
| `NETCLAW_EVAL_TIMEOUT` | `120` | Prompt timeout in seconds. |
| `NETCLAW_EVAL_CASE` | all cases | Comma-separated case names. |
| `NETCLAW_IMAGE` | `ghcr.io/netclaw-dev/netclaw:dev` | Daemon image. |
| `NETCLAW_BIN` | `publish/cli/netclaw` | Host CLI path. |
| `NETCLAW_EVAL_NO_BUILD` | `0` | Set to `1` to skip the local build. |
| `NETCLAW_EVAL_PORT` | `5299` | Host port for the eval daemon. |
| `NETCLAW_EVAL_ASSET_ROOT` | current checkout | Checkout that supplies fixtures. |

## Cases

The case list is a Python tuple in `evals/run.py`. Each case has:

- a name;
- one prompt template;
- one coarse assertion.

Current cases:

| Case | Assertion |
| --- | --- |
| `subagent-invocation` | A complex task records at least one accepted child run. |
| `skill-loading` | A mission email task calls `skill_load` for `business-email-review`. |
| `tool-selection` | A shell task calls `shell_execute`. |
| `trivial-negative` | A trivial arithmetic task records no accepted child run. |
| `unavailable-profile-negative` | A missing profile request records no accepted child and no profile file change. |

## Isolation

The runner uses the same isolation model as the old harness:

- It creates a temporary `NETCLAW_HOME`.
- It copies only eval identity, skills, agents, MCP, and config fixtures.
- It starts `netclawd` in Docker with `--network host`.
- It points the host CLI at that daemon with `NETCLAW_DAEMON_ENDPOINT`.
- It archives stdout, stderr, logs, and `results.json` under `evals/runs/<run-id>/`.
- It removes the temporary home on exit unless `--keep-home` is set.

Archives can contain prompts, tool calls, and model output. Do not publish raw
archives without review.

## Verification

The scorer is not a CI gate and does not need a model during normal PR checks.
For syntax verification, run:

```bash
python3 -m py_compile evals/run.py
```
