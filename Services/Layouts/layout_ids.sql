-- The layout id allocators, moved off Firestore.
--
-- Run this BEFORE setting LayoutIds__Source to supabase, then call
-- SupabaseMigration/layout-ids/copy, which seeds both counters from
-- Firestore's and copies the operation lookup across.
--
-- Read the warning on ILayoutIdAllocator first. Once ids have been
-- allocated here, going back to Firestore means re-seeding ITS counters
-- above whatever these have reached - otherwise it re-issues ids that
-- are already in use, which has happened on this system once before.

-- LayoutMaster ids. A table rather than a sequence because the allocator
-- needs the self-healing floor: when the counter has fallen behind the
-- rows that exist, it catches up instead of handing out a duplicate, and
-- a sequence has no way to be told that.
create table if not exists public.layout_counters (
    name  text    primary key,
    value integer not null default 0
);

-- OperationIds. Starts at 1000, as the Firestore counter did, but the
-- copy step raises it to Firestore's real value before anything is
-- allocated from it.
create sequence if not exists public.operation_id_seq
    as integer
    start with 1000
    minvalue 1000;

-- The identity of an operation to its stable id. The key is built by the
-- allocator as ccId_operation_machine_grade_section, with underscores
-- and slashes in the parts replaced by hyphens - the same string that
-- was the Firestore document id, so the rows copy across unchanged.
create table if not exists public.operation_id_lookup (
    lookup_key      text        primary key,
    operation_id    integer     not null unique,
    last_updated_on timestamptz not null default now()
);

alter table public.layout_counters     enable row level security;
alter table public.operation_id_lookup enable row level security;
