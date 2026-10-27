-- What each line is meant to make today.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. The editor runs the whole script as one transaction, so a
-- syntax error anywhere reverts all of it. Safe to run twice.
--
-- One row per line per day, and the unique constraint is what makes
-- that true rather than a thing people have to be careful about: the
-- plan gets set in the morning and corrected twice before lunch, and
-- every correction has to replace the figure rather than sit beside it.
-- The API upserts on that constraint.

create table if not exists public.line_targets (
    id              bigint generated always as identity primary key,

    -- The factory day this plan is for, as a plain date.
    --
    -- Asia/Kolkata and not the server's zone, which is UTC on Render: a
    -- plan set at nine at night here would otherwise be tomorrow's.
    target_day      date        not null
        default ((now() at time zone 'Asia/Kolkata')::date),

    line_id         integer     not null,
    -- The name as it was when the plan was set, so an old plan still
    -- reads correctly after a line is renamed.
    line_name       text        not null default '',

    -- Pieces. Not minutes and not a percentage - the floor plans in
    -- garments, and every other figure on these screens is derived from
    -- a piece count rather than the other way round.
    target          integer     not null,

    entered_by_code text        not null default '',
    entered_by_name text        not null default '',

    -- When it was last set, which is not when the row was created: a
    -- plan corrected at eleven is a different fact from one set at nine
    -- and left alone.
    updated_at      timestamptz not null default now(),

    constraint line_targets_once_per_day unique (target_day, line_id)
);

-- The page is always one day.
create index if not exists line_targets_day
    on public.line_targets (target_day);

-- The API connects as the Postgres user in Supabase__ConnectionString,
-- which bypasses row level security. RLS is enabled anyway, with no
-- policy, so the anon and authenticated keys Supabase exposes to the
-- public internet cannot read this if the project ever uses them.
alter table public.line_targets enable row level security;
