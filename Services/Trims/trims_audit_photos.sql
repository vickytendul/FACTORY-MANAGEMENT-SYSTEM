-- Photographs against an audit checkpoint.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. Safe to run twice.
--
-- The bytes live here rather than in Supabase Storage, and that is a
-- choice worth writing down. Storage would need a bucket, a service key
-- in Render's environment and a second thing that can be down; this
-- backend holds a Postgres connection and nothing else, and a feature
-- that adds a new secret and a new failure mode to save a column is a
-- poor trade at this size.
--
-- Size is what makes it a fair one. The app resizes to 1024px and
-- re-encodes before it uploads, so a photograph arrives at roughly a
-- hundred kilobytes rather than the four megabytes a phone took. A
-- handful per audit, twice a week, is something like fifty megabytes a
-- year. If that stops being true - someone starts photographing every
-- rack - this is the table to move out, and nothing above it changes.

create table if not exists public.trims_audit_photos (
    id           bigint generated always as identity primary key,
    audit_id     bigint      not null
        references public.trims_audits (id) on delete cascade,

    -- Which checkpoint it belongs to. Not a foreign key to the answer
    -- row: a photograph can be taken before the OK or NOT OK is
    -- pressed, and losing it because the answer had not been given yet
    -- would be the worst moment to lose it.
    item_key     text        not null,

    content_type text        not null default 'image/jpeg',
    bytes        bytea       not null,
    size_bytes   integer     not null,
    width        integer,
    height       integer,

    uploaded_at  timestamptz not null default now(),
    uploaded_by  text        not null default '',

    -- A megabyte and a half after the app has already resized. This is
    -- not the expected size; it is the catch for a client that did not
    -- compress, so one phone cannot put a hundred megabytes in the
    -- database by itself.
    constraint trims_audit_photos_size_check
        check (size_bytes > 0 and size_bytes <= 1572864)
);

-- Every read asks the same question: what is on this audit, by
-- checkpoint.
create index if not exists trims_audit_photos_audit
    on public.trims_audit_photos (audit_id, item_key);

alter table public.trims_audit_photos enable row level security;
