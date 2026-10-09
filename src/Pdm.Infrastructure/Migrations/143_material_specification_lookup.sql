ALTER TABLE material_master
    ADD COLUMN specification_lookup VARCHAR(500) GENERATED ALWAYS AS (UPPER(TRIM(specification))) VIRTUAL,
    ADD INDEX ix_material_specification_lookup (specification_lookup, approval_status, is_archived);
