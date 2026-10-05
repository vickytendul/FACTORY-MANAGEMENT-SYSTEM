-- The login accounts, moved off Firestore.
--
-- Run this in the Supabase SQL editor BEFORE setting Users__Source to
-- anything but firebase. Then call UserMigration/copy to fill it.
--
-- Identity is the username (the employee code). It was already the
-- Firestore document id, so there is no firebase_doc_id column here as
-- there is in the stores whose documents had generated ids.

create table if not exists public.app_users (
    username      text        primary key,
    display_name  text        not null default '',
    password_hash text        not null,
    role          text        not null default 'Supervisor',
    is_active     boolean     not null default true,
    created_on    timestamptz not null default now(),

    constraint app_users_role_check
        check (role in ('Admin', 'Supervisor', 'IE', 'Viewer'))
);

-- Login matches the username case-insensitively, because it is typed by
-- hand at a login box on a factory floor. Without this, 'A1234' and
-- 'a1234' could both be created and the login would pick whichever the
-- planner happened to return first.
create unique index if not exists app_users_username_lower
    on public.app_users (lower(username));

-- The API connects as the Postgres user in Supabase__ConnectionString,
-- which bypasses row level security. RLS is enabled anyway, with no
-- policy, so that the anon and authenticated keys - which Supabase
-- exposes to the public internet - cannot read the password hashes if
-- this project ever starts using them.
alter table public.app_users enable row level security;
