ALTER TABLE document_version
    ADD COLUMN version_status VARCHAR(30) NOT NULL DEFAULT 'Work' AFTER revision_label,
    ADD COLUMN property_snapshot_json JSON NULL AFTER comment,
    ADD COLUMN reference_snapshot_json JSON NULL AFTER property_snapshot_json,
    ADD COLUMN mechanical_bom_snapshot_json JSON NULL AFTER reference_snapshot_json,
    ADD COLUMN electrical_bom_snapshot_json JSON NULL AFTER mechanical_bom_snapshot_json,
    ADD COLUMN source_version_id BINARY(16) NULL AFTER electrical_bom_snapshot_json,
    ADD COLUMN source_description VARCHAR(500) NULL AFTER source_version_id,
    ADD COLUMN approval_task_id BINARY(16) NULL AFTER source_description,
    ADD COLUMN release_package_id BINARY(16) NULL AFTER approval_task_id,
    ADD CONSTRAINT fk_document_version_source FOREIGN KEY (source_version_id) REFERENCES document_version(id),
    ADD CONSTRAINT fk_document_version_approval FOREIGN KEY (approval_task_id) REFERENCES approval_task(id),
    ADD CONSTRAINT fk_document_version_release_package FOREIGN KEY (release_package_id) REFERENCES release_package(id),
    ADD KEY ix_document_version_created (document_id, created_at),
    ADD KEY ix_document_version_source (source_version_id);

UPDATE document_version
SET property_snapshot_json = JSON_OBJECT(),
    reference_snapshot_json = JSON_OBJECT(
        'nodeId', '00000000-0000-0000-0000-000000000000',
        'documentId', NULL,
        'instancePath', '',
        'fileName', '',
        'displayName', '',
        'kind', 5,
        'revision', NULL,
        'configuration', '',
        'quantity', 1,
        'status', 2,
        'isSuppressed', FALSE,
        'children', JSON_ARRAY()),
    mechanical_bom_snapshot_json = JSON_ARRAY(),
    electrical_bom_snapshot_json = JSON_ARRAY()
WHERE property_snapshot_json IS NULL;

ALTER TABLE document_version
    MODIFY property_snapshot_json JSON NOT NULL,
    MODIFY reference_snapshot_json JSON NOT NULL,
    MODIFY mechanical_bom_snapshot_json JSON NOT NULL,
    MODIFY electrical_bom_snapshot_json JSON NOT NULL;

CREATE TRIGGER document_version_immutable_update
BEFORE UPDATE ON document_version
FOR EACH ROW
SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Document versions are immutable';

CREATE TRIGGER document_version_immutable_delete
BEFORE DELETE ON document_version
FOR EACH ROW
SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Document versions cannot be deleted';
