-- The day's entry for a line, of which the plan is now one field.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- reads it. The editor runs the whole script as one transaction, so a
-- syntax error anywhere reverts all of it. Safe to run twice.
--
-- ── what changes ────────────────────────────────────────────────────
--
-- A second figure is typed beside the plan on the Down Time Entry
-- screen: how many days this line has run. It is typed rather than
-- counted because the floor's answer and the attendance system's answer
-- are not always the same one, and the sheet is read against the
-- floor's.
--
-- It lives here rather than in a table of its own because it is entered
-- in the same column of boxes, by the same person, with the same one
-- Save - and a second table would mean a second round trip that can
-- fail on its own and leave half the morning's entry behind.
--
-- ── and why target stops being NOT NULL ─────────────────────────────
--
-- The row is no longer "the plan". It is what was entered for this line
-- on this day, and either field can be filled without the other: a line
-- whose day count is being corrected need not have a plan set, and a
-- plan can be set before anybody knows the count.
--
-- Nullable and not zero-defaulted, for the reason the original file
-- already gives about the plan: zero is a real figure on a line that is
-- not running, so "nothing entered" has to be sayable as something
-- else. The same is true of the day count.

alter table public.line_targets
    alter column target drop not null;

alter table public.line_targets
    add column if not exists no_of_days integer;

comment on column public.line_targets.no_of_days is
    'Days this line has run, as the floor counts them. Typed, not derived. '
    'Null means nothing was entered, which is not the same as nought.';
