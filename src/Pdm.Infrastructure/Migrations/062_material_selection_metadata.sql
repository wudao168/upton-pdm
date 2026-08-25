ALTER TABLE material_master
    ADD COLUMN selection_advice VARCHAR(1000) NULL AFTER purchase_link,
    ADD COLUMN reference_price DECIMAL(18,2) NULL AFTER selection_advice,
    ADD COLUMN model_3d_link VARCHAR(2048) NULL AFTER reference_price,
    ADD COLUMN document_link VARCHAR(2048) NULL AFTER model_3d_link,
    ADD COLUMN is_recommended TINYINT(1) NOT NULL DEFAULT 0 AFTER document_link;
