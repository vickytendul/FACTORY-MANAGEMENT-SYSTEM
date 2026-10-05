-- The employee master, moved off Firestore.
--
-- Run this in the Supabase SQL editor BEFORE setting Employees__Source
-- to supabase. Then call SupabaseMigration/employees/copy to fill it.
--
-- Grade is the reason this table matters. The Company API carries the
-- roster - code, name, department, designation, barcode - but no grade,
-- and grade is what Layout Allocation matches an operator to an
-- operation with. The roster can be re-fetched from the vendor at any
-- time; the grade cannot, because it is only ever entered in this app.

create table if not exists public.employee_masters (
    employee_id      integer     not null,
    employee_code    text        primary key,
    employee_barcode text        not null default '',
    employee_name    text        not null default '',
    grade            text        not null default '',
    designation      text,
    department       text,
    is_active        boolean     not null default true,
    unit             text,
    sex              text,
    contact          text,
    experience       double precision,
    date_of_releave  text,
    reason           text
);

-- The roster is read whole and filtered in memory, so there is little to
-- index for that. These two are for the single-employee lookups: by code
-- (the primary key already covers it) and by the barcode a scanner sends.
create index if not exists employee_masters_barcode
    on public.employee_masters (employee_barcode)
    where employee_barcode <> '';

-- Only the active roster is wanted most of the time.
create index if not exists employee_masters_active
    on public.employee_masters (is_active);

-- The API connects as the Postgres user in Supabase__ConnectionString,
-- which bypasses row level security. RLS is enabled anyway, with no
-- policy, so the anon and authenticated keys - which Supabase exposes to
-- the public internet - cannot read this roster if the project ever
-- starts using them.
alter table public.employee_masters enable row level security;
