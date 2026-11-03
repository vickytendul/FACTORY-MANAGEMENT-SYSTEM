-- The trims store's online-control audit: GUT 2-SEW-91 on a screen.
--
-- Run this in the Supabase SQL editor BEFORE deploying the page. Safe to
-- run twice. New data with no Firestore history, so there is no source
-- flag and nothing to migrate.
--
-- One audit per day, for one trims store. The factory has one, so there
-- is nothing to key on but the day - and the unique index on it is what
-- stops two people walking the floor at the same time and ending with
-- two half-filled audits nobody can reconcile.

create table if not exists public.trims_audits (
    id               bigint generated always as identity primary key,
    created_at       timestamptz not null default now(),

    -- The factory day, Asia/Kolkata. An audit started at nine at night
    -- belongs to that night, not to tomorrow.
    audit_day        date        not null
        default ((now() at time zone 'Asia/Kolkata')::date),

    started_by_code  text        not null default '',
    started_by_name  text        not null default '',

    -- Null while it is still being walked. An audit is only finished
    -- when every row has an answer, and until then its score is a
    -- score-so-far rather than a result.
    submitted_at     timestamptz,
    submitted_by_code text,
    submitted_by_name text,

    -- The revision of the printed form these answers were given
    -- against. Kept on the audit so a checklist reworded next year
    -- does not silently restate what somebody signed this year.
    form_rev         text        not null default '00',

    constraint trims_audits_one_per_day unique (audit_day)
);

-- The list is read newest first, and the status strip asks for the most
-- recent submitted one.
create index if not exists trims_audits_day
    on public.trims_audits (audit_day desc);

-- One row per checkpoint answered.
--
-- Rows only for answers. A checkpoint nobody reached has NO row, which
-- is what tells "not checked" apart from "checked and fine" - the gap
-- the paper form could not express, where a blank box and a tick that
-- somebody forgot look identical.
create table if not exists public.trims_audit_items (
    id            bigint generated always as identity primary key,
    audit_id      bigint      not null
        references public.trims_audits (id) on delete cascade,

    -- "1.2" - section two-dot-item. Stable across rewording, which the
    -- labels are not: this is a controlled document and its text WILL
    -- be revised.
    item_key      text        not null,

    -- The section and the label as they read on the day this was
    -- answered, so an old audit still reads correctly after the form is
    -- revised. The same reason a down time entry keeps the line's name.
    section_no    integer     not null,
    section_name  text        not null default '',
    item_no       integer     not null,
    item_label    text        not null default '',

    -- 'ok' or 'not_ok'. No third value: "not checked" is the absence of
    -- the row, not a value on it.
    result        text        not null,

    -- Why it is not OK, in the auditor's own words. The whole point of
    -- the record - a cross with no sentence beside it tells the next
    -- audit nothing, and the same cross comes back on Thursday.
    reason        text,

    answered_at   timestamptz not null default now(),
    answered_by   text        not null default '',

    constraint trims_audit_items_result_check
        check (result in ('ok', 'not_ok')),

    -- A refusal always carries its reason.
    constraint trims_audit_items_reason_required
        check (
            result <> 'not_ok'
            or coalesce(btrim(reason), '') <> ''
        ),

    constraint trims_audit_items_one_per_item unique (audit_id, item_key)
);

create index if not exists trims_audit_items_audit
    on public.trims_audit_items (audit_id);

-- What the history page opens asking: which checkpoints keep coming
-- back not OK.
create index if not exists trims_audit_items_not_ok
    on public.trims_audit_items (item_key, answered_at desc)
    where result = 'not_ok';

-- The API connects as the Postgres user in Supabase__ConnectionString,
-- which bypasses row level security. RLS is enabled anyway, with no
-- policy, so the anon and authenticated keys Supabase exposes to the
-- public internet cannot read this if the project ever uses them.
alter table public.trims_audits enable row level security;
alter table public.trims_audit_items enable row level security;
