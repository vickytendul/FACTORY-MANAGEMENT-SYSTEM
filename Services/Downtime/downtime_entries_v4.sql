-- What the stoppage cost in pieces.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. Safe to run twice.
--
-- The minutes are already here, worked out from the two clock times, and
-- a piece figure could in principle be derived from them at the line's
-- SAM. It is asked for instead, because the derivation is wrong in the
-- cases that matter: half a line kept running, the operators were moved
-- to another style, the stoppage caught the line mid-changeover. The
-- person standing there when it happened knows what was actually lost,
-- and that is a different number from minutes divided by SAM.
--
-- Nullable, and null means nobody stated it - not nothing lost. A
-- stoppage recorded in a hurry at ten past ten, before anybody has
-- counted, is a real entry; filling it with a zero would put a figure
-- in a column that adds up, and it would add up wrongly.

alter table public.downtime_entries
    add column if not exists pieces_lost integer;

-- Refuses a negative outright, so the one check that cannot be argued
-- with lives next to the data rather than only in the API.
do $$
begin
    alter table public.downtime_entries
        add constraint downtime_entries_pieces_lost_nonneg
        check (pieces_lost is null or pieces_lost >= 0);
exception
    when duplicate_object then null;
end $$;
