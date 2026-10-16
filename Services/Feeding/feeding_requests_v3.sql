-- Feeding requests, third pass: why a request went past its four hours.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. Safe to run twice.
--
-- A request closed inside its four hours needs no explanation. One
-- closed outside them does, and the only moment anybody actually knows
-- the reason is the moment of closing - an hour later it is a number in
-- a report that nobody can account for. So cutting is asked then, and
-- cannot close a late request without answering.
--
-- Nullable, because most closes are on time and an empty string is not a
-- better record of "nothing to explain" than no row value at all.

alter table public.feeding_requests
    add column if not exists close_remarks text;
