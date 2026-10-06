-- Payroll's attendance, kept.
--
-- The vendor's Employee_Att only answers for a range that INCLUDES
-- today. Ask it for a past date on its own and it returns an empty
-- array, so the same report run a day later falls back to whatever
-- supervisors marked in this app - which is how 05 Oct read 574 present
-- when it was today and 498 present the morning after.
--
-- So every time payroll's answer is in hand, it is written here, and a
-- past day is read back from here rather than asked for again.

create table if not exists public.payroll_attendance (
    attendance_date date        not null,
    employee_code   text        not null,
    status          text        not null,
    captured_at     timestamptz not null default now(),

    primary key (attendance_date, employee_code)
);

-- Every read is "one day, or a range of days, all of it" - the report
-- wants the whole floor for a date, never one person across dates.
create index if not exists payroll_attendance_date
    on public.payroll_attendance (attendance_date);

alter table public.payroll_attendance enable row level security;
