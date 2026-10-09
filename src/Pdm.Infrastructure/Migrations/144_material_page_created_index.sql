ALTER TABLE material_master
    ADD INDEX ix_material_master_page_created (is_archived, created_at DESC, material_code, id);
