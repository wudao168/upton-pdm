ALTER TABLE release_package
    ADD COLUMN whole_set_multiplier INT NOT NULL DEFAULT 1 AFTER locks_documents;

ALTER TABLE bom_item
    ADD COLUMN is_release_excluded TINYINT(1) NOT NULL DEFAULT 0 AFTER is_manually_excluded,
    ADD COLUMN release_exclusion_reason VARCHAR(500) NULL AFTER is_release_excluded;

CREATE INDEX ix_bom_item_release_exclusion ON bom_item(project_id, is_release_excluded);
