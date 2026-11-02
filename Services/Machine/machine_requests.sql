-- Machine requests: a line saying a machine has stopped, and the two
-- handovers that answer it - maintenance attending, then maintenance
-- closing against the line's own pin.
--
-- Run this in the Supabase SQL editor BEFORE deploying the page. Safe to
-- run twice. New data with no Firestore history, so there is no source
-- flag and nothing to migrate.
--
-- Built beside feeding_requests on purpose and in the same shape: the
-- floor already knows that flow, and a second one that worked
-- differently would be a second set of habits to teach.
--
-- One pin, not two, because there is only one handover that matters. The
-- line leader is given it when he raises the request and maintenance has
-- to be told it to close - so closing proves the machine is running
-- again, because the only way to learn the pin is from the person
-- standing at the machine. A mechanic cannot mark his own work finished.

create table if not exists public.machine_requests (
    id                bigint generated always as identity primary key,

    -- What the floor calls it out loud. Sequential within a day, so
    -- "today's number four" identifies it across a noisy room.
    request_no        text        not null,
    created_at        timestamptz not null default now(),

    line_id           integer     not null,
    line_name         text        not null default '',

    -- Optional, and deliberately present from the first day.
    --
    -- Nothing reads it yet. It is here because the question it answers -
    -- which machines keep stopping - can only ever be asked of data
    -- already collected, and a column added the month somebody wants
    -- that report starts empty.
    machine_no        text,

    -- What is wrong, in the words of whoever saw it stop.
    problem           text        not null,

    raised_by_code    text        not null default '',
    -- The name as the roster had it at that moment, so an old request
    -- still reads correctly after somebody leaves.
    raised_by_name    text        not null default '',

    -- Four digits, kept as text so a leading zero survives. Readable
    -- rather than hashed: the line has to say it out loud an hour later,
    -- and a hash would make it a thing they must write down and not
    -- lose. Only ever shown to the line that raised the request.
    line_pin          text        not null,

    stage             text        not null default 'raised',

    -- Maintenance at the machine. The ten-minute target is measured to
    -- here and nowhere else.
    attended_at       timestamptz,
    attended_by       text,

    -- Finished, against the line's pin.
    closed_at         timestamptz,
    closed_by         text,

    -- What was actually wrong, and what was done about it. The part of
    -- the record that cannot be reconstructed later: the line, the
    -- machine and the minutes will all still be countable from this
    -- table long after everybody has forgotten the morning.
    fault             text,
    action            text,

    -- What the stoppage cost in garments. Optional, and null means
    -- nobody has said rather than nothing lost - the same rule the down
    -- time entries use.
    pieces_lost       integer,

    -- Raised and then not needed. A reason is required, for the same
    -- reason the GM's refusal of a stoppage requires one: without it the
    -- line learns nothing and calls again tomorrow.
    cancelled_at      timestamptz,
    cancelled_by      text,
    cancel_note       text,

    -- Wrong pins since the last correct one. Four digits is ten thousand
    -- guesses, which is an afternoon for somebody who wants a job marked
    -- finished without fixing anything.
    wrong_tries       integer     not null default 0,

    -- The factory day this belongs to, as a plain date. Asia/Kolkata and
    -- not the server's zone, which is UTC on Render: a machine that
    -- stopped at nine at night here would otherwise land on tomorrow.
    request_day       date        not null
        default ((now() at time zone 'Asia/Kolkata')::date),

    constraint machine_requests_stage_check
        check (stage in ('raised', 'attended', 'closed', 'cancelled')),

    -- A cancellation always carries its reason.
    constraint machine_requests_cancel_has_reason
        check (
            cancelled_at is null
            or coalesce(btrim(cancel_note), '') <> ''
        ),

    constraint machine_requests_pieces_nonneg
        check (pieces_lost is null or pieces_lost >= 0)
);

-- The list is always one day, newest first.
create index if not exists machine_requests_day
    on public.machine_requests (request_day, created_at desc);

-- What the floor opens the page to see: what is still standing.
create index if not exists machine_requests_open
    on public.machine_requests (request_day)
    where stage in ('raised', 'attended');

-- And a line's own machine time, for the day a report is showing.
create index if not exists machine_requests_line
    on public.machine_requests (request_day, line_id);

-- Request numbers restart each day, so they are unique within one rather
-- than across all time. This is also what stops two lines pressing Save
-- together from both calling themselves 4: the second insert collides
-- and is retried.
create unique index if not exists machine_requests_no_per_day
    on public.machine_requests (request_day, request_no);

-- The API connects as the Postgres user in Supabase__ConnectionString,
-- which bypasses row level security. RLS is enabled anyway, with no
-- policy, so the anon and authenticated keys Supabase exposes to the
-- public internet cannot read the pins if this project ever uses them.
alter table public.machine_requests enable row level security;
