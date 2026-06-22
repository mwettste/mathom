# Task 1 Report: GlossaryTerm table + user-scoped GlossaryService

## What Was Implemented

- **`src/Mathom.Web/Domain/GlossaryTerm.cs`**: New entity with `Guid Id`, `string UserId`, `string Term`, `DateTimeOffset CreatedAt`.
- **`src/Mathom.Web/Data/MathomDbContext.cs`**: Added `DbSet<GlossaryTerm> GlossaryTerms` and EF config in `OnModelCreating` — required properties, cascade-delete FK to `AspNetUsers`, unique index on `(UserId, Term)`.
- **`src/Mathom.Web/Glossary/GlossaryService.cs`**: New service with `GetTermsAsync` (user-scoped, oldest-first), `AddAsync` (trim, reject empty, case-insensitive dedupe, catch `DbUpdateException` race), `RemoveAsync` (user-scoped — filters by both `Id` and `UserId`).
- **`src/Mathom.Web/Program.cs`**: `builder.Services.AddScoped<Mathom.Web.Glossary.GlossaryService>()` added next to `NoteService`.
- **`src/Mathom.Web/Data/Migrations/20260622134055_AddGlossary.cs`**: Additive migration — `Up` is a single `CreateTable("GlossaryTerms", …)` plus `CreateIndex` for the unique `(UserId, Term)` index. No `DropTable` in `Up`.
- **`tests/Mathom.Tests/GlossaryServiceTests.cs`**: Three tests as specified in brief (plus `using Microsoft.EntityFrameworkCore;` added since brief omitted it and `FirstAsync` requires it).

## TDD Evidence

**Step 3 — Compile failure (expected):**
```
error CS0234: The type or namespace name 'Glossary' does not exist in the namespace 'Mathom.Web'
```

**Step 9 — All 3 pass:**
```
dotnet test --nologo --filter "FullyQualifiedName~GlossaryServiceTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 400 ms
```

## Migration `Up` Contents

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(
        name: "GlossaryTerms",
        columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            UserId = table.Column<string>(type: "text", nullable: false),
            Term = table.Column<string>(type: "text", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        },
        constraints: table =>
        {
            table.PrimaryKey("PK_GlossaryTerms", x => x.Id);
            table.ForeignKey(
                name: "FK_GlossaryTerms_AspNetUsers_UserId",
                column: x => x.UserId,
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        });

    migrationBuilder.CreateIndex(
        name: "IX_GlossaryTerms_UserId_Term",
        table: "GlossaryTerms",
        columns: new[] { "UserId", "Term" },
        unique: true);
}
```

Pure `CreateTable` + `CreateIndex`. No `DropTable` in `Up`. Safe for a live database with a persistent volume.

## Files Changed

- `src/Mathom.Web/Domain/GlossaryTerm.cs` (created)
- `src/Mathom.Web/Glossary/GlossaryService.cs` (created)
- `src/Mathom.Web/Data/MathomDbContext.cs` (modified — DbSet + OnModelCreating config)
- `src/Mathom.Web/Program.cs` (modified — DI registration)
- `src/Mathom.Web/Data/Migrations/20260622134055_AddGlossary.cs` (created)
- `src/Mathom.Web/Data/Migrations/20260622134055_AddGlossary.Designer.cs` (created)
- `src/Mathom.Web/Data/Migrations/MathomDbContextModelSnapshot.cs` (modified by EF tooling)
- `tests/Mathom.Tests/GlossaryServiceTests.cs` (created)

## Self-Review

- All three tests pass, including the hard constraint `Glossary_IsUserScoped` which validates that user B cannot remove user A's terms.
- `AddAsync` normalizes (trim), rejects empty/whitespace, performs a case-insensitive pre-check before inserting, and catches `DbUpdateException` as a safety net for races against the unique DB index.
- Migration is strictly additive — verified by reading the generated file directly.

## Code Review Fix Report (Review Findings A/B/C)

### Finding A — Detach failed entity on dedupe race backstop

Changed `AddAsync` to capture the new entity in a local variable `entity` before adding to the context. In the `catch (DbUpdateException)` block, added `_db.Entry(entity).State = EntityState.Detached;` before returning false. This prevents the stale `Added`-state entity from being re-inserted if the same `DbContext` instance is reused.

### Finding B — Invariant culture for dedupe comparison

Changed `term.ToLower()` to `term.ToLowerInvariant()` for the C#-side local variable `lower`. The EF query side (`g.Term.ToLower()`) was left unchanged — it must remain a translatable SQL expression.

### Finding C — Strengthen ordering test

In `Remove_Works_AndListIsOldestFirst`, added an assertion before the remove step:
```csharp
var termsBeforeRemove = await svc.GetTermsAsync(u, CancellationToken.None);
Assert.Equal(new[] { "Alpha", "Beta" }, termsBeforeRemove); // oldest-first ordering
```
This ensures the ordering is genuinely exercised with two results before the remove operation.

### Test Run

Command: `dotnet test --nologo --filter "FullyQualifiedName~GlossaryServiceTests"`

Output:
```
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 382 ms - Mathom.Tests.dll (net10.0)
```

All 3 GlossaryServiceTests pass after the review fixes.

## Concerns / Deviations

- **Minor deviation**: The brief's test code was missing `using Microsoft.EntityFrameworkCore;` (needed for `db.GlossaryTerms.FirstAsync(...)`). Added it to the test file. No functional change.
- No other deviations from the brief.
