-- What a line's running production total read at the end of each hour.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. The editor runs the whole script as one transaction, so a
-- syntax error anywhere reverts all of it. Safe to run twice.
--
-- WHY A TABLE AT ALL
--
-- The Company production API answers one question: what has this line
-- made today, so far. It is a running total that climbs through the day
-- and carries no hour in it. Nothing on the vendor side says what the
-- line made between ten and eleven.
--
-- So the running total is written down at the end of each hour, and the
-- hour's production is the difference between two readings. A reading
-- at eleven of 165 against a reading at ten of 60 says the line made
-- 105 in that hour, and nobody had to type any of it.
--
-- One row per line per slot per day, and the unique constraint is what
-- makes that true: a reading is a fact about a moment, and a second
-- reading of the same moment is the same fact, not another one.

create table if not exists public.line_production_snapshots (
    id              bigint generated always as identity primary key,

    -- The factory day, in Asia/Kolkata and not the server's zone, which
    -- is UTC on Render - a reading taken at half five here would
    -- otherwise be stamped with tomorrow.
    snap_day        date        not null
        default ((now() at time zone 'Asia/Kolkata')::date),

    line_id         integer     not null,

    -- Which of the day's nine production slots had just ENDED when this
    -- reading was taken. The slots are defined in HourlyProduction.cs
    -- and are the floor's own: 9-10, 10-11, 11-12, 12-12.30, lunch,
    -- 1-1.30, 1.30-2.30, 2.30-3.30, 3.30-4.30, 4.30-5.30. Slot 10 is
    -- everything after half five, which is overtime.
    slot_index      smallint    not null,

    -- The reading itself: the day's total to that point, not the hour's
    -- own figure. Stored as read so that a missed hour costs the SPLIT
    -- and never the total - the next reading still carries everything
    -- made since the last one.
    cumulative_output   numeric(12, 2) not null default 0,
    cumulative_rejects  numeric(12, 2) not null default 0,

    -- When the reading was actually taken, which is not the end of the
    -- slot it belongs to: a reading taken at 11:04 is the eleven
    -- o'clock reading, and knowing it was four minutes late is how a
    -- figure that looks wrong gets explained.
    captured_at     timestamptz not null default now(),

    constraint line_production_snapshots_once
        unique (snap_day, line_id, slot_index)
);

-- Every read is one day, every line.
create index if not exists line_production_snapshots_day
    on public.line_production_snapshots (snap_day);

-- The API connects as the Postgres user in Supabase__ConnectionString,
-- which bypasses row level security. RLS is enabled anyway, with no
-- policy, so the anon and authenticated keys Supabase exposes to the
-- public internet cannot read this if the project ever uses them.
alter table public.line_production_snapshots enable row level security;
