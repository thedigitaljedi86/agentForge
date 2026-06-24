# DevAgent

DevAgent is a secure platform for running internal AI agents for software-development
workflows (scheduled, event-based and — eventually — interactive). The first concrete
agent, **DependencyPilot**, detects new NuGet package versions, finds affected
repositories and opens pull requests that update dependencies safely.

> First milestone: a deterministic, policy-controlled skeleton. **No LLM is wired in
> yet**, by design. The interfaces are shaped so an LLM coding agent can be added later
> *only* through structured, policy-checked tools.

## Solution layout

```
src/
  DevAgent.Contracts/        Shared DTOs, enums, validation contracts (no deps)
  DevAgent.Audit/            Append-only audit log (decisions, jobs, prompts, diffs)
  DevAgent.Guard/            SECURITY CORE: allowlists, path/command validation, SafeCommandRunner
  DevAgent.Bridge.Git/       IGitProvider abstraction + placeholder (GitHub/GitLab/ADO later)
  DevAgent.Bridge.NuGet/     Package + usage scanning abstractions
  DevAgent.Forge/            FUTURE LLM layer: structured tool contracts only (no shell)
  DevAgent.Worker.DotNet/    Console app that runs INSIDE the sandbox container
  DevAgent.Runner.Api/       Final validation gate; ISandboxJobRunner + DockerSandboxJobRunner stub
  DevAgent.Hub.Api/          Front door: webhooks, Hangfire schedule, manual triggers
  Agents.DependencyPilot/    The first concrete agent (composes the platform)

tests/
  DevAgent.Guard.Tests/             allowlists, path traversal, protected files, SafeCommandRunner
  DevAgent.Runner.Tests/            validation gate + "no user-supplied infrastructure"
  DevAgent.Worker.DotNet.Tests/     env-var failure, deterministic update, no auto-merge
  DevAgent.Forge.Tests/             structured tool surface has no shell/exec escape hatch
  Agents.DependencyPilot.Tests/     detection + watch-list enforcement
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

* The LLM never gets host shell, Docker socket, SSH, secrets, deploy or cloud creds.
* Callers supply **allowlist keys**, never raw URLs, images or Docker arguments.
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
* **Agent-specific:** `Agents.DependencyPilot` only — it composes platform capabilities and
  contains no generic platform code.
* **The LLM coding agent (`DevAgent.Forge`):** a controlled agent that can fix build/test
  errors *inside the sandbox worker*. It acts ONLY through the seven structured tools in
  `ToolCalls.cs` (`list_files`, `read_file`, `apply_patch`, `replace_file`,
  `run_dotnet_build`, `run_dotnet_test`, `git_status`) — there is no shell, bash, curl,
  ssh, docker or generic command tool. Every call is path-validated, protected-file-checked,
  tool-allowlisted and audited; the loop is iteration-capped; the final diff and the model's
  reasoning summary are saved. Wire it into the worker by passing an `ICodingAgent`
  (built via `CodingAgentFactory.Create`) as an opt-in build-repair step; the worker still
  pushes a branch and opens a review-required PR — the agent never merges.
* **Must stay deterministic & policy-controlled:** the Runner validation gate, all `Guard`
  policies, `SafeCommandRunner`, and the worker's NuGet update path.

## Building

> ⚠️ This skeleton was authored in an environment without the .NET SDK or nuget.org
> access, so it has **not been compiled here**. On a machine with the .NET 8 SDK:

```bash
dotnet restore
dotnet build
dotnet test
```

Package versions referenced: Hangfire 1.8.x, xUnit 2.9.x, Microsoft.Extensions.Options 8.0.x.
