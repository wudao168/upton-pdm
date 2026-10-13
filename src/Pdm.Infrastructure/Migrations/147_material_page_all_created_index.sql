-- Includes archived items without scanning and sorting the complete material directory.
ALTER TABLE material_master
    ADD INDEX ix_material_master_all_created (created_at DESC, material_code, id);
