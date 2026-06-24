# Mathom — common tasks. Run `just` to list recipes.
# Requires: just (https://github.com/casey/just), Docker, and a .env file (cp .env.example .env).

set dotenv-load := true

# Show available recipes
default:
    @just --list

# Build and start the stack in the background (Postgres + web; auto-migrates on startup)
up:
    docker compose up --build -d
    @echo "Mathom is starting on http://localhost:${WEB_PORT:-8080}  (/Capture to add a note, / for the timeline)"

# Start in the foreground (logs stream to your terminal; Ctrl-C to stop)
up-fg:
    docker compose up --build

# Start ONLY Postgres (on localhost:${POSTGRES_PORT:-5432}), so you can run the app
# yourself from the IDE/CLI. Migrations apply on app startup. Pair with `just run`.
db:
    docker compose up -d db
    @echo "Postgres ready on localhost:${POSTGRES_PORT:-5432}  →  just run"

# Run the app directly (not in Docker) with hot reload, all .env vars loaded as environment
# variables (connection string, LLM/STT keys, AdminEmail). Starts Postgres first. Ctrl-C to stop.
run: db
    ASPNETCORE_ENVIRONMENT=Development dotnet watch --project src/Mathom.Web

# Stop the stack (keeps the database volume)
down:
    docker compose down

# Stop the stack AND delete the database volume (wipes all captured data)
reset:
    docker compose down -v

# Rebuild the web image without cache and restart
rebuild:
    docker compose build --no-cache web
    docker compose up -d

# Restart just the web service (e.g. after changing .env)
restart:
    docker compose restart web

# Follow logs (all services). Use `just logs web` for one service.
logs service="":
    docker compose logs -f {{service}}

# Show container status
ps:
    docker compose ps

# Open a psql shell against the running database
psql:
    docker compose exec db psql -U "${POSTGRES_USER}" -d "${POSTGRES_DB}"

# Run the test suite locally (needs .NET 10 SDK + Docker for Testcontainers; not the compose stack)
test:
    dotnet test
