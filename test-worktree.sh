#!/bin/bash
set -euo pipefail

# Launch an existing Mathom worktree for manual testing.
#
# Lists the open GitHub PRs whose head branch already has a local git worktree,
# lets you pick one, copies your .env into that worktree, and opens a new iTerm
# window running `just dev` (Postgres + Tailwind --watch + dotnet watch hot reload).
#
# Worktree creation is intentionally out of scope. Create one first with:
#   git worktree add worktrees/<name> <branch>
# (the worktrees/ dir is gitignored).

# --- Configuration ---
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$SCRIPT_DIR"
ENV_SRC="$REPO_ROOT/.env"
PR_LIMIT=100

# --- Pre-flight checks ---
command -v gh >/dev/null 2>&1       || { echo "ERROR: gh CLI not found." >&2; exit 1; }
command -v just >/dev/null 2>&1     || { echo "ERROR: just not found (https://github.com/casey/just)." >&2; exit 1; }
command -v osascript >/dev/null 2>&1 || { echo "ERROR: osascript not found (macOS only)." >&2; exit 1; }
[[ -f "$ENV_SRC" ]] || { echo "ERROR: $ENV_SRC not found (cp .env.example .env)." >&2; exit 1; }

# Return the worktree path for a given branch, empty if none.
worktree_path_for_branch() {
    local target="$1"
    git -C "$REPO_ROOT" worktree list --porcelain | awk -v b="refs/heads/$target" '
        /^worktree /{ p = substr($0, 10) }
        /^branch /  { if ($2 == b) print p }
    '
}

# --- Collect open PRs that have a matching local worktree ---
echo "Fetching open PRs..."
PR_NUM=()
PR_TITLE=()
PR_BRANCH=()
PR_PATH=()

while IFS=$'\t' read -r num title branch; do
    [[ -n "$branch" ]] || continue
    path="$(worktree_path_for_branch "$branch")"
    [[ -n "$path" ]] || continue
    # Skip the main checkout — this script launches dedicated worktrees only.
    [[ "$path" -ef "$REPO_ROOT" ]] && continue
    PR_NUM+=("$num")
    PR_TITLE+=("$title")
    PR_BRANCH+=("$branch")
    PR_PATH+=("$path")
done < <(cd "$REPO_ROOT" && gh pr list --state open --limit "$PR_LIMIT" \
    --json number,title,headRefName \
    --jq '.[] | "\(.number)\t\(.title)\t\(.headRefName)"')

if [[ ${#PR_NUM[@]} -eq 0 ]]; then
    echo "No open PRs have a local worktree. Create one first:"
    echo "  git worktree add worktrees/<name> <branch>"
    exit 0
fi

# --- Selection menu ---
echo ""
echo "Open PRs with a local worktree:"
for i in "${!PR_NUM[@]}"; do
    printf "  [%d] #%s  %s  (%s)\n" "$((i + 1))" "${PR_NUM[$i]}" "${PR_TITLE[$i]}" "${PR_BRANCH[$i]}"
done
echo ""

read -rp "Select PR to test [1-${#PR_NUM[@]}]: " choice
if ! [[ "$choice" =~ ^[0-9]+$ ]] || (( choice < 1 || choice > ${#PR_NUM[@]} )); then
    echo "Aborted: invalid selection." >&2
    exit 1
fi

idx=$((choice - 1))
WORKTREE_DIR="${PR_PATH[$idx]}"
[[ -d "$WORKTREE_DIR" ]] || { echo "ERROR: $WORKTREE_DIR does not exist." >&2; exit 1; }

echo ""
echo "Launching PR #${PR_NUM[$idx]} (${PR_BRANCH[$idx]})"
echo "  Worktree: $WORKTREE_DIR"
echo "  NOTE: 'just dev' starts Postgres on \${POSTGRES_PORT:-5432}. If your main"
echo "        checkout's stack is up, stop it (just down) or set a distinct"
echo "        POSTGRES_PORT in the worktree's .env to avoid a host-port clash."

# .env is gitignored, so a fresh worktree won't have it — copy it in.
cp "$ENV_SRC" "$WORKTREE_DIR/.env"

# One iTerm tab: install the CSS toolchain deps, then `just dev`, which starts
# Postgres + Tailwind --watch + `dotnet watch`. && chaining keeps the tab open
# on a failing step so you can read the error.
RUN_CMD="cd '$WORKTREE_DIR' && npm install && just dev"

osascript <<EOF
tell application "iTerm"
    activate
    set newWindow to (create window with default profile)
    tell current session of newWindow
        set name to "mathom: ${PR_BRANCH[$idx]}"
        write text "$RUN_CMD"
    end tell
end tell
EOF

echo "Done. iTerm window opened running 'just dev'. Watch the tab for the app's"
echo "listening URL (dotnet watch). Tip: 'just up' in the worktree instead runs"
echo "the full Docker stack on http://localhost:\${WEB_PORT:-8080}."
