ALTER TABLE bom_item
    ADD COLUMN impact_stage VARCHAR(32) NULL AFTER is_wear_part;

CREATE INDEX ix_bom_item_impact_stage ON bom_item(project_id, impact_stage);
