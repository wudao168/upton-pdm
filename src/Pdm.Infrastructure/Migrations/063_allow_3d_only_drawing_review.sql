ALTER TABLE drawing_review_item
    MODIFY drawing_document_id BINARY(16) NULL,
    MODIFY drawing_version_id BINARY(16) NULL,
    MODIFY drawing_revision VARCHAR(20) NULL,
    MODIFY drawing_sha256 CHAR(64) NULL,
    MODIFY drawing_created_by VARCHAR(100) NULL;
