ALTER TABLE release_package
    MODIFY package_number VARCHAR(160) NOT NULL,
    MODIFY change_number VARCHAR(160) NULL;

ALTER TABLE bom_version
    MODIFY version_label VARCHAR(120) NOT NULL,
    MODIFY change_number VARCHAR(160) NULL;

ALTER TABLE manufacturing_bom_baseline
    MODIFY baseline_label VARCHAR(120) NOT NULL,
    MODIFY change_number VARCHAR(160) NOT NULL;

ALTER TABLE drawing_review_package
    MODIFY review_number VARCHAR(160) NOT NULL;
