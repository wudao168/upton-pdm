ALTER TABLE material_master
    ADD COLUMN source_system VARCHAR(20) NOT NULL DEFAULT 'Pdm' AFTER u9_sync_confirmed,
    ADD COLUMN master_owner VARCHAR(20) NOT NULL DEFAULT 'Pdm' AFTER source_system,
    ADD COLUMN last_u9_synced_at DATETIME(6) NULL AFTER master_owner,
    ADD KEY ix_material_master_source_owner (source_system,master_owner,last_u9_synced_at);
