ALTER TABLE engineering_kit
    ADD COLUMN selection_advice VARCHAR(1000) NULL,
    ADD COLUMN reference_price DECIMAL(18,2) NULL,
    ADD COLUMN is_recommended TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN cover_image_attachment_id BINARY(16) NULL;

ALTER TABLE engineering_kit_attachment
    DROP CHECK ck_kit_attachment_kind,
    ADD CONSTRAINT ck_kit_attachment_kind CHECK (attachment_kind IN ('CoverImage','Model3D','Document'));
