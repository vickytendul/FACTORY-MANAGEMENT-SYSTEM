-- One row per login, so the question "who is signed in, on what, right
-- now" has an answer.
--
-- Until this, the token was the whole of it: signed at login, handed
-- over, and forgotten by the server the moment it left. Nothing here
-- knew whether a person was still signed in, on how many devices, or on
-- which - and nothing could sign one of them out. The token carries the
-- id of its row from now on, and this is that row.
--
-- A session is live when ended_at is null and expires_at is still in the
-- future; it is at its screen when last_seen_at is within the idle
-- window the app uses. The two are different questions and both get
-- asked: a tablet left on a bench at the end of a line is signed in and
-- nobody is at it.
--
-- Run once in the Supabase SQL editor.

create table if not exists public.app_sessions (
    id            uuid        primary key,
    username      text        not null,
    display_name  text,
    role          text,

    -- What the device says it is. Taken from the client rather than
    -- guessed from the user agent, because the app knows whether it is
    -- a phone in a pocket or a board bolted to a wall and a parsed
    -- user agent only knows it is Chrome.
    device        text,
    platform      text,

    started_at    timestamptz not null default now(),
    last_seen_at  timestamptz not null default now(),
    expires_at    timestamptz not null,

    -- Set when the session stops being usable, whoever stopped it.
    -- ended_by is 'self' for signing out, or the username of whoever
    -- ended it from the devices list.
    ended_at      timestamptz,
    ended_by      text
);

create index if not exists app_sessions_username_idx
    on public.app_sessions (username);

create index if not exists app_sessions_last_seen_idx
    on public.app_sessions (last_seen_at desc);

-- The one the middleware asks on a timer: which live sessions have been
-- ended, so a device that was signed out from somewhere else stops
-- working on its next request.
create index if not exists app_sessions_live_idx
    on public.app_sessions (expires_at)
    where ended_at is null;

-- Old rows are history, not state. The application deletes anything a
-- month past its expiry; this is the same statement if it ever needs
-- running by hand.
--
--   delete from public.app_sessions where expires_at < now() - interval '30 days';
