ALTER TABLE bom_item
    ADD COLUMN is_wear_part TINYINT(1) NOT NULL DEFAULT 0 AFTER weight;

CREATE INDEX ix_bom_item_wear_part ON bom_item(project_id, is_wear_part);
