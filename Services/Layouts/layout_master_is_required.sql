-- An operation the floor has decided not to run.
--
-- The row stays on the layout - it belongs to the style, and deleting it
-- is both wrong and dangerous: a layout save pairs request row i with
-- existing row i, and an allocation binds a person to the row's id, so
-- removing a row from the middle slides every operation below it onto
-- somebody else's id. This takes the row out of the required count
-- instead, and nothing moves.
--
-- Defaults to true, so every row that already exists goes on being
-- required and no line's percentage changes on the day this is applied.

alter table public.layout_masters
    add column if not exists is_required boolean not null default true;

-- Proof it applied, and that nothing was quietly reclassified: every
-- existing row should come back required.
--   select is_required, count(*) from public.layout_masters group by 1;
