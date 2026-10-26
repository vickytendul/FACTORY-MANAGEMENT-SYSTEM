-- Down time: why a line stopped, in the words of whoever saw it stop.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. The editor runs the whole script as one transaction, so a
-- syntax error anywhere reverts all of it. Safe to run twice.
--
-- New data with no Firestore history, so there is no source flag and
-- nothing to migrate: it has never lived anywhere else.

create table if not exists public.downtime_entries (
    id              bigint generated always as identity primary key,
    created_at      timestamptz not null default now(),

    line_id         integer     not null,
    -- The name as it was when the entry was made, so an old entry still
    -- reads correctly after a line is renamed.
    line_name       text        not null default '',

    -- Man, Machine, Material, Method, Environment, Others. Text rather than an
    -- enum or a check constraint: the floor is still settling this list
    -- (the department one has a gap in it today), and a constraint
    -- turns adding a word into a migration somebody has to remember to
    -- run before the deploy that needs it. The API checks the value
    -- against one list in code, which is the half of this that actually
    -- stops bad data.
    domain          text        not null,

    -- Quality, Cutting, Production, HR, Maintenance, Trims Store - who
    -- the stoppage belonged to, which is not always who reported it.
    department      text        not null,

    -- Why, in whoever's own words. The point of the whole table.
    reason          text        not null,

    entered_by_code text        not null default '',
    entered_by_name text        not null default '',

    -- The factory day this belongs to, as a plain date.
    --
    -- Not derived in an index from created_at: a timestamptz converted
    -- to a local day is not an expression Postgres will index, and the
    -- day an entry belongs to is a fact about it rather than something
    -- to recompute. Filled by the default at insert, which is evaluated
    -- then and so has no such restriction.
    --
    -- Asia/Kolkata and not the server's zone, which is UTC on Render: an
    -- entry made at nine at night here would otherwise land on
    -- tomorrow's report.
    entry_day       date        not null
        default ((now() at time zone 'Asia/Kolkata')::date)
);

-- The report is always one day, newest first.
create index if not exists downtime_entries_day
    on public.downtime_entries (entry_day, created_at desc);

-- And a line's own history, for the day the OWE sheet is showing.
create index if not exists downtime_entries_line
    on public.downtime_entries (entry_day, line_id);

-- The API connects as the Postgres user in Supabase__ConnectionString,
-- which bypasses row level security. RLS is enabled anyway, with no
-- policy, so the anon and authenticated keys Supabase exposes to the
-- public internet cannot read this if the project ever uses them.
alter table public.downtime_entries enable row level security;
