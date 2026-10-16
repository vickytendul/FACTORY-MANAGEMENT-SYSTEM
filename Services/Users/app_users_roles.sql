-- Roles are worked out from where somebody actually works, so the set
-- of them is no longer Admin/Supervisor/IE/Viewer.
--
-- Run this before deploying the access-tier change. The old names stay
-- accepted: the four accounts that exist today carry them, and a
-- constraint that rejected them would lock those people out at the
-- moment this is applied.

alter table public.app_users
    drop constraint if exists app_users_role_check;

alter table public.app_users
    add constraint app_users_role_check
    check (role in (
        -- Worked out from the floor, at every login.
        'Admin',       -- GM-Operations, Factory Manager, AGM. Also User Master.
        'Manager',     -- every other manager, and the admin office
        'Production',  -- sewing section, line leaders, supervisors
        'Cutting',     -- the cutting section, by work detail and not by payroll
        'Store',
        'IE',
        'HR',          -- Strength Summary, which is a headcount
        'Quality',
        'None',        -- works here, has no business in this application

        -- Carried by the accounts that existed before this, so applying
        -- it does not shut anybody out mid-shift. A login recomputes the
        -- role, so they convert themselves the next time each person
        -- signs in.
        'Supervisor',
        'Viewer'
    ));
