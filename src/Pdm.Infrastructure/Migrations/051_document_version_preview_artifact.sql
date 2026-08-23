ALTER TABLE document_version
    ADD COLUMN preview_format VARCHAR(10) NULL AFTER release_package_id,
    ADD COLUMN preview_storage_relative_path VARCHAR(1000) NULL AFTER preview_format,
    ADD COLUMN preview_file_length BIGINT NULL AFTER preview_storage_relative_path,
    ADD COLUMN preview_sha256 CHAR(64) NULL AFTER preview_file_length,
    ADD COLUMN preview_source_sha256 CHAR(64) NULL AFTER preview_sha256;

ALTER TABLE document_version
    ADD CONSTRAINT chk_document_version_preview_complete CHECK (
        (preview_format IS NULL
            AND preview_storage_relative_path IS NULL
            AND preview_file_length IS NULL
            AND preview_sha256 IS NULL
            AND preview_source_sha256 IS NULL)
        OR
        (preview_format IN ('Step','Pdf')
            AND preview_storage_relative_path IS NOT NULL
            AND preview_file_length > 0
            AND preview_sha256 IS NOT NULL
            AND preview_source_sha256 IS NOT NULL)
    );
