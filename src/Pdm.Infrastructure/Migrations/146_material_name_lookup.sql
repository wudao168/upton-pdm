ALTER TABLE material_master
    ADD COLUMN name_lookup VARCHAR(500) GENERATED ALWAYS AS (UPPER(TRIM(name))) VIRTUAL,
    ADD INDEX ix_material_name_lookup (name_lookup, approval_status, is_archived);
