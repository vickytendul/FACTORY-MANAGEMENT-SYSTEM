-- Maintenance, as a role of its own.
--
-- Run this in the Supabase SQL editor BEFORE deploying the backend that
-- assigns it. Safe to run twice.
--
-- The mechanics answer the machine requests, and until now the floor had
-- nowhere to put them: the role is worked out from the payroll
-- department at every login, and MAINTENANCE matched nothing, so four
-- mechanics and the needle issue landed on None while INCHARGE -
-- MAINTENANCE landed on Production - caught by the rule that takes
-- anything with INCHARGE in its designation.
--
-- A login recomputes the role, so the six of them convert themselves the
-- next time each one signs in. Nothing has to be edited by hand.

alter table public.app_users
    drop constraint if exists app_users_role_check;

alter table public.app_users
    add constraint app_users_role_check
    check (role in (
        -- Worked out from the floor, at every login.
        'Admin',        -- GM-Operations, Factory Manager, AGM. Also User Master.
        'Manager',      -- every other manager, and the admin office
        'Production',   -- sewing section, line leaders, supervisors
        'Cutting',      -- the cutting section, by work detail and not by payroll
        'Store',
        'IE',
        'HR',           -- Strength Summary, which is a headcount
        'Quality',
        'Maintenance',  -- the mechanics, who answer the machine requests
        'None',         -- works here, has no business in this application

        -- Carried by the accounts that existed before the tiers, so
        -- applying this does not shut anybody out mid-shift.
        'Supervisor',
        'Viewer'
    ));
