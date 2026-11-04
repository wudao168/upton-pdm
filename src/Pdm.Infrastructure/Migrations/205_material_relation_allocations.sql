ALTER TABLE bom_item
    ADD COLUMN relation_allocations_json JSON NULL,
    ADD COLUMN engineering_kit_relation_allocations_json JSON NULL;
