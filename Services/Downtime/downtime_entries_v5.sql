-- The GM reading a stoppage and not accepting it.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. Safe to run twice.
--
-- Until now a stoppage had two states, and one of them was doing two
-- jobs: a null approved_at meant "nobody has read this yet" AND "the GM
-- read it and would not sign it". The report could only say WAITING to
-- both, so a decision that had been made looked like a queue, and the
-- person who wrote the entry never learned why it was refused - which
-- is the one thing that would have let them write a better one.
--
-- So: a third state, with the reason on it. rejected_note is the point
-- of the whole thing. A refusal with no reason is the same failure as a
-- stoppage with no reason, one level up.
--
-- Approved and rejected are mutually exclusive, enforced below rather
-- than left to the API: a row claiming to be both is a row every report
-- would count differently.

alter table public.downtime_entries
    add column if not exists rejected_at      timestamptz,
    add column if not exists rejected_by_code text,
    add column if not exists rejected_by_name text,
    add column if not exists rejected_note    text;

do $$
begin
    alter table public.downtime_entries
        add constraint downtime_entries_one_decision
        check (approved_at is null or rejected_at is null);
exception
    when duplicate_object then null;
end $$;

-- A refusal always carries its reason. The API refuses an empty one
-- too, but this is the half that cannot be bypassed by a direct write.
do $$
begin
    alter table public.downtime_entries
        add constraint downtime_entries_rejection_has_reason
        check (
            rejected_at is null
            or coalesce(btrim(rejected_note), '') <> ''
        );
exception
    when duplicate_object then null;
end $$;

-- The report opens asking which of today's are still waiting, and
-- waiting now means neither signed nor refused.
drop index if exists public.downtime_entries_unapproved;
create index if not exists downtime_entries_undecided
    on public.downtime_entries (entry_day)
    where approved_at is null and rejected_at is null;
