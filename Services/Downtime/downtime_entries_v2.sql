-- Down time: how long the line actually stood.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. Safe to run twice.
--
-- The first pass recorded which line, what kind of thing stopped it,
-- whose it was to fix and why - everything except how long, which is
-- the one figure a down time report is added up on. From and to rather
-- than a number of minutes: somebody standing at a line knows when it
-- stopped and when it started again, and asking them to subtract is
-- asking for the subtraction to be wrong.
--
-- Nullable, because the column has to exist before the code that fills
-- it is deployed. Every new entry is refused without both - the API
-- requires them - so the nulls can only ever be rows written in the
-- minutes between this script and that deploy.

alter table public.downtime_entries
    add column if not exists from_at timestamptz,
    add column if not exists to_at   timestamptz;
