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
environment file once on the server, in the deploy directory:

```bash
ssh marco@wettsti-edge
sudo mkdir -p /opt/apps/mathom
sudo $EDITOR /opt/apps/mathom/.env      # see keys below; chmod 600
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
