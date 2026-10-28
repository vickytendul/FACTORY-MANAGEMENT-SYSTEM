-- Each stoppage, signed off on its own.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. Safe to run twice.
--
-- The approval belongs here rather than in a table of its own, because
-- the thing being approved is one stoppage. A line can stand three
-- times in a day - once on Material against Cutting, once on Method
-- against Trims Store, once on something else again - and the GM looks
-- at each of those on its own terms. One signature for the day would
-- accept all three at once, which is not what reading them is.
--
-- Nullable, and absence is the unapproved state. No flag set to false,
-- so withdrawing leaves nothing behind claiming it was ever signed.

alter table public.downtime_entries
    add column if not exists approved_at      timestamptz,
    add column if not exists approved_by_code text,
    add column if not exists approved_by_name text;

-- What the report asks for every time it opens: the day's entries and
-- which of them are still waiting on somebody.
create index if not exists downtime_entries_unapproved
    on public.downtime_entries (entry_day)
    where approved_at is null;

-- If the downtime_approvals table was created from the earlier
-- day-level design, nothing uses it. Drop it when convenient:
--
--   drop table if exists public.downtime_approvals;
