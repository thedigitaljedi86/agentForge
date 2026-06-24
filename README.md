# DevAgent

DevAgent is a secure platform for running internal AI agents for software-development
workflows (scheduled, event-based and — eventually — interactive). The first concrete
agent, **DependencyPilot**, detects new NuGet package versions, finds affected
repositories and opens pull requests that update dependencies safely.

> A deterministic, policy-controlled skeleton. The platform now ships **LLM clients for
> Claude (default), ChatGPT and Gemini**, and each agent can **pin its own provider and
> model**. The model can only ever act through structured, policy-checked tools — there
> is still no shell or generic command tool anywhere.

> 📖 **Using the system / how to run it:** open [`docs/index.html`](docs/index.html) — a
> single-page, copy-ready guide (services, ports, API examples, security model).

## Solution layout

```
src/
  DevAgent.Contracts/        Shared DTOs, enums, validation contracts (no deps)
  DevAgent.Audit/            Append-only audit log (decisions, jobs, prompts, diffs)
  DevAgent.Guard/            SECURITY CORE: allowlists, path/command validation, SafeCommandRunner
  DevAgent.Bridge.Git/       IGitProvider abstraction + placeholder (GitHub/GitLab/ADO later)
  DevAgent.Bridge.NuGet/     Package + usage scanning abstractions
  DevAgent.Forge/            LLM coding-agent layer: structured tool contracts + loop (no shell)
  DevAgent.Bridge.Llm/       ILlmClient implementations: Claude / ChatGPT / Gemini (+ factory)
  DevAgent.Worker.DotNet/    Sandbox console app: RepoWorkflow (clone->edit->build/test/repair->PR)
  DevAgent.Runner.Api/       Final validation gate; ISandboxJobRunner + PodmanSandboxJobRunner stub
  DevAgent.Hub.Api/          Front door: webhooks, Hangfire schedule, manual triggers
  Agents.DependencyPilot/    Concrete agent: proposes NuGet PackageReference updates
  Agents.DotNetUpgrader/     Concrete agent: proposes upgrading all projects' target framework

tests/
  DevAgent.Guard.Tests/             allowlists, path traversal, protected files, SafeCommandRunner
  DevAgent.Runner.Tests/            validation gate + "no user-supplied infrastructure"
  DevAgent.Worker.DotNet.Tests/     env-var failure, deterministic update/upgrade, build repair, no auto-merge
  DevAgent.Forge.Tests/             structured tool surface has no shell/exec escape hatch
  DevAgent.Bridge.Llm.Tests/        provider wire format, tool-schema round-trip, model-per-agent
  Agents.DependencyPilot.Tests/     detection + watch-list enforcement
  Agents.DotNetUpgrader.Tests/      upgrade planning + watch-list enforcement
```

## What each project is for

| Project | Purpose |
|---|---|
| **DevAgent.Contracts** | Shared, dependency-free DTOs/enums/validation contracts: job requests (keys only — no URLs/images), `AgentJobType`, `AgentJobResult`, `SandboxJobRequest`. The platform's vocabulary. |
| **DevAgent.Audit** | Append-only audit sink (`IAuditLog`): records decisions, jobs, prompts, tool calls and diffs as immutable evidence of what the platform did. |
| **DevAgent.Guard** | The **security core**. Allowlist policies (repository, package, container image, job type, **target framework**), `SafeCommandRunner` (argument-vector exec of `dotnet`/`git` only — no shell), `WorkspacePathValidator`, `ProtectedFilePolicy`. |
| **DevAgent.Bridge.Git** | `IGitProvider` abstraction for clone / push / open-PR, with a placeholder impl that refuses auto-merge. A real GitHub/GitLab/ADO provider drops in later. |
| **DevAgent.Bridge.NuGet** | Abstractions for finding new package versions and scanning which repos use them (feeds DependencyPilot). |
| **DevAgent.Forge** | The **controlled LLM coding agent**: the agent loop plus the seven structured tool contracts (`read_file`, `apply_patch`, `run_dotnet_build`, …). No shell, no generic command tool. |
| **DevAgent.Bridge.Llm** | Concrete `ILlmClient` implementations for **Claude (default), ChatGPT and Gemini**, plus the factory. Maps the structured tools to each provider's tool-calling API; model is selectable per agent. |
| **DevAgent.Worker.DotNet** | The sandbox console app. `RepoWorkflow` does clone → deterministic edit → build/test → **opt-in LLM build-repair** → push → review-required PR. Hosts `PackageReferenceUpdater` and `TargetFrameworkUpdater`. Dispatches on `DEVAGENT_JOB_TYPE`. |
| **DevAgent.Runner.Api** | The **final validation gate**. Re-validates every job against all allowlists, resolves keys → trusted values, and dispatches to the (rootless Podman) sandbox runner. The only place jobs are authorised. Swagger at `/swagger`. |
| **DevAgent.Hub.Api** | The **front door**: manual triggers, the Hangfire schedule, the **agent-status dashboard** (`/`) + `GET /jobs`, and Swagger. Forwards validated jobs to the Runner; never does container work itself. |
| **Agents.DependencyPilot** | Concrete agent: proposes NuGet `PackageReference` updates for watched repos. |
| **Agents.DotNetUpgrader** | Concrete agent: proposes upgrading **all projects' target framework** (e.g. → `net10.0`) for watched repos. Wired as the example **scheduled** agent. |

Every `Agents.*` project only *proposes* work by key; the Runner re-validates and a sandbox worker performs it. The result is always a reviewable pull request.

## Request flow (DependencyPilot NuGet update)

```
Hub.Api  ──(keys + version only)──►  Runner.Api  ──(validated, resolved)──►  Sandbox worker
   │                                     │                                        │
manual trigger / Hangfire        allowlist gate:                         clone → branch →
                                 repo? package? image? jobtype?          update PackageReference →
                                 resolves KEY → trusted URL/image        restore/build/test →
                                                                          push → open PR (never merge)
```

## Security model (enforced, not aspirational)

* The LLM never gets host shell, the Podman/Docker socket, SSH, secrets, deploy or cloud creds.
* Callers supply **allowlist keys**, never raw URLs, images or container arguments.
* The Runner re-validates **every** job against repo / package / image / job-type allowlists.
* `SafeCommandRunner` runs only allowlisted executables (`dotnet`, `git`) as an argument
  vector — **no shell**, so `;`, `&&`, `|`, `$()` are inert. Sub-commands are constrained too.
* `WorkspacePathValidator` blocks path traversal and absolute-path escapes.
* `ProtectedFilePolicy` blocks edits to secrets and deployment descriptors.
* The result is **always a pull request** — there is no auto-merge code path anywhere.
* All decisions, jobs (and later prompts / tool calls / diffs) are audited.

## What belongs where

* **Platform infrastructure:** Contracts, Audit, Guard, Bridge.Git, Bridge.NuGet, Forge,
  Worker.DotNet, Runner.Api, Hub.Api.
* **Agent-specific:** `Agents.DependencyPilot` (NuGet updates) and `Agents.DotNetUpgrader`
  (target-framework upgrades). Each composes platform capabilities, proposes work by key only,
  and contains no generic platform code.
* **The LLM coding agent (`DevAgent.Forge`):** a controlled agent that can fix build/test
  errors *inside the sandbox worker*. It acts ONLY through the seven structured tools in
  `ToolCalls.cs` (`list_files`, `read_file`, `apply_patch`, `replace_file`,
  `run_dotnet_build`, `run_dotnet_test`, `git_status`) — there is no shell, bash, curl,
  ssh, docker or generic command tool. Every call is path-validated, protected-file-checked,
  tool-allowlisted and audited; the loop is iteration-capped; the final diff and the model's
  reasoning summary are saved. It is **wired into the worker** (`RepoWorkflow`) as an opt-in
  build-repair step: when the post-edit build fails and `DEVAGENT_LLM_PROVIDER` is set, the
  agent gets ONE bounded attempt, then the worker re-verifies deterministically and still
  opens a review-required PR — the agent never pushes and never merges.
* **The sandbox worker (`Worker.DotNet`)** dispatches on `DEVAGENT_JOB_TYPE`
  (`NuGetUpdate` | `DotNetUpgrade`); both share `RepoWorkflow`, differing only in the
  deterministic edit (`PackageReferenceUpdater` vs `TargetFrameworkUpdater`).
* **Must stay deterministic & policy-controlled:** the Runner validation gate, all `Guard`
  policies, `SafeCommandRunner`, and the worker's NuGet-update and framework-upgrade edits.

## Running it

All projects target **.NET 10**.

### Option A — Docker (whole platform at once)

```bash
docker compose up --build
```

Then open:

| URL | What |
|---|---|
| <http://localhost:5080/> | **Agent-status dashboard** — which agents received tasks + their status (auto-refreshing) |
| <http://localhost:5080/swagger> | Hub API — manual triggers (`/hub/dependencypilot/nuget-update`, `/hub/dotnetupgrader/upgrade`) |
| <http://localhost:5080/hangfire> | Schedule — recurring agents (e.g. the nightly `dotnetupgrader-nightly-sweep`) |
| <http://localhost:5081/swagger> | Runner API — the validation gate |

The Hub forwards validated jobs to the Runner over the compose network.

### Option B — local (.NET 10 SDK)

```bash
dotnet build DevAgent.sln
dotnet test  DevAgent.sln          # all tests should pass

# Run the two services (Runner first — the Hub forwards to it):
dotnet run --project src/DevAgent.Runner.Api --launch-profile Runner   # :5081
dotnet run --project src/DevAgent.Hub.Api    --launch-profile Hub      # :5080
```

### Scheduled agents

The Hub registers two recurring Hangfire jobs as examples of unattended,
time-based agent work: `dependencypilot-package-check` (hourly heartbeat) and
`dotnetupgrader-nightly-sweep` (nightly — proposes upgrading every watched repo
to the configured framework). The nightly sweep also runs **once at startup** so
the dashboard shows agent activity immediately.

### Build-repair with a real LLM

Set a provider + key on the worker to enable the opt-in Forge build-repair step:
`DEVAGENT_LLM_PROVIDER=Claude` (or `OpenAi` / `Gemini`) plus the matching
`ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GEMINI_API_KEY`. Without it, a failing
build after an edit fails safely with no PR.
