ALTER TABLE standard_library_category ADD COLUMN code VARCHAR(32) NULL,
    ADD UNIQUE KEY ux_standard_library_category_code(code);

ALTER TABLE engineering_kit
    MODIFY COLUMN kit_model VARCHAR(160) NULL,
    ADD COLUMN standard_library_category_id BINARY(16) NULL,
    ADD CONSTRAINT fk_engineering_kit_standard_category FOREIGN KEY(standard_library_category_id) REFERENCES standard_library_category(id);

ALTER TABLE engineering_kit_revision
    ADD COLUMN standard_library_category_id BINARY(16) NULL,
    ADD CONSTRAINT fk_engineering_kit_revision_standard_category FOREIGN KEY(standard_library_category_id) REFERENCES standard_library_category(id);

-- 历史套件没有选过标准分类，保留型号与版本，交由标准化归类。
UPDATE engineering_kit SET standard_library_category_id=UUID_TO_BIN('53544c42-0000-0000-0000-000000000001')
WHERE current_released_revision_id IS NOT NULL AND standard_library_category_id IS NULL;
