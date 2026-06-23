# Contributing to Mathom

Thanks for your interest! Mathom is a small, opinionated, self-hosted project, so here's how to
contribute smoothly.

## Please start a discussion before building a feature

If you'd like to add a **new feature**, please **open an issue or a GitHub Discussion first** so we can
talk through the idea, scope, and how it fits the roadmap — *before* you invest time building it.
Mathom has a deliberate design and a direction, and a quick conversation up front saves everyone from a
large PR that doesn't quite align. Unsolicited feature PRs may be declined simply because they weren't
discussed first — not because the work isn't good.

For **bug fixes, typos, docs, and small focused improvements**, feel free to open a PR directly.

## Development

- Requires the **.NET 10 SDK** and **Docker** (for the database and the integration tests).
- Run the app: `just up` (or `docker compose up --build`). See the [README](README.md) for
  configuration (notably the `.env` file and `AdminEmail`).
- Run the tests: `just test` (or `dotnet test`). Integration tests use
  [Testcontainers](https://testcontainers.com/), so Docker must be running.
- Keep the test suite green, and add tests for new behavior.
- Follow the existing style: file-scoped namespaces, `async` + `CancellationToken`, EF Core for data
  access (additive migrations only — never regenerate the initial migration), HTMX + server-rendered
  partials for interactivity.

## Reporting bugs

Open an issue with steps to reproduce, what you expected, and what actually happened. Logs help
(`just logs web`).

## License

By contributing, you agree that your contributions are licensed under the project's
[MIT License](LICENSE).
