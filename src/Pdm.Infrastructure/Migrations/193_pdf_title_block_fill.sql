ALTER TABLE release_package
    ADD COLUMN pdf_title_block_snapshot_json JSON NULL,
    ADD COLUMN pdf_delivery_artifacts_json JSON NULL;
