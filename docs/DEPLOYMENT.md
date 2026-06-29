# Deployment

Mathom self-deploys to the **wettsti.ch platform** (a Hetzner VM running a shared
`caddy-docker-proxy` edge, provisioned by the private `mwettste/hetzner-vps` repo).
Deploying is a **manual, owner-gated action** (this is a public repo): you trigger
the **Deploy** workflow by hand, an approval gate on the `production` environment
must pass, then GitHub Actions builds the image, publishes it to GHCR, and deploys
the stack to the server over Tailscale. It never runs automatically on push.

For running Mathom anywhere else (local or your own box), see the **standalone**
[`docker-compose.yml`](../docker-compose.yml) and the README instead — that path
builds locally and binds a host port. This document is specifically about the
platform deploy.

## How it works

```
manual run (Actions → Deploy → Run workflow)
  → gate job:  waits for approval on the `production` environment (required reviewer = you)
  → build-and-push job:  docker build → push ghcr.io/mwettste/mathom:{latest,sha-…}
  → deploy job (steps inlined from the hetzner-vps deploy-app workflow):
       join tailnet → scp docker-compose.deploy.yml to /opt/apps/mathom/docker-compose.yml
       → docker compose pull && up -d   (over Tailscale SSH)
       → upsert proxied Cloudflare A record  mathom.wettsti.ch → ORIGIN_IP
  caddy-docker-proxy sees the labels and routes  https://mathom.wettsti.ch → web:8080
```

The two relevant files in this repo:

- [`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml) — build/push + the inlined platform deploy steps (Tailscale SSH + Cloudflare DNS).
- [`docker-compose.deploy.yml`](../docker-compose.deploy.yml) — the production stack (image from GHCR, joins the `edge` network, Caddy labels, internal-only db).

The app at `https://mathom.wettsti.ch` is published on subdomain **`mathom`**.

## One-time setup

### 1. Create the `production` environment (the approval gate)

In **Settings → Environments**, create an environment named **`production`** and add
yourself under **Required reviewers**. The `gate` job in the workflow targets this
environment, so every deploy pauses until you approve it — this is what makes the
deploy owner-only on a public repo. Optionally also restrict it to the `main` branch
under **Deployment branches**.

### 2. Repo secrets and variable

In this repo's **Settings → Secrets and variables → Actions**, add the four secrets
the platform workflow needs (values come from the platform owner / 1Password):

| Secret | Purpose |
| --- | --- |
| `TS_OAUTH_CLIENT_ID` | Tailscale OAuth client (tag:ci) so CI can SSH to the server |
| `TS_OAUTH_SECRET` | "" |
| `CLOUDFLARE_API_TOKEN` | upsert the `mathom.wettsti.ch` DNS record |
| `CLOUDFLARE_ZONE_ID` | the `wettsti.ch` zone id |

And one **variable**:

| Variable | Value |
| --- | --- |
| `ORIGIN_IP` | the platform's `server_ipv4` (from `tofu output` in hetzner-vps) |

These can be plain repository secrets. (For tighter isolation you could scope them
to the `production` environment and add `environment: production` to the `deploy`
job so only the gated job can read them — repository secrets + the gate job is the
simplest setup and what the workflow assumes.)

### 3. Make the GHCR image pullable from the server

The server runs `docker compose pull` with no registry login, so the image must be
**public**. After the first successful `build-and-push`, open the
`mathom` package under <https://github.com/users/mwettste/packages>, then **Package
settings → Change visibility → Public**. (Alternatively, keep it private and add a
`docker login ghcr.io` step on the server — not covered here.)

### 4. Place the server-side `.env` (secrets)

The deploy workflow ships **only** the compose file, never secrets. Create the
deploy directory and environment file once on the server. The deploy runs over
Tailscale SSH as `marco`, so the directory must be **owned by `marco`** (not root)
or `scp` fails with `Permission denied`:

```bash
ssh marco@wettsti-edge
sudo install -d -o marco -g marco /opt/apps/mathom   # marco-owned dir
$EDITOR /opt/apps/mathom/.env                         # see keys below; then: chmod 600 .env
```

`/opt/apps/mathom/.env` must contain (mirror [`.env.example`](../.env.example)):

```dotenv
POSTGRES_USER=mathom
POSTGRES_PASSWORD=<a strong password>
POSTGRES_DB=mathom

# REQUIRED — owner email promoted to the Admin role on startup. Without it nobody
# can approve new users at /Admin/Users. Use the email you register/registered with.
AdminEmail=marco.wettstein@gmail.com

# At least one LLM provider so capture cleanup works (see .env.example for Infomaniak).
Llm__OpenRouter__ApiKey=<key>
Llm__OpenRouter__Model=openai/gpt-4o-mini

# Optional: voice transcription.
Stt__Infomaniak__ApiKey=<key>
Stt__Infomaniak__Model=whisper
```

Docker Compose reads this `.env` from the deploy directory for both `${...}`
interpolation in the compose file and the `web` service's `env_file`. It persists
across deploys (the workflow overwrites `docker-compose.yml` only). Edit it and
re-run the deploy (or `docker compose up -d` on the server) to apply changes.

> **Create the `.env` before the first deploy.** The `web` service declares
> `env_file: .env`, so `docker compose up` fails if the file is missing.

## Triggering a deploy

- **Actions → Deploy → Run workflow** (or `gh workflow run deploy.yml`). Only users
  with write access can start it. The run waits on the `production` environment until
  a required reviewer approves, then builds and deploys. There is no push trigger.

## Preview deployments (per-PR)

Ephemeral preview environments let you spin up a full, isolated copy of the app for a
pull request at its own subdomain, then tear it down. They reuse the same platform
(edge Caddy + Cloudflare + Tailscale) as production.

The two relevant files:

- [`.github/workflows/preview.yml`](../.github/workflows/preview.yml) — manual,
  owner-gated workflow with a `pr_number` input and a `deploy`/`destroy` mode.
- [`docker-compose.preview.yml`](../docker-compose.preview.yml) — a near-copy of the
  production stack, parametrised by `${IMAGE_TAG}` and `${PREVIEW_HOST}`, with the
  Authenticated Origin Pulls labels removed.

### How it works

```
Actions → Preview → Run workflow  (pr_number=12, mode=deploy)
  → gate: waits for approval on the `preview` environment (required reviewer = you)
  → build-and-push: build the PR's HEAD → ghcr.io/mwettste/mathom:pr-12
  → deploy: Tailscale SSH → /opt/apps/_previews/mathom-pr-12
       cp /opt/apps/mathom/.env  (reuse prod secrets; DB is still isolated)
       scp docker-compose.preview.yml → docker-compose.yml
       docker compose -p mathom-pr-12 up -d        (own DB + volumes, namespaced)
       → upsert proxied A record  mathom-pr-12.wettsti.ch → ORIGIN_IP
  caddy-docker-proxy routes  https://mathom-pr-12.wettsti.ch → web:8080
```

`mode=destroy` skips the gate (no approval needed) and runs
`docker compose -p mathom-pr-12 down -v`, removes the deploy dir, and deletes the DNS
record.

### Why these choices

- **Isolation is by compose project name** (`mathom-pr-<N>`). Docker namespaces the
  containers and the three named volumes per project, so each preview has its own
  Postgres, media and Data Protection keys. No production data is touched.
- **Image from the PR, compose from `main`.** The image is built from the PR's HEAD (so
  the preview runs the PR's code), but the `deploy` job ships `docker-compose.preview.yml`
  from `main`, not from the PR. So any PR can be previewed without carrying the compose
  file on its branch — including PRs branched before previews existed — and a PR cannot
  rewrite the preview compose (e.g. to mount host paths or run privileged) on the server.
  The trade-off: changes to `docker-compose.preview.yml` itself are only exercised once
  merged to `main`, not from a PR preview.
- **Secrets never enter the workflow.** The deploy step copies the existing
  `/opt/apps/mathom/.env` into the preview dir, reusing the LLM keys and `AdminEmail`.
  The DB stays isolated regardless (separate container + namespaced volume), so reusing
  the same `POSTGRES_*` values just seeds a fresh, empty database.
- **Painless first login.** A fresh preview DB has no users, and `AdminEmail` only
  auto-promotes an *existing* matching account. Set the repo secret `PREVIEW_ADMIN_EMAILS`
  (comma-separated) so registering on a preview with any of those emails — or the prod
  `AdminEmail` — yields an instantly approved Admin account instead of being parked at
  `/Pending`. The workflow appends it as `PreviewAdminEmails=…` to the preview's `.env`
  only; it is never written to the production `.env`, so prod's admin set is unchanged.
- **TLS needs no infra change.** The platform's Origin CA cert already covers
  `*.wettsti.ch`, so the flat host `mathom-pr-<N>.wettsti.ch` is valid out of the box.
  (A nested host like `pr-<N>.mathom.wettsti.ch` would *not* be covered by the single
  wildcard — that's why previews use flat names.)
- **No Authenticated Origin Pulls on previews.** AOP is enabled per-hostname in the
  infra repo (`var.aop_hostnames`) and only covers `mathom.wettsti.ch`. Cloudflare does
  not present the client cert for preview hosts, so requiring it would make Caddy reject
  every handshake. The edge firewall still limits 80/443 to Cloudflare IPs, which is an
  acceptable posture for a throwaway preview. (To harden a long-lived preview, add its
  host to `var.aop_hostnames`, apply the infra repo, then add the `client_auth` labels.)
- **Owner-gated like production.** A `gate` job binds to a `preview` GitHub environment
  with a required reviewer, so a deploy pauses for one approval before it builds — the
  same pattern as the production `gate` job. `mode=destroy` does not need the gate, so
  teardown is always frictionless. This reuses the same repository secrets as the
  production Deploy (`TS_OAUTH_*`, `CLOUDFLARE_*`) and the `ORIGIN_IP` variable — no new
  secrets to add.

### One-time setup

The preview workflow reuses everything the production Deploy already needs (repo
secrets, `ORIGIN_IP`, the server-side `/opt/apps/mathom/.env`). Two extra steps (plus one
optional):

- **Create the `preview` environment.** In **Settings → Environments**, add an
  environment named **`preview`** and put yourself under **Required reviewers** (same as
  the `production` environment in step 1 above). Until this environment exists with a
  reviewer, the `gate` job passes straight through and a deploy runs without pausing.

- **Create a `marco`-owned previews directory on the server.** Each preview lives in its
  own dir under `/opt/apps/_previews/`, which the workflow creates and removes
  automatically. The deploy runs as `marco` over Tailscale SSH, but `/opt/apps` itself is
  root-owned, so `marco` cannot create subdirs there directly. Make the previews parent
  `marco`-owned once:

  ```bash
  ssh marco@wettsti-edge
  sudo install -d -o marco -g marco /opt/apps/_previews
  ```

  (`install -d -o marco -g marco` just creates the directory owned by `marco:marco` —
  equivalent to `mkdir` + `chown`. Production app dirs under `/opt/apps` are unaffected.)

- **(Optional) Set `PREVIEW_ADMIN_EMAILS` for painless preview logins.** In **Settings →
  Secrets and variables → Actions**, add a repository secret `PREVIEW_ADMIN_EMAILS` with a
  comma-separated list of emails. Registering on any preview with one of them (or the prod
  `AdminEmail`) yields an instantly approved Admin account. Leave it unset to keep the old
  behavior (only the prod `AdminEmail` auto-promotes).

### Triggering

- **Deploy:** Actions → Preview → Run workflow, set `pr_number` and `mode=deploy` (or
  `gh workflow run preview.yml -f pr_number=12 -f mode=deploy`). Only users with write
  access can start it; the run then waits for approval on the `preview` environment.
- **Destroy:** same workflow with `mode=destroy` (or
  `gh workflow run preview.yml -f pr_number=12 -f mode=destroy`). Runs immediately (no
  approval): tears the stack down, wipes that preview's volumes, deletes the DNS record.
- Remember to run `mode=destroy` when the PR is closed/merged; nothing tears previews
  down automatically. (Promoting this to a label-gated `pull_request` workflow with a
  `closed`-trigger cleanup is the natural next step — "Option B".)

### Notes / not handled yet

- **GHCR image cleanup.** Each preview leaves a `pr-<N>` image tag in the GHCR package.
  Delete stale tags periodically (package settings or the `gh api` packages endpoint).
- **No seed data.** Previews start with an empty DB. Add a `pg_dump`/restore step if you
  want representative content.

## Operating notes

- **Data lives in named volumes** on the server: `mathom-pgdata` (Postgres),
  `mathom-media` (captured audio), `mathom-dataprotection` (auth/anti-forgery keys
  — keep this so login cookies survive redeploys). Back these up; there is no
  automated backup yet (a `pg_dump` cron is the obvious next step).
- **Migrations are additive only.** The app runs `Database.Migrate()` on startup
  against the persistent `mathom-pgdata` volume. Never regenerate `InitialCreate`
  (it crash-loops the container against an existing schema); only add migrations on
  top of the existing chain.
- **Rollback:** images are also tagged `sha-<commit>`. To pin a known-good build,
  set `image:` in `docker-compose.deploy.yml` to that tag and redeploy.
- **HTTPS:** Caddy terminates TLS with the platform's Cloudflare Origin CA cert
  (the mandatory `caddy.tls` label). Mathom is a PWA and needs HTTPS to install /
  go offline — `mathom.wettsti.ch` satisfies that.
- **Container hardening:** the `web` image runs as the non-root user **uid 1654**,
  with `no-new-privileges` on both services and all Linux capabilities dropped on
  `web`. A fresh deployment works out of the box (a new volume inherits 1654
  ownership). An **existing** deployment whose `mathom-dataprotection` / `mathom-media`
  volumes were created while the app ran as root needs a **one-time `chown`** before
  the first non-root deploy — otherwise the app can neither read existing Data
  Protection keys (all sessions drop) nor write new ones (it crashes):

  ```bash
  ssh marco@wettsti-edge
  cd /opt/apps/mathom
  docker compose down                       # stop the old root container
  docker run --rm \
    -v mathom_mathom-dataprotection:/keys \
    -v mathom_mathom-media:/media \
    alpine chown -R 1654:1654 /keys /media  # re-own the persisted data
  ```

  Then trigger the **Deploy** workflow (pulls the non-root image and starts `web` as
  1654 on the now-1654-owned volumes). Verify the volume names first with
  `docker volume ls` — they are `<project>_<name>`, and the project is the deploy dir
  (`mathom`). The `mathom-pgdata` volume is unaffected (Postgres is unchanged).
