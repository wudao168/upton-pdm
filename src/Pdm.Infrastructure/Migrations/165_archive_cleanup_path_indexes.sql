-- Normalize legacy Windows/Linux separators without scanning every version per upload.
-- Virtual columns do not rewrite CAD files or change version identities.
ALTER TABLE document_version
    ADD COLUMN cleanup_storage_path VARCHAR(1024) GENERATED ALWAYS AS (REPLACE(storage_relative_path,CHAR(92),'/')) VIRTUAL,
    ADD COLUMN cleanup_preview_path VARCHAR(1024) GENERATED ALWAYS AS (REPLACE(preview_storage_relative_path,CHAR(92),'/')) VIRTUAL;
CREATE INDEX ix_version_cleanup_storage ON document_version(cleanup_storage_path(700));
CREATE INDEX ix_version_cleanup_preview ON document_version(cleanup_preview_path(700));
