ALTER TABLE engineering_kit
    ADD COLUMN kit_model VARCHAR(20) NULL AFTER kit_code,
    ADD COLUMN brand VARCHAR(160) NOT NULL DEFAULT '' AFTER name;

UPDATE engineering_kit
SET kit_model = kit_code
WHERE kit_code IS NOT NULL;

UPDATE engineering_kit_component
SET is_optional = 0
WHERE is_optional <> 0;
