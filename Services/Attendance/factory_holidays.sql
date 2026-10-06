-- The days the factory does not run.
--
-- Sundays are known without being told. A declared holiday is not: on
-- 02 Oct 2026, Gandhi Jayanti, payroll marked 822 people AB and 27 P -
-- the twenty-seven being security and maintenance - and there is no
-- holiday code in the response to tell it apart from a day everybody
-- happened to stay home. Inference cannot find it, so it is recorded.
--
-- A date here is left out of the comparison columns and out of every
-- average, exactly as a Sunday is. The figures for it are not deleted:
-- ask for that date directly and it still answers.

create table if not exists public.factory_holidays (
    holiday_date date not null primary key,
    name         text not null default '',
    created_on   timestamptz not null default now()
);

alter table public.factory_holidays enable row level security;

-- The one this was written for.
insert into public.factory_holidays (holiday_date, name)
values ('2026-10-02', 'Gandhi Jayanti')
on conflict (holiday_date) do nothing;
