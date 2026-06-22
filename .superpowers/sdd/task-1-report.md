# Task 1 Report: ItemProcessor tag reconcile

## What was implemented

### Problem
`ItemProcessor.ProcessAsync` loaded items without their `ItemTags` navigation property. On re-processing (item already has tags in the DB), the additive tag loop would:
1. Call `item.ItemTags.Any(it => it.TagId == ...)` on an empty in-memory collection → always false → always try to add → duplicate PK `(ItemId, TagId)` crash → item marked Failed
2. Leave stale tags from the previous processing run

### Fix (2 changes in `src/Mathom.Web/Processing/ItemProcessor.cs`)

1. **Load with Include**: Changed the item query from `_db.Items.FirstOrDefaultAsync(...)` to `_db.Items.Include(i => i.ItemTags).FirstOrDefaultAsync(...)` so the in-memory collection is populated and the duplicate-key guard works.

2. **Reconcile loop**: Replaced the additive `foreach` tag loop with a reconciling version that:
   - Tracks `desiredTagIds` (the tag IDs corresponding to the new cleanup result)
   - Skips adding an `ItemTag` join row if it already exists in `item.ItemTags` (avoiding the duplicate-key crash)
   - After the loop, calls `item.ItemTags.RemoveAll(it => !desiredTagIds.Contains(it.TagId))` to drop stale tags

Added `using System.Collections.Generic;` (needed for `List<int>`).

## TDD evidence

### Step 2: Confirmed failure before fix
```
dotnet test --nologo --filter "FullyQualifiedName~ItemProcessorTests.Reprocess_ReconcilesTags_NoDuplicateNoStale"
→ FAIL: Npgsql.PostgresException: 23505: duplicate key value violates unique constraint "PK_ItemTags"
```
Exactly the expected duplicate-key crash.

### Step 4: All ItemProcessor tests pass after fix
```
dotnet test --nologo --filter "FullyQualifiedName~ItemProcessorTests"
→ Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7
```
All 7 ItemProcessor tests pass (6 pre-existing + 1 new reconcile test).

### Full suite
```
dotnet test --nologo
→ Passed! - Failed: 0, Passed: 87, Skipped: 0, Total: 87
```

## Files changed
- `src/Mathom.Web/Processing/ItemProcessor.cs`: Added `using System.Collections.Generic;`, changed `.Items.FirstOrDefaultAsync` to `.Items.Include(i => i.ItemTags).FirstOrDefaultAsync`, replaced additive tag loop with reconciling version.
- `tests/Mathom.Tests/ItemProcessorTests.cs`: Added `Reprocess_ReconcilesTags_NoDuplicateNoStale` test.

## Self-review

The reconcile logic is correct and equivalent to original behavior for first-time processing (empty `ItemTags` collection → `RemoveAll` removes nothing, tags are added as before). The `desiredTagIds` list uses EF-assigned `tag.Id` which is populated after `SaveChangesAsync`, so the check is reliable. The brief's exact code was used verbatim.

## Concerns

None. The fix is minimal and well-targeted. The `RemoveAll` call on the EF-tracked `ICollection<ItemTag>` correctly marks those rows for deletion when `SaveChangesAsync` is called.
