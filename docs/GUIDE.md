# The DevAgent Guide

*How the platform works, explained in plain language. Ten minutes, no prior context needed.*

---

## What is DevAgent?

DevAgent is a **self-hosted platform for running AI agents that do software-development chores** — safely. Five agents ship today, plus two observers:

- **DependencyPilot** — watches your NuGet packages: when a new version is published, it updates the dependency in every affected repository, builds and tests the change in an isolated container, and opens a pull request for a human to review.
- **.NET Upgrader** — a scheduled (nightly) sweep that upgrades every project in watched repositories to a new target framework (e.g. `net10.0`), builds, tests, and opens a PR.
- **PipelineDoctor** — watches for failed CI pipelines on **GitHub Actions, GitLab CI and Azure DevOps** (one connection per repository, managed in the admin console), reproduces the failure in a sandbox, lets the caged coding agent repair it, and opens a PR only when the build is green again.
- **DocScribe** — generates a deterministic code map (`docs/CODEMAP.md`) and maintains `docs/` + `README.md` on a **weekly schedule**, so documentation keeps up with the code. Its in-sandbox agent is *write-scoped to docs/* by policy — it structurally cannot modify code.
- **CodeReviewer** — reviews newly-opened pull requests with a **read-only** agent; its only output is a review comment. It never pushes and never merges.
- **SplunkSentinel & ConfluenceGuide** (observer tier) — scheduled Splunk searches recorded as audited findings, and a docs→Confluence sync plan; actual page publishing is an explicit operator action, never a scheduled one.

The platform's core idea is simple:

> **Automation gets a narrow, allowlisted lane. Everything it does is logged. The output is always a pull request — never a merge.**

That last sentence is not a promise, it's architecture. There is no code path in the platform that merges anything, and 350 tests fail if someone tries to add the dangerous parts back.

---

## The 60-second mental model

Think of it as four stations on a one-way conveyor belt, with a security guard and a camera crew:

```
  trigger            validate              execute               review
┌──────────┐      ┌─────────────┐      ┌──────────────┐      ┌──────────────┐
│   HUB    │ ───► │   RUNNER    │ ───► │   SANDBOX    │ ───► │ PULL REQUEST │
│ schedule │      │  the gate:  │      │  worker in a │      │ human review │
│ webhooks │      │  allowlist  │      │  throwaway   │      │  mandatory   │
│dashboard │      │   checks    │      │  container   │      │              │
└──────────┘      └─────────────┘      └──────────────┘      └──────────────┘
      └────────────────┴─────────────────────┴────────────────────┘
                        AUDIT LOG (every decision, every diff)
```

| Component | Job in one sentence |
|---|---|
| **DevAgent.Hub** | The front door: schedules agents, receives webhooks, offers manual triggers, and serves the live agent-status dashboard. |
| **DevAgent.Runner** | The security gate: re-validates every job against allowlists, then launches a sandbox. |
| **DevAgent.Worker** | The hands: runs *inside* the container; clones, edits, builds, tests, pushes, opens the PR. |
| **DevAgent.Guard** | The rulebook: allowlists, path validation, the safe command runner. Used by everyone. |
| **DevAgent.Forge** | The caged AI: a coding agent with exactly seven tools, for fixing broken builds. Opt-in. |
| **DevAgent.Bridge.Llm** | Real LLM clients — **Claude (default), ChatGPT and Gemini** — behind one interface; each agent can pin its own model. |
| **DevAgent.Bridge.Mcp** | MCP client (tools **and prompts**) + the grant policy. Agents reach MCP servers only via the Runner's gateway. |
| **DevAgent.Store** | The SQLite configuration store the **admin console** edits: allowlists, agent settings, MCP servers, skills, webhooks. |
| **DevAgent.Audit** | The camera crew: append-only log of decisions, prompts, tool calls and diffs. |
| **Agents.DependencyPilot** | Decides *which package updates* should happen and asks the platform to do them. |
| **Agents.DotNetUpgrader** | Decides *which framework upgrades* should happen — the example of a scheduled agent. |
| **Agents.PipelineDoctor** | Watches failing pipelines on **GitHub Actions, GitLab CI and Azure DevOps** and proposes a sandboxed repair per new failure. |
| **Agents.DocScribe** | Maintains repository documentation on a weekly schedule; its in-sandbox agent is **write-scoped to docs/** by policy. |
| **Agents.CodeReviewer** | First-pass PR review by a **read-only** agent; the review comment is its only output. |
| **Agents.SplunkSentinel / .ConfluenceGuide** | Observer tier: scheduled Splunk searches recorded as audited findings, and a docs→Confluence sync plan (publishing is an explicit operator action). |
| **DevAgent.Bridge.Git / .NuGet / .Ci** | Adapters for Git hosts, NuGet feeds and CI systems, so no provider is hardcoded. |
| **DevAgent.Contracts** | The shared types everything else speaks. |

---

## A job's life, step by step

Here is exactly what happens when Serilog 3.1.1 is published:

1. **Trigger.** One of three things starts the job:
   - the hourly Hangfire check in the Hub notices the new version on the feed,
   - your feed calls the webhook `POST /hub/webhooks/nuget-package-published`,
   - or a human calls the manual endpoint `POST /hub/dependencypilot/nuget-update` (Swagger at `/swagger` makes this comfortable).

2. **DependencyPilot decides.** The agent checks its watch lists: is Serilog a package we watch? Which watched repositories use it, and are they behind? For each affected repository it proposes one job — identified by a **repository key** like `sample-service`, never by a URL.

3. **The Hub records and forwards.** The job appears on the dashboard (`/status/`) as `Pending`, gets an audit entry, and is sent to the Runner.

4. **The Runner validates — the gate.** Job type allowlisted? Repository key on the allowlist? Package on the allowlist? (For framework upgrades: target framework allowlisted?) Container image — chosen *by the Runner from policy*, never by the caller — allowlisted? **Any failure → the job is rejected, the rejection is logged, and no container ever starts.** Only now are keys resolved into a real clone URL and image name.

5. **The sandbox runs.** The Runner starts a throwaway container (rootless Podman by default; Docker also supported): `--rm`, all Linux capabilities dropped, no privilege escalation, CPU/memory/process limits, **no volume mounts, no container socket**. The worker inside receives its instructions purely as environment variables — and fails safely if any are missing.

6. **The worker does the deterministic work.** Clone → create branch → edit the `<PackageReference>` version (or the `<TargetFramework>`, for upgrade jobs) — plain XML editing, no AI → `dotnet restore` → `build` → `test`.

7. **If the build breaks — the AI may help (opt-in).** Only if the operator configured an LLM provider/model for the job, the Forge coding agent wakes up inside the same sandbox. It can read files, apply patches and re-run build/test — through seven structured tools, nothing else. Whatever it claims, **the worker re-runs restore/build/test itself before believing it.**

8. **Push and PR.** The branch is pushed with a limited bot token and a pull request is opened. If the AI touched anything, the PR says so prominently. If the build is still broken, there is **no PR** — the job fails safely.

9. **A human reviews and merges.** Branch protection stays on. DevAgent has no merge button.

Every step above wrote audit events correlated by job id, and the dashboard at `/status/` (or `GET /jobs`) shows each job's latest status.

---

## The security model in plain words

Six invariants, each enforced by code (not by configuration hoping):

| # | Rule | How it's enforced |
|---|---|---|
| 1 | **The AI never gets a shell.** | Tool calls are a *closed set of C# types*: `list_files`, `read_file`, `apply_patch`, `replace_file`, `run_dotnet_build`, `run_dotnet_test`, `git_status`. There is no type for "run a command", so it cannot be called. A second allowlist (`ToolPolicy`) explicitly bans `bash`, `curl`, `docker`, `ssh`, `kubectl`, `az`, `aws`, `exec`, … |
| 2 | **Everything is allowlisted.** | Repositories, packages, container images, job types, target frameworks and shell commands resolve from admin-edited allowlists **by key**. The request DTOs simply have no field for a URL, an image or a container argument — tests assert those properties don't exist. |
| 3 | **PR only, never a merge.** | No auto-merge code path exists; the Git provider abstraction throws if `AutoMerge = true` is ever requested. |
| 4 | **Throwaway sandboxes.** | The container argument vector is fixed in code: `--rm`, `--cap-drop=ALL`, `no-new-privileges`, pid/memory/CPU limits, no mounts, no socket. Because it's an argument *vector* (never a shell string), a malicious value can't grow new flags. Podman runs daemonless and rootless, so there's no privileged daemon socket to steal. |
| 5 | **Everything is evidence.** | Decisions, prompts, tool calls and diffs are append-only `AuditEvent`s. The agent's final diff and reasoning summary are stored with the job. |
| 6 | **Deterministic first, AI second.** | The update/upgrade is plain XML editing plus `dotnet` commands. The AI runs only on failure, only when an operator configured a model, and its work is deterministically re-verified. |

Also worth knowing:

- **Commands** are limited to `dotnet` and `git` — and even their *sub*-commands are allowlisted (`git daemon` or `dotnet exec` are rejected). Nothing runs through a shell, so `;`, `&&`, `|`, `$()` are inert text.
- **Paths** are validated against the workspace root: no absolute paths, no `../` escapes, no sibling-prefix tricks.
- **Secret files** (`.env`, `secrets.json`, `*.pem`, `id_rsa`, …) can never be read *or* written by the agent. **Deployment files** (Dockerfiles, Terraform, CI workflows, k8s manifests) are locked unless policy explicitly allows them.
- **The worker's token** is a limited bot/service account: push branches and open PRs. No deploy rights, no cloud credentials.
- **Which LLM runs is operator configuration** (per agent, or per sandbox launch) — an API caller can never pick the model or switch the AI on.

---

## Running it locally

Prerequisites: Docker or Podman (with compose), or just the .NET 10 SDK to run the services directly.

**1. Start the platform**

```bash
DEVAGENT_ADMIN_PASSWORD='choose-one' docker compose up --build
# http://localhost:5080/          → landing page
# http://localhost:5080/status/   → live agent-status dashboard
# http://localhost:5080/admin/    → ADMIN CONSOLE (allowlists, agents, MCP, skills, webhooks, audit)
# http://localhost:5080/swagger   → Hub API (manual triggers)
# http://localhost:5080/hangfire  → schedule (admin login required)
# http://localhost:5081/swagger   → Runner API (the validation gate)
```

The Runner starts in **Stub** sandbox mode: jobs pass the full validation gate but no containers are launched — perfect for trying the flow on a laptop. (A nightly .NET-upgrade sweep also kicks once on startup so the dashboard shows live data immediately.)

**2. Trigger a job manually**

```bash
curl -X POST localhost:5080/hub/dependencypilot/nuget-update \
  -H "Content-Type: application/json" \
  -d '{"repositoryKey":"sample-service","packageId":"Serilog","targetVersion":"3.1.1"}'
```

**3. Watch the gate do its job**

Open the dashboard at `http://localhost:5080/status/`, or poll the API:

```bash
curl localhost:5080/jobs
```

Now try a repository that isn't allowlisted and watch it bounce with a logged reason:

```bash
curl -X POST localhost:5080/hub/dependencypilot/nuget-update \
  -H "Content-Type: application/json" \
  -d '{"repositoryKey":"prod-secrets","packageId":"Serilog","targetVersion":"3.1.1"}'
# → status Rejected: "Repository 'prod-secrets' is not on the allowlist."
```

**4. Simulate a feed webhook**

```bash
curl -X POST localhost:5080/hub/webhooks/nuget-package-published \
  -H "Content-Type: application/json" \
  -d '{"packageId":"Serilog","version":"3.1.1"}'
```

**Running real sandboxes.** Flip the Runner to CLI mode and give it a worker image + bot token:

```
Runner__Sandbox__Mode=Cli                    # podman by default; Runner__Sandbox__Cli=docker also works
Runner__Sandbox__WorkerGitToken=<limited-bot-token>
```

…and make sure the worker image appears in both `Guard:ContainerImages` and `Guard:JobTypeImages`.

**Run the tests** (350 across 17 projects, including every security invariant):

```bash
dotnet test
```

---

## Configuration: who controls what

All power sits in configuration files that only administrators edit. Callers of the API can only pick from what these files allow.

**The Runner (`src/DevAgent.Runner.Api/appsettings.json`) — the authoritative allowlists:**

```jsonc
"Guard": {
  "Repositories": [                       // the ONLY repos the platform may touch
    { "Key": "sample-service", "CloneUrl": "https://git.internal/.../sample-service.git", "BaseBranch": "main" }
  ],
  "Packages": [ "Serilog", "Newtonsoft.Json", "Polly" ],   // the ONLY packages it may update
  "AllowedTargetFrameworks": [ "net8.0", "net9.0", "net10.0" ],
  "ContainerImages": [ "registry.internal/devagent/worker-dotnet:8.0" ],
  "JobTypeImages": { "NuGetUpdate": "…", "DotNetUpgrade": "…" }
},
"Runner": {
  "Sandbox": { "Mode": "Stub", "Cli": "podman" }   // or Mode "Cli" + resource limits + bot token
}
```

**The Hub (`src/DevAgent.Hub.Api/appsettings.json`) — what the agents watch:**

```jsonc
"DependencyPilot": {
  "RepositoryKeys": [ "sample-service" ], // repos the agent proposes updates for
  "WatchedPackages": [ "Serilog" ]        // packages it watches on the feed
},
"NuGetFeed": { "BaseUrl": "https://api.nuget.org" },  // or your internal feed
"PackageUsage": { ... },                  // declarative repo→package usage map
"DotNetUpgrader": {
  "RepositoryKeys": [ "sample-service" ],
  "TargetFramework": "net10.0"            // nightly sweep target
}
```

Note the layering: the agents' watch lists say what they *care about*; the Runner's Guard lists say what the platform *is allowed to do*. A job must pass **both**.

**With the store enabled** (`ConnectionStrings:DevAgent`, on by default in compose), all of the
above lives in SQLite and is edited in the **admin console** instead — the JSON sections then
only seed an empty database on first run.

**The worker** reads only environment variables (all set by the Runner, never by users): `DEVAGENT_JOB_TYPE`, `DEVAGENT_JOB_ID`, `DEVAGENT_CLONE_URL`, `DEVAGENT_BASE_BRANCH`, `DEVAGENT_PACKAGE_ID` / `DEVAGENT_TARGET_VERSION` (NuGet jobs), `DEVAGENT_TARGET_FRAMEWORK` (upgrade jobs), `DEVAGENT_WORKSPACE`, `DEVAGENT_GIT_TOKEN`, `DEVAGENT_ONLY_UPGRADE`, and — only when the operator enables AI repair — `DEVAGENT_LLM_PROVIDER` + `DEVAGENT_LLM_MODEL`, plus, when MCP is granted, `DEVAGENT_MCP_GATEWAY`, `DEVAGENT_MCP_TOKEN` (per-job), `DEVAGENT_MCP_TOOLS` (granted descriptors) and `DEVAGENT_SKILL_INSTRUCTIONS`.

---

## MCP servers & skills

**MCP servers** (Model Context Protocol) give agents extra tools and prompts — under the
platform's rules, not instead of them:

1. An administrator **registers** a server in the admin console: a key, its endpoint, and
   which of its tools/prompts the platform may use at all. Credentials are referenced as an
   environment-variable *name* on the Runner — the secret value is never stored or displayed.
2. Each agent gets an explicit **grant**: which servers, which tools, which prompts. The
   platform always takes the **intersection** of registration and grant — neither side can
   widen the other.
3. At job start the Runner lists the granted tools (with their real schemas), mints a
   **short-lived per-job token**, and passes both into the sandbox. The model sees the tools
   as `mcp__{server}__{tool}`.
4. When the agent calls one, the sandbox talks to the **Runner's MCP gateway** — never to the
   MCP server itself. The gateway re-validates the call against registration ∩ grant, holds
   the credentials, executes it, and audits it. An ungranted call is denied and logged.

**Skills** are reusable instruction packages for the repair agent — inline markdown, or backed
by a registered **MCP prompt** (fetched host-side at job time, with arguments). A skill can
*require* tools, but it can never *grant* them: if an agent lacks a required tool, the skill is
refused and the refusal is audited. Skill text that reaches the model is recorded as prompt
evidence.

The security model is unchanged by all of this: the built-in tool set stays closed; MCP adds
exactly one new typed call, behind two allowlists, a gateway, and the audit log.

## The admin console

`http://localhost:5080/admin/` — login `admin` + `DEVAGENT_ADMIN_PASSWORD` (if unset, a random
password is generated and printed once in the Hub's logs). Behind the login you manage:

- **Allowlists** — repositories (key → clone URL), packages, container images, the job-type →
  image map, target frameworks, and the package-usage map.
- **Agents** — watch lists, LLM provider/model pin, MCP grants and skills per agent.
- **MCP servers** and **Skills** — as described above.
- **Webhooks** — enable/disable and shared secrets (`X-DevAgent-Secret`).
- **Audit** — the live audit trail and every configuration change ever made (who, what, when).

Changes are stored in SQLite (shared with the Runner via a volume) and apply from the **next
job** — no restart. Every save is recorded twice: in the config-change log and in the audit
trail. Authentication is cookie-based with a PBKDF2-hashed local admin user; the seam is
standard ASP.NET authentication, so OIDC (Entra ID, Keycloak…) can be added without touching
the UI. The Hangfire dashboard requires the same login outside Development.

## The AI repair loop, honestly

**What it is.** When the deterministic edit breaks the build (a renamed API, a stricter signature), the Forge coding agent can attempt a fix inside the same sandbox: read the error, read the code, apply a patch, rebuild — in a loop with a **hard iteration cap**.

**Which model?** `DevAgent.Bridge.Llm` ships clients for **Claude (the default), ChatGPT and Gemini**. Each agent can pin its own provider and model in configuration (e.g. `DependencyPilot:Llm`), and the sandbox launcher can set it per job — always the operator's choice, never the API caller's. No provider configured = no AI, and the platform works fine without it.

**What keeps it honest:**

- It acts only through the seven structured tools; each call is path-validated, checked against the protected-file policy, and audit-logged *before* it runs.
- It cannot read secrets, touch deployment files, leave the repository checkout, or run anything except `dotnet build/test` and `git status`.
- Its "I'm done" claim is worthless on its own — the worker re-runs restore/build/test deterministically and only proceeds on green.
- Its complete diff and reasoning summary are saved, and the PR is labelled so reviewers know an AI touched it.

---

## What's real and what's a placeholder today

Honesty section — the platform is wired end-to-end and tested, but a few edges are deliberately simple:

| Area | Today | Production step |
|---|---|---|
| Git provider | `PlaceholderGitProvider` (logs, returns fake PR URL, still refuses auto-merge) | Implement `IGitProvider` for GitHub/GitLab/Azure DevOps/Bitbucket |
| LLM clients | Claude/ChatGPT/Gemini clients implemented; need an API key at runtime | Provide keys via your secret store; pick models per agent |
| Sandbox mode | `Stub` by default; hardened `Cli` (podman/docker) launcher implemented and tested | Flip config, later swap for Kubernetes Jobs behind `ISandboxJobRunner` |
| Job tracking & Hangfire | In-memory (reset on restart) | Point Hangfire + the job tracker at a database |
| Package usage map | Declarative config | Replace with an index produced by sandboxed scans |
| Audit sink | Console + in-memory ring (admin console window) | Implement `IAuditLog` against a durable, append-only store |
| Admin login | Local user (PBKDF2), cookie auth | Add your OIDC provider behind the same authentication seam |
| MCP transport | Streamable HTTP (tools + prompts) | stdio-launched local servers as a separately-gated feature |
| CI providers | GitHub Actions / GitLab CI / Azure DevOps clients implemented (read-only); need a CI connection + token env var per repo | Add connections in the admin console; set the token env vars on the Hub host |
| PR review comments | `PlaceholderGitProvider` logs the review | Same `IGitProvider` production step as PRs |
| SplunkSentinel / ConfluenceGuide | Observer tier: searches + sync plans recorded as audited findings; Confluence publish is an explicit endpoint | Configure connections; publishing stays operator-triggered by design |

Each row is behind an interface, so none of these upgrades touch the security model.

---

## FAQ

**Why repository *keys* instead of URLs?**
If the API accepted URLs, anyone who could call it could point the platform at any repository on the internet. With keys, the worst a caller can do is pick from the admin-approved list. The URL only exists inside the Runner, after validation.

**Can DevAgent merge my PR if reviews are slow?**
No. There is no merge code anywhere, auto-merge requests throw an exception, and tests fail if the capability is ever added.

**What happens if the AI "goes rogue"?**
It can call seven tools, inside one directory, for a capped number of iterations, with every call logged and every edit checked against the protected-file policy. The realistic worst case is a bad code change — which lands in a pull request labelled as AI-repaired, in front of a human, with the full diff and tool-call log attached.

**Does the worker need Docker/Podman access?**
No — and it must never have it. Only the Runner launches containers. The worker just sees a filesystem and environment variables.

**Why Podman rather than Docker?**
Podman is daemonless and runs rootless by default: there's no privileged daemon socket to expose, and a container breakout lands as an unprivileged user. Docker is still supported via `Runner__Sandbox__Cli=docker`.

**Which Git providers are supported?**
The worker and agents depend only on `IGitProvider`. The placeholder ships today; concrete GitHub/GitLab/Azure DevOps/Bitbucket implementations slot in without touching workers or agents.

**How do I add a new agent (e.g. PipelineDoctor)?**
Create a project like `Agents.DependencyPilot` or `Agents.DotNetUpgrader`: it proposes jobs through the Hub and inherits the gate, sandbox and audit trail. Agents contain *what to do*; the platform owns *what's allowed*.

---

## Project map

```
src/
  DevAgent.Contracts/        Shared DTOs, enums, validation results (no dependencies)
  DevAgent.Audit/            Audit events + IAuditLog (console sink today)
  DevAgent.Guard/            SECURITY CORE: allowlist policies, path validation, SafeCommandRunner,
                             write-scope + ref-name policies
  DevAgent.Bridge.Git/       IGitProvider (PRs + review comments) + placeholder implementation
  DevAgent.Bridge.NuGet/     NuGet V3 feed client + package-usage scanner
  DevAgent.Bridge.Ci/        Read-only CI providers: GitHub Actions, GitLab CI, Azure DevOps
  DevAgent.Bridge.Llm/       Claude / ChatGPT / Gemini clients + factory (model per agent)
  DevAgent.Bridge.Mcp/       MCP client (tools + prompts), grant policy, gateway client
  DevAgent.Bridge.Splunk/    Minimal read-only Splunk oneshot-search client
  DevAgent.Bridge.Confluence/ Minimal Confluence page client (find/upsert)
  DevAgent.Store/            SQLite config store (EF Core): everything the admin console edits
  DevAgent.Forge/            The caged coding agent: tools, policies, loop, factory
  DevAgent.Worker.DotNet/    Runs inside the sandbox: update/upgrade/repair/docs/review flows
  DevAgent.Runner.Api/       The gate: validation + stub/CLI (podman/docker) sandbox launchers
  DevAgent.Hub.Api/          Front door: API, webhooks, Hangfire schedules, dashboard, admin console
  Agents.DependencyPilot/    Agent: NuGet dependency updates
  Agents.DotNetUpgrader/     Agent: target-framework upgrades (scheduled example)
  Agents.PipelineDoctor/     Agent: CI failure watcher → sandboxed pipeline repair
  Agents.DocScribe/          Agent: scheduled documentation maintenance (docs-scoped)
  Agents.CodeReviewer/       Agent: read-only PR review via webhook
  Agents.SplunkSentinel/     Agent (observer): scheduled Splunk searches → audited findings
  Agents.ConfluenceGuide/    Agent (planner): docs → Confluence sync plan + explicit publish

tests/                       17 projects, 350 tests — every security invariant is locked
docs/
  GUIDE.md                   This document
  index.html                 Single-page getting-started reference (Danish)
  landing/index.html         The product landing page (mirrored into
                              DevAgent.Hub.Api/wwwroot/index.html, served at "/" —
                              re-copy after editing this file)
```
