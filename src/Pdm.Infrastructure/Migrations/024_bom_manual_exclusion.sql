ALTER TABLE bom_item
    ADD COLUMN is_manually_excluded TINYINT(1) NOT NULL DEFAULT 0 AFTER is_manually_retained;
