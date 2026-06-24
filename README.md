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

## Building

> ⚠️ This skeleton was authored in an environment without the .NET SDK or nuget.org
> access, so it has **not been compiled here**. On a machine with the .NET 8 SDK:

```bash
dotnet restore
dotnet build
dotnet test
```

Package versions referenced: Hangfire 1.8.x, xUnit 2.9.x, Microsoft.Extensions.Options 8.0.x.
