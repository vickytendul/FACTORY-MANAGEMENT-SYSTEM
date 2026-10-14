-- Feeding requests: a sewing line asking to be fed, and the two
-- handovers that answer it - cutting first, then the store.
--
-- Run this in the Supabase SQL editor before deploying the page. New
-- data with no Firestore history, so there is no source flag and nothing
-- to migrate: it has never lived anywhere else.

create table if not exists public.feeding_requests (
    id                  bigint generated always as identity primary key,
    request_no          text        not null,
    created_at          timestamptz not null default now(),

    line_id             integer     not null,
    line_name           text        not null default '',
    cc_id               integer     not null,
    cc_no               text        not null default '',

    requested_by_code   text        not null,
    -- The name as the roster had it when the request was raised, so the
    -- request still reads correctly after somebody leaves and their code
    -- stops resolving.
    requested_by_name   text        not null default '',

    -- Both genuinely optional. NULL is "did not say", which is not the
    -- same as zero.
    size                text,
    quantity            integer,

    stage               text        not null default 'cutting',

    -- Four digits each, kept as text so a leading zero survives. Readable
    -- rather than hashed: the line that raised the request has to say it
    -- out loud hours later, and a hash would make it a thing they must
    -- write down and not lose. Only ever sent back to that line - cutting
    -- and the store type it in and are never shown it.
    cutting_pin         text        not null,
    -- Empty until cutting closes. Generating it up front would mean the
    -- store's pin could be read, and told to somebody, before cutting had
    -- delivered anything.
    store_pin           text        not null default '',

    cutting_closed_at   timestamptz,
    cutting_closed_by   text,
    store_closed_at     timestamptz,
    store_closed_by     text,

    -- Wrong pins since the last correct one. Four digits is ten thousand
    -- guesses; without this, a supplier who wanted a request closed
    -- without delivering could sit and work through them.
    wrong_tries         integer     not null default 0,

    constraint feeding_requests_stage_check
        check (stage in ('cutting', 'store', 'closed'))
);

-- The list is always "today", newest first.
create index if not exists feeding_requests_created_at
    on public.feeding_requests (created_at desc);

-- Request numbers restart each day, so they are unique within one rather
-- than across all time.
create unique index if not exists feeding_requests_no_per_day
    on public.feeding_requests ((created_at at time zone 'Asia/Kolkata')::date, request_no);

-- The API connects as the Postgres user in Supabase__ConnectionString,
-- which bypasses row level security. RLS is enabled anyway, with no
-- policy, so the anon and authenticated keys Supabase exposes to the
-- public internet cannot read the pins if this project ever uses them.
alter table public.feeding_requests enable row level security;
