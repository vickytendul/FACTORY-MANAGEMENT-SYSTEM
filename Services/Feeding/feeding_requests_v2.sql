-- Feeding requests, second pass: the three handovers the floor actually
-- works to, and the clock they work against.
--
-- Run this in the Supabase SQL editor BEFORE the backend that reads it is
-- deployed. The editor runs the whole script as one transaction, so a
-- syntax error anywhere reverts all of it - which is what happened the
-- first time round. Every statement here is written to be safe to run
-- twice, so if it fails partway you can simply fix and run it again.
--
-- What changes, and why
-- ---------------------
-- The pins swapped owners. A pin is a receipt - the only way to learn it
-- is from the person who received the goods - so it has to be held by
-- whoever takes delivery, and the floor's actual sequence is:
--
--   line leader / IE raises            -> requester holds PIN A
--   cutting enters the sewing order    -> cutting  holds PIN B
--   store readies trims, asks cutting for PIN B, closes its stage
--   cutting asks the requester for PIN A, closes the whole request
--
-- So cutting_pin (which the requester held, and cutting typed) becomes
-- requester_pin and is now used at the END, and store_pin becomes
-- trims_pin and is now held by cutting rather than by the requester.
-- Renamed rather than added-and-dropped so no request in flight loses its
-- pin halfway through this.
--
-- Cutting no longer "closes" anything in the middle: entering the sewing
-- order is what hands the request to the store, so cutting_closed_at /
-- _by become sewing_order_at / _by, which is the same moment under its
-- real name.

-- 1. cutting_pin -> requester_pin
do $$
begin
    if exists (
            select 1 from information_schema.columns
             where table_schema = 'public' and table_name = 'feeding_requests'
               and column_name = 'cutting_pin')
       and not exists (
            select 1 from information_schema.columns
             where table_schema = 'public' and table_name = 'feeding_requests'
               and column_name = 'requester_pin')
    then
        alter table public.feeding_requests
            rename column cutting_pin to requester_pin;
    end if;
end $$;

-- 2. store_pin -> trims_pin
do $$
begin
    if exists (
            select 1 from information_schema.columns
             where table_schema = 'public' and table_name = 'feeding_requests'
               and column_name = 'store_pin')
       and not exists (
            select 1 from information_schema.columns
             where table_schema = 'public' and table_name = 'feeding_requests'
               and column_name = 'trims_pin')
    then
        alter table public.feeding_requests
            rename column store_pin to trims_pin;
    end if;
end $$;

-- 3. cutting_closed_at -> sewing_order_at
do $$
begin
    if exists (
            select 1 from information_schema.columns
             where table_schema = 'public' and table_name = 'feeding_requests'
               and column_name = 'cutting_closed_at')
       and not exists (
            select 1 from information_schema.columns
             where table_schema = 'public' and table_name = 'feeding_requests'
               and column_name = 'sewing_order_at')
    then
        alter table public.feeding_requests
            rename column cutting_closed_at to sewing_order_at;
    end if;
end $$;

-- 4. cutting_closed_by -> sewing_order_by
do $$
begin
    if exists (
            select 1 from information_schema.columns
             where table_schema = 'public' and table_name = 'feeding_requests'
               and column_name = 'cutting_closed_by')
       and not exists (
            select 1 from information_schema.columns
             where table_schema = 'public' and table_name = 'feeding_requests'
               and column_name = 'sewing_order_by')
    then
        alter table public.feeding_requests
            rename column cutting_closed_by to sewing_order_by;
    end if;
end $$;

-- 5. The new columns.
alter table public.feeding_requests
    -- Four or five digits, as cutting writes it. Text rather than a
    -- number so a leading zero survives, and deliberately NOT unique: it
    -- is not yet known whether one sewing order covers one request or
    -- several, and a unique index would decide that question by refusing
    -- the second one at the worst possible moment.
    add column if not exists sewing_order      text,

    -- The store saying it has seen the request. Separate from closing it,
    -- so a request that sat unread for forty minutes can be told apart
    -- from one the store picked up at once and that simply took time.
    add column if not exists store_ack_at      timestamptz,
    add column if not exists store_ack_by      text,

    -- Cutting's final close, against the requester's pin. The store's own
    -- close keeps store_closed_at / _by, which already exist.
    add column if not exists final_closed_at   timestamptz,
    add column if not exists final_closed_by   text;

-- 6. The fourth stage.
--
-- 'final' is the request after the store has delivered trims and before
-- cutting has taken the requester's pin: nobody is waiting on material
-- any more, one signature is outstanding. Without it, a request would
-- have to look closed while that signature was still missing.
alter table public.feeding_requests
    drop constraint if exists feeding_requests_stage_check;

alter table public.feeding_requests
    add constraint feeding_requests_stage_check
    check (stage in ('cutting', 'store', 'final', 'closed'));

-- 7. The queue cutting works from: everything still open, oldest first,
-- because the oldest is the one closest to running out of its four hours.
-- The existing feeding_requests_day index is ordered newest-first for the
-- day list and cannot serve this.
create index if not exists feeding_requests_open
    on public.feeding_requests (created_at)
    where stage <> 'closed';
